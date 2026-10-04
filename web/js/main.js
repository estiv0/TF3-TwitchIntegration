// Spina dane (DataFeed), mapę (MapView) i panel (Sidebar).

import { config } from "../config.js";
import { DataFeed } from "./api.js";
import {
  carrierIcon, carrierName, cargoLabel, escapeHtml as esc, fmtDate, fmtInt, fmtMoney, fmtPercent,
  fmtSignedPercent, iconHtml, linesByIndustry, linesByStation, rgb, stationImg, stationName, splitAllOwners, attachEffects, stationUse, townGrowth, townSize,
} from "./format.js";
import { balanceSparkline } from "./company.js";
import { readSelection, writeSelection } from "./hash.js";
import { LANGS, applyStatic, lang, locale, onLangChange, setLang, t } from "./i18n.js";
import { MapView } from "./map.js";
import { PinController, readPinHash } from "./pin.js";
import { BuildController } from "./build.js";
import { initFloatingPanel } from "./float.js";
import { initSidebarResize } from "./resize.js";
import { Sidebar, townSupply } from "./sidebar.js";

const $ = (sel) => document.querySelector(sel);
const tooltip = $("#tooltip");
const status = $("#status");

let lastMeta = null;
let lastDynamic = null;
let loadError = false;
let pollError = null;                   // błąd pobierania któregoś pliku przy działającym meta.json
let pendingSelection = readSelection(); // zaznaczenie z linku - zastosujemy, gdy przyjdą dane

applyStatic();

