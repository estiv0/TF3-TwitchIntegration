// Ties together the data (DataFeed), the map (MapView) and the panel (Sidebar).

import { config } from "../config.js";
import { DataFeed } from "./api.js";
import {
  carrierIcon, carrierName, cargoIcon, cargoLabel, cargoName, escapeHtml as esc, fmtAge, fmtDate, fmtSpeed, vehicleImgHtml, fmtInt, fmtMoney, fmtPercent,
  fmtSignedPercent, iconHtml, lineWaitingAt, linesByIndustry, linesByStation, rgb, stationImg, stationName, splitAllOwners, attachEffects, stationUse, townGrowth, townSize,
} from "./format.js";
import { balanceSparkline } from "./company.js";
import { readSelection, writeSelection } from "./hash.js";
import { LANGS, applyStatic, lang, locale, onLangChange, setLang, t } from "./i18n.js";
import { MapView } from "./map.js";
import { PinController, readPinHash } from "./pin.js";
import { BuildController } from "./build.js";
import { FloatingWindows } from "./float.js";
import { StreamPlayer } from "./stream.js";
import { initSidebarResize } from "./resize.js";
import { Sidebar, townSupply } from "./sidebar.js";

const $ = (sel) => document.querySelector(sel);
const tooltip = $("#tooltip");
const status = $("#status");

let lastMeta = null;
let lastDynamic = null;
let loadError = false;
let pollError = null;                   // an error downloading one of the files while meta.json works
let pendingSelection = readSelection(); // the selection from a link - applied once the data arrives
// A chat link to a freshly built town / industry may arrive before the object is in the export:
// we wait this many more data batches before deciding it isn't there.
const PENDING_SELECTION_TRIES = 6;
let pendingMisses = 0;

applyStatic();

const map = new MapView($("#map"), {
  onLayers() {
    $("#stations").setAttribute("aria-pressed", String(map.showStations));
    $("#industries").setAttribute("aria-pressed", String(map.showIndustries));
    $("#warehouses").setAttribute("aria-pressed", String(map.showWarehouses));
  },
  onHover(item, x, y) {
    if (!item) {
      tooltip.hidden = true;
      return;
    }
    tooltip.innerHTML = tooltipHtml(item);
    tooltip.hidden = false;
    tooltip.style.transform = `translate(${x + 14}px, ${y + 14}px)`;
  },
  onSelect(item) {
    sidebar.setSelected(item?.kind, item?.id);
    writeSelection(item);
  },
  onPlace(place) {
    if (place.mode === "build") build.place(place);
    else pins.place(place);
  },
  onPlaceMode(mode) {
    pins.modeChanged(mode === "pin");
    build.modeChanged(mode === "build");
  },
  onVisibleLines(ids) {
    sidebar.setVisibleLines(ids);
  },
  // phone (like @media in CSS): the object in the upper part of the map, above the detail window
  focusAnchorY: () => (window.innerWidth <= 900 ? 0.3 : 0.5),
});

const pins = new PinController(map, { form: $("#pin-form"), button: $("#pin"), wrap: $(".map-wrap") });
const build = new BuildController(map, { form: $("#build-form"), button: $("#build"), getDynamic: () => lastDynamic });

$("#brand-title").textContent = config.title;
document.title = config.title;
if (config.logo) {
  $("#brand-logo").src = config.logo;
  $("#brand-logo").hidden = false;
}

const stream = new StreamPlayer({ button: $("#stream-btn"), layer: $("#stream"), wrap: $(".map-wrap") });

const sidebar = new Sidebar($("#sidebar"), $("#detail"), {
  windows: new FloatingWindows($("#detail"), $(".map-wrap")),
  onPick(kind, id) {
    setSheet(false); // phone: picking from the list shows the object on the map
    // An owner (a viewer's estate) isn't a single map object - the map only drops the previous selection.
    if (kind === "owner") map.select(null);
    else map.focus(kind, id);
    sidebar.setSelected(kind, id);
    writeSelection({ kind, id });
  },
  onStop(lineId, index) {
    map.focusStop(lineId, index);
  },
  onClose() {
    clearSelection();
  },
  onPins(pinned) {
    map.setPinned(pinned);
  },
  onLinesView(view) {
    map.setLinesView(view);
  },
  onHoverItem(kind, id) {
    map.hoverItem(kind, id);
  },
  onHoverCargo(kind, id, cargo, dir) {
    map.hoverCargo(kind, id, cargo, dir);
  },
  onShowList() {
    setSheet(true);
  },
});

initSidebarResize($(".layout"), $(".sidebar-resize"));

