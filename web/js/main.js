// Spina dane (DataFeed), mapę (MapView) i panel (Sidebar).

import { config } from "../config.js";
import { DataFeed } from "./api.js";
import {
  carrierIcon, carrierName, cargoLabel, escapeHtml as esc, fmtDate, fmtInt, fmtMoney, fmtPercent,
  iconHtml, linesByIndustry, linesByStation, stationImg, stationName, stationUse, townSize,
} from "./format.js";
import { LANGS, applyStatic, lang, locale, onLangChange, setLang, t } from "./i18n.js";
import { MapView } from "./map.js";
import { PinController } from "./pin.js";
import { Sidebar } from "./sidebar.js";

const $ = (sel) => document.querySelector(sel);
const tooltip = $("#tooltip");
const status = $("#status");

let lastMeta = null;
let lastDynamic = null;
let loadError = false;

applyStatic();

const map = new MapView($("#map"), {
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
  },
  onPlace(place) {
    pins.place(place);
  },
  onPinMode(on) {
    pins.modeChanged(on);
  },
  onVisibleLines(ids) {
    sidebar.setVisibleLines(ids);
  },
});

const pins = new PinController(map, { form: $("#pin-form"), button: $("#pin"), wrap: $(".map-wrap") });

const sidebar = new Sidebar($("#sidebar"), {
  onPick(kind, id) {
    map.focus(kind, id);
    sidebar.setSelected(kind, id);
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

$("#fit").addEventListener("click", () => map.fit());
$("#terrain").addEventListener("click", (e) => {
  e.currentTarget.setAttribute("aria-pressed", String(map.toggleTerrain()));
});
$("#stations").addEventListener("click", (e) => {
  e.currentTarget.setAttribute("aria-pressed", String(map.toggleStations()));
});

// Esc odznacza wybrany obiekt (linię, stację...), chyba że zamknął już pinezkę albo trwa pisanie w polu.
document.addEventListener("keydown", (e) => {
  if (e.key !== "Escape" || e.defaultPrevented) return;
  if (e.target.closest?.("input, textarea, select, [contenteditable]")) return;
  if (sidebar.selected) clearSelection();
});

// Odznaczenie obiektu: Esc albo × w panelu szczegółów.
function clearSelection() {
  map.select(null);
  sidebar.setSelected(null);
  map.hoverItem(null);
}

let stationLines = new Map();  // id stacji -> linie (do tooltipa)
let industryLines = new Map(); // id zakładu -> linie jego stacji

// Podgląd stanu z konsoli przeglądarki (debug).
window.tf3 = { map, sidebar, pins };

function tooltipHtml(item) {
  const d = item.data;
  if (item.kind === "pin") {
    return `<strong>📍 ${esc(t("pin.title"))}</strong>${d.note ? `<span class="tip-note">${esc(d.note)}</span>` : ""}`;
  }
  const lines = (n) => `<span>${esc(t("lines.n", { n: fmtInt(n) }))}</span>`;
  if (item.kind === "town") {
    const needs = (d.cargoNeeds ?? []).flatMap((g) => Object.keys(g ?? {}));
    const profit = sidebar.earnings.towns.get(d.id)?.profit12m;
    return `<strong>${esc(d.name)}</strong><span>${esc(t("town.capacity", { n: fmtInt(townSize(d)) }))}</span>`
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
  $("#balance").textContent = fmtMoney(p.balance);
  $("#date").textContent = fmtDate(dynamic.date);

  const byCarrier = {};
  for (const v of dynamic.vehicles ?? []) byCarrier[v.carrier] = (byCarrier[v.carrier] ?? 0) + 1;
  $("#fleet").innerHTML = Object.entries(byCarrier).sort((a, b) => b[1] - a[1])
    .map(([c, n]) => `<span title="${esc(carrierName(c) ?? c)}">${carrierIcon(c) || "•"} ${fmtInt(n)}</span>`).join("");
}

function renderStatus() {
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
  if (lastDynamic) renderHeader(lastDynamic);
  sidebar.render();
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
    stationLines = linesByStation(dynamic);
    industryLines = linesByIndustry(dynamic, stationLines);
    map.setDynamic(dynamic);
    sidebar.setDynamic(dynamic);
    lastDynamic = dynamic;
    renderHeader(dynamic);
  },
  onError(err) {
    console.warn(err);
    if (!lastMeta) {
      loadError = true;
      renderStatus();
    }
  },
}).start();

renderStatus();
setInterval(renderStatus, 5000);