const map = new MapView($("#map"), {
  onLayers() {
    $("#stations").setAttribute("aria-pressed", String(map.showStations));
    $("#industries").setAttribute("aria-pressed", String(map.showIndustries));
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
  // telefon (jak @media w CSS): obiekt w górnej części mapy, nad okienkiem szczegółów
  focusAnchorY: () => (window.innerWidth <= 900 ? 0.3 : 0.5),
});

const pins = new PinController(map, { form: $("#pin-form"), button: $("#pin"), wrap: $(".map-wrap") });
const build = new BuildController(map, { form: $("#build-form"), button: $("#build"), wrap: $(".map-wrap"), getDynamic: () => lastDynamic });

const sidebar = new Sidebar($("#sidebar"), $("#detail"), {
  onPick(kind, id) {
    setSheet(false); // telefon: wybór z listy pokazuje obiekt na mapie
    map.focus(kind, id);
    sidebar.setSelected(kind, id);
    writeSelection({ kind, id });
  },
  onStop(lineId, index) {
    map.focusStop(lineId, index);
  },
  onClose() {
    clearSelection();
  },
  onLinesView(view) {
    map.setLinesView(view);
  },
  onHoverItem(kind, id) {
    map.hoverItem(kind, id);
  },
});

initSidebarResize($(".layout"), $(".sidebar-resize"));
initFloatingPanel($("#detail"), $(".map-wrap"));

// Telefon: przycisk na dole ekranu wysuwa panel z listami na mapę i chowa go z powrotem.
const sheetToggle = $("#sheet-toggle");
function setSheet(open) {
  $(".layout").classList.toggle("sheet-open", open);
  sheetToggle.setAttribute("aria-expanded", String(open));
  sheetToggle.innerHTML = open ? `✕ ${esc(t("sheet.close"))}` : `☰ ${esc(t("sheet.open"))}`;
}
sheetToggle.addEventListener("click", () => setSheet(sheetToggle.getAttribute("aria-expanded") !== "true"));
setSheet(false);
$("#fit").addEventListener("click", () => map.fit());
// Saldo w górnym pasku (ze sparkline) otwiera zakładkę Firma.
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
$("#vehicles").addEventListener("click", (e) => {
  e.currentTarget.setAttribute("aria-pressed", String(map.toggleVehicles()));
});

// Esc odznacza wybrany obiekt (linię, stację...), chyba że zamknął już pinezkę albo trwa pisanie w polu.
document.addEventListener("keydown", (e) => {
  if (e.key !== "Escape" || e.defaultPrevented) return;
  if (e.target.closest?.("input, textarea, select, [contenteditable]")) return;
  if (sheetToggle.getAttribute("aria-expanded") === "true") setSheet(false);
  else if (sidebar.selected) clearSelection();
});

// Odznaczenie obiektu: Esc albo × w okienku szczegółów.
function clearSelection() {
  map.select(null);
  sidebar.setSelected(null);
  map.hoverItem(null);
  writeSelection(null);
}

// Zaznaczenie z adresu (#line=123, #town=...): po wczytaniu danych i po wklejeniu nowego linku.
// Z pinezką w linku widok zostaje na pinezce, a obiekt jest tylko zaznaczony.
function applyPendingSelection() {
  const sel = pendingSelection;
  if (!sel || !lastDynamic) return;
  pendingSelection = null;
  const d = lastDynamic;
  const list = { town: d.towns, industry: d.industries, station: d.stations, line: d.lines }[sel.kind];
  if (!list?.some((x) => x.id === sel.id)) {
    writeSelection(sidebar.selected); // obiektu już nie ma (np. usunięta linia)
    return;
  }
  if (readPinHash()) map.select(sel.kind, sel.id);
  else map.focus(sel.kind, sel.id);
  sidebar.setSelected(sel.kind, sel.id);
}
window.addEventListener("hashchange", () => {
  pendingSelection = readSelection();
  applyPendingSelection();
});

let stationLines = new Map();  // id stacji -> linie (do tooltipa)
let industryLines = new Map(); // id zakładu -> linie jego stacji
let linesById = new Map();     // id linii -> linia (tooltip pojazdu)

// Podgląd stanu z konsoli przeglądarki (debug).
window.tf3 = { map, sidebar, pins, build };

function tooltipHtml(item) {
  const d = item.data;
  if (item.kind === "pin") {
    return `<strong>📍 ${esc(t("pin.title"))}</strong>${d.note ? `<span class="tip-note">${esc(d.note)}</span>` : ""}`;
  }
  const lines = (n) => `<span>${esc(t("lines.n", { n: fmtInt(n) }))}</span>`;
  if (item.kind === "vehicle") {
    const line = linesById.get(d.line);
    const stop = line?.stops?.[d.stop];
    const where = t(d.state === "AT_TERMINAL" ? "vehicle.atStop" : "vehicle.enRoute",
      { stop: stop?.name ?? t("line.stop", { n: d.stop + 1 }) });
    return `<strong><span class="swatch" style="background:${rgb(line?.color)}"></span> ${carrierIcon(d.carrier)} ${esc(line?.name ?? t("line"))}</strong>`
      + `<span>${esc(where)}</span>`
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
  if (item.kind === "station") {
    const use = stationUse(d);
    return `<strong>${iconHtml(stationImg(d), d.kind)} ${esc(d.name ?? stationName(d.kind))}</strong>`
      + `<span>${esc(stationName(d.kind) ?? t("station"))}${use ? ` · ${esc(use)}` : ""}</span>`
      + lines(stationLines.get(d.id)?.length ?? 0);
  }
  const n = industryLines.get(d.id)?.length ?? 0;
  return `<strong>${esc(d.name)}</strong>`
    + `<span>${esc(t("industry.levelTip", { level: d.level ?? "–", p: fmtPercent(d.productivity ?? 0) }))}</span>`
    + (n ? lines(n) : "");
}

function renderHeader(dynamic) {
  const p = dynamic.player ?? {};
  $("#balance").innerHTML = `<span>${esc(fmtMoney(p.balance))}</span>${balanceSparkline(dynamic.company)}`;
  $("#balance").title = t("kpi.balanceOpen");
  $("#date").textContent = fmtDate(dynamic.date);

  // bez klucza vehicles mod ma wyłączone pojazdy (ustawienia w Tf3Bridge): "–" i bez przycisku na mapie
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
  status.textContent = t(live ? "status.live" : "status.stale");
  status.title = t("status.updated", { time: new Date(lastMeta.updatedAtUtc).toLocaleTimeString(locale()) });
}

// ---------------------------------------------------------------- język
// Przełącznik PL / EN w górnym pasku; po zmianie przerysowujemy wszystko, co budujemy w JS.
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