// Phone: a button at the bottom of the screen slides the panel with the lists over the map and hides it again.
const sheetToggle = $("#sheet-toggle");
function setSheet(open) {
  $(".layout").classList.toggle("sheet-open", open);
  sheetToggle.setAttribute("aria-expanded", String(open));
  sheetToggle.innerHTML = open ? `✕ ${esc(t("sheet.close"))}` : `☰ ${esc(t("sheet.open"))}`;
}
sheetToggle.addEventListener("click", () => setSheet(sheetToggle.getAttribute("aria-expanded") !== "true"));
setSheet(false);
$("#fit").addEventListener("click", () => map.fit());
// The balance in the top bar (with the sparkline) opens the Company tab.
$("#balance").addEventListener("click", () => sidebar.showTab("company"));
$("#balance").addEventListener("keydown", (e) => {
  if (e.key === "Enter" || e.key === " ") {
    e.preventDefault();
    sidebar.showTab("company");
  }
});
$("#terrain").addEventListener("click", (e) => {
  e.currentTarget.setAttribute("aria-pressed", String(map.toggleTerrain()));
});
$("#stations").addEventListener("click", (e) => {
  e.currentTarget.setAttribute("aria-pressed", String(map.toggleStations()));
});
$("#industries").addEventListener("click", (e) => {
  e.currentTarget.setAttribute("aria-pressed", String(map.toggleIndustries()));
});
$("#warehouses").addEventListener("click", (e) => {
  e.currentTarget.setAttribute("aria-pressed", String(map.toggleWarehouses()));
});
$("#vehicles").addEventListener("click", (e) => {
  e.currentTarget.setAttribute("aria-pressed", String(map.toggleVehicles()));
});

// Esc deselects the selected object (line, station...), unless it already closed the pin or typing is going on in a field.
document.addEventListener("keydown", (e) => {
  if (e.key !== "Escape" || e.defaultPrevented) return;
  if (e.target.closest?.("input, textarea, select, [contenteditable]")) return;
  if (sheetToggle.getAttribute("aria-expanded") === "true") setSheet(false);
  else if (sidebar.selected) clearSelection();
});

// Deselecting an object: Esc or × in the detail window.
function clearSelection() {
  map.select(null);
  sidebar.setSelected(null);
  map.hoverItem(null);
  writeSelection(null);
}

// The selection from the address (#line=123, #town=...): after the data loads and after pasting a new link.
// With a pin in the link the view stays on the pin, and the object is only selected.
function applyPendingSelection() {
  const sel = pendingSelection;
  if (!sel || !lastDynamic) return;
  const d = lastDynamic;
  const list = { town: d.towns, industry: d.industries, warehouse: d.warehouses, station: d.stations, line: d.lines }[sel.kind];
  const exists = sel.kind === "owner" ? sidebar.owners.has(sel.id) : list?.some((x) => x.id === sel.id);
  if (!exists && sel.kind !== "owner" && ++pendingMisses < PENDING_SELECTION_TRIES) return; // it may still arrive
  pendingSelection = null;
  pendingMisses = 0;
  if (!exists) {
    writeSelection(sidebar.selected); // the object is gone (e.g. a deleted line)
    return;
  }
  if (sel.kind === "owner") map.select(null);
  else if (readPinHash()) map.select(sel.kind, sel.id);
  else map.focus(sel.kind, sel.id);
  sidebar.setSelected(sel.kind, sel.id);
}
window.addEventListener("hashchange", () => {
  pendingSelection = readSelection();
  pendingMisses = 0;
  applyPendingSelection();
});

let stationLines = new Map();  // station id -> lines (for the tooltip)
let industryLines = new Map(); // industry id -> the lines of its stations
let linesById = new Map();     // line id -> line (vehicle tooltip)

// State preview from the browser console (debug).
window.tf3 = { map, sidebar, pins, build };

function tooltipHtml(item) {
  const d = item.data;
  if (item.kind === "pin") {
    return `<strong>📍 ${esc(t("pin.title"))}</strong>${d.note ? `<span class="tip-note">${esc(d.note)}</span>` : ""}`;
  }
  const lines = (n) => `<span>${esc(t("lines.n", { n: fmtInt(n) }))}</span>`;
  if (item.kind === "stop") {
    return `<strong>${carrierIcon(item.type, item.type)} ${esc(item.name ?? t("stop"))}</strong>`
      + (item.waiting != null ? `<span>${esc(t("station.waitingTip", { n: fmtInt(item.waiting) }))}</span>` : "")
      + item.lines.map((l) => {
        const n = lineWaitingAt(l, (s) => s.group === item.id);
        return `<span>${esc(l.name)}${n != null ? ` · ${cargoIcon("passengers")}${fmtInt(n)}` : ""}</span>`;
      }).join("");
  }
  if (item.kind === "vehicle") {
    const line = linesById.get(d.line);
    const stop = line?.stops?.[d.stop];
    const where = t(d.state === "AT_TERMINAL" ? "vehicle.atStop" : "vehicle.enRoute",
      { stop: stop?.name ?? t("line.stop", { n: d.stop + 1 }) });
    return `<strong><span class="swatch" style="background:${rgb(line?.color)}"></span> ${carrierIcon(d.carrier)} ${esc(line?.name ?? t("line"))}</strong>`
      + vehicleDetailsTip(d)
      + `<span>${esc(where)}</span>`
      + (d.speed != null && d.state === "EN_ROUTE" ? `<span>${esc(t("vehicle.speed", { v: fmtSpeed(d.speed) }))}</span>` : "")
      + (d.stopped ? `<span class="neg">${esc(t("vehicle.stopped"))}</span>` : "");
  }
  if (item.kind === "town") {
    const needs = (d.cargoNeeds ?? []).flatMap((g) => Object.keys(g ?? {}));
    const profit = sidebar.earnings.towns.get(d.id)?.profit12m;
    const growth = townGrowth(d);
    const coverage = townSupply(d)?.coverage;
    return `<strong>${esc(d.name)}</strong><span>${esc(t("town.capacity", { n: fmtInt(townSize(d)) }))}</span>`
      + (growth != null ? `<span>${esc(t("town.growthTip", { p: fmtSignedPercent(growth) }))}</span>` : "")
      + (coverage != null ? `<span>${esc(t("town.supply"))}: ${esc(fmtPercent(coverage))}</span>` : "")
      + (profit != null ? `<span>${esc(t("town.profitTip", { v: fmtMoney(Math.round(profit)) }))}</span>` : "")
      + (needs.length ? `<span class="tip-cargo">${esc(t("needs", { list: "" }))}${needs.map(cargoLabel).join(" ")}</span>` : "");
  }
  if (item.kind === "warehouse") {
    const cargo = Object.entries(d.cargo ?? {}).sort((a, b) => b[1] - a[1]);
    const n = sidebar.warehouseLines.get(d.id)?.length ?? 0;
    return `<strong>📦 ${esc(d.name ?? t("warehouse"))}</strong>`
      + (d.owner ? `<span>${esc(t("owner"))}: ${esc(d.owner)}</span>` : "")
      + `<span>${esc(t("warehouse.stored", { n: fmtInt(d.amount ?? 0), cap: fmtInt(d.capacity) }))}`
      + `${d.capacity ? ` (${esc(fmtPercent((d.amount ?? 0) / d.capacity))})` : ""}</span>`
      + (cargo.length ? `<span class="tip-cargo">${cargo.map(([c, v]) => `<span title="${esc(cargoName(c))}">${cargoIcon(c)}${fmtInt(v)}</span>`).join(" ")}</span>` : "")
      + (n ? lines(n) : "");
  }
  if (item.kind === "station") {
    const use = stationUse(d);
    return `<strong>${iconHtml(stationImg(d), d.kind)} ${esc(d.name ?? stationName(d.kind))}</strong>`
      + `<span>${esc(stationName(d.kind) ?? t("station"))}${use ? ` · ${esc(use)}` : ""}</span>`
      + (d.waiting != null ? `<span>${esc(t("station.waitingTip", { n: fmtInt(d.waiting) }))}</span>` : "")
      + lines(stationLines.get(d.id)?.length ?? 0);
  }
  const n = industryLines.get(d.id)?.length ?? 0;
  return `<strong>${esc(d.name)}</strong>`
    + `<span>${esc(t("industry.levelTip", { level: d.level ?? "–", p: fmtPercent(d.productivity ?? 0) }))}</span>`
    + (n ? lines(n) : "");
}

// A vehicle's image, model, age, condition and cargo / capacity (lua_mod: collectors.vehicleDetails); without details in the data - nothing.
function vehicleDetailsTip(v) {
  const m = lastDynamic?.models?.[v.model];
  const model = m?.name;
  const age = v.age != null ? t("vehicle.age", { age: fmtAge(v.age) }) : null;
  const cap = Object.entries(v.cap ?? {});
  const loaded = v.load != null;
  return vehicleImgHtml(m, "tip-veh")
    + (model ? `<span>${esc(model)}${v.parts ? ` · ${esc(t("vehicle.parts", { n: v.parts }))}` : ""}</span>` : "")
    + (age ? `<span class="${v.wear >= 1 ? "neg" : ""}">${esc(age)}</span>` : "")
    + (v.maint != null ? `<span class="${v.maint < 0.5 ? "neg" : ""}">${esc(t("vehicle.condition", { p: fmtPercent(v.maint) }))}</span>` : "")
    + (cap.length ? `<span class="tip-cargo">${esc(t(loaded ? "vehicle.load" : "vehicle.capacity"))} ${cap.map(([c, n]) =>
      `<span title="${esc(cargoName(c))}">${cargoIcon(c)}${v.load?.[c] != null ? `${fmtInt(v.load[c])} / ` : ""}${fmtInt(n)}</span>`).join(" ")}</span>` : "");
}

function renderHeader(dynamic) {
  const p = dynamic.player ?? {};
  $("#balance").innerHTML = `<span>${esc(fmtMoney(p.balance))}</span>${balanceSparkline(dynamic.company)}`;
  $("#balance").title = t("kpi.balanceOpen");
  $("#date").textContent = fmtDate(dynamic.date);

  // without the vehicles key the mod has vehicles turned off (settings in Tf3Bridge): "–" and no button on the map
  $("#vehicles").hidden = !dynamic.vehicles;
  if (!dynamic.vehicles) {
    $("#fleet").textContent = "–";
    return;
  }
  const byCarrier = {};
  for (const v of dynamic.vehicles) byCarrier[v.carrier] = (byCarrier[v.carrier] ?? 0) + 1;
  $("#fleet").innerHTML = Object.entries(byCarrier).sort((a, b) => b[1] - a[1])
    .map(([c, n]) => `<span title="${esc(carrierName(c) ?? c)}">${carrierIcon(c) || "•"} ${fmtInt(n)}</span>`).join("");
}

function renderStatus() {
  stream.setLive(false);
  if (lastMeta && pollError) {
    status.dataset.state = "error";
    status.textContent = t("status.partial");
    status.title = String(pollError.message ?? pollError);
    return;
  }
  if (!lastMeta) {
    status.dataset.state = loadError ? "error" : "waiting";
    status.textContent = t(loadError ? "status.error" : "status.waiting");
    status.title = loadError ? t("status.fetchFailed", { url: `${config.dataBaseUrl}meta.json` }) : "";
    return;
  }
  const age = Date.now() - Date.parse(lastMeta.updatedAtUtc);
  const live = age < config.staleAfterMs;
  status.dataset.state = live ? "live" : "stale";
  stream.setLive(live);
  status.textContent = t(live ? "status.live" : "status.stale");
  status.title = t("status.updated", { time: new Date(lastMeta.updatedAtUtc).toLocaleTimeString(locale()) });
}

// ---------------------------------------------------------------- language
// The EN / PL switch in the top bar; after a change we redraw everything built in JS.
const langSwitch = $("#lang");
function renderLangSwitch() {
  langSwitch.innerHTML = LANGS.map((l) =>
    `<button type="button" data-lang="${l}" aria-pressed="${l === lang()}">${l.toUpperCase()}</button>`).join("");
}
langSwitch.addEventListener("click", (e) => {
  const btn = e.target.closest("[data-lang]");
  if (btn) setLang(btn.dataset.lang);
});
onLangChange(() => {
  renderLangSwitch();
  renderStatus();
  stream.refresh();
  setSheet(sheetToggle.getAttribute("aria-expanded") === "true");
  if (lastDynamic) renderHeader(lastDynamic);
  sidebar.render();
  build.refresh();
  tooltip.hidden = true;
});
renderLangSwitch();

new DataFeed({
  onMeta(meta) {
    lastMeta = meta;
    loadError = false;
    renderStatus();
  },
  onTerrain(terrain) {
    map.setTerrain(terrain);
    build.setGround(terrain);
  },
  onRules(rules) {
    build.setRules(rules);
  },
  onNetwork(network) {
    map.setNetwork(network);
    $("#edges").textContent = fmtInt(network.edges.length);
  },
  onDynamic(dynamic) {
    splitAllOwners(dynamic);
    attachEffects(dynamic);
    stationLines = linesByStation(dynamic);
    linesById = new Map((dynamic.lines ?? []).map((l) => [l.id, l]));
    industryLines = linesByIndustry(dynamic, stationLines);
    map.setDynamic(dynamic);
    sidebar.setDynamic(dynamic);
    lastDynamic = dynamic;
    build.refresh();
    renderHeader(dynamic);
    applyPendingSelection();
  },
  onPoll(err) {
    if (err) console.warn(err);
    if (lastMeta) pollError = err;
    else loadError = !!err;
    renderStatus();
  },
}).start();

renderStatus();
setInterval(renderStatus, 5000);
