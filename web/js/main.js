// Spina dane (DataFeed), mapę (MapView) i panel (Sidebar).

import { config } from "../config.js";
import { DataFeed } from "./api.js";
import { CARRIER_ICON, cargoName, escapeHtml as esc, fmtDate, fmtInt, fmtMoney, townSize } from "./format.js";
import { MapView } from "./map.js";
import { Sidebar } from "./sidebar.js";

const $ = (sel) => document.querySelector(sel);
const tooltip = $("#tooltip");
const status = $("#status");

let lastMeta = null;

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
});

const sidebar = new Sidebar($("#sidebar"), {
  onPick(kind, id) {
    map.focus(kind, id);
    sidebar.setSelected(kind, id);
  },
  onStop(lineId, index) {
    map.focusStop(lineId, index);
  },
});

$("#fit").addEventListener("click", () => map.fit());
$("#terrain").addEventListener("click", (e) => {
  e.currentTarget.setAttribute("aria-pressed", String(map.toggleTerrain()));
});

// Podgląd stanu z konsoli przeglądarki (debug).
window.tf3 = { map, sidebar };

function tooltipHtml(item) {
  const d = item.data;
  if (item.kind === "town") {
    const needs = (d.cargoNeeds ?? []).flatMap((g) => Object.keys(g ?? {})).map(cargoName);
    return `<strong>${esc(d.name)}</strong><span>Miasto · pojemność ${fmtInt(townSize(d))}</span>`
      + (needs.length ? `<span>Potrzebuje: ${esc(needs.join(", "))}</span>` : "");
  }
  return `<strong>${esc(d.name)}</strong><span>Poziom ${d.level ?? "–"} · produktywność ${Math.round((d.productivity ?? 0) * 100)}%</span>`;
}

function renderHeader(dynamic) {
  const p = dynamic.player ?? {};
  $("#company").textContent = p.name ?? "–";
  $("#balance").textContent = fmtMoney(p.balance);
  $("#date").textContent = fmtDate(dynamic.date);

  const byCarrier = {};
  for (const v of dynamic.vehicles ?? []) byCarrier[v.carrier] = (byCarrier[v.carrier] ?? 0) + 1;
  $("#fleet").innerHTML = Object.entries(byCarrier).sort((a, b) => b[1] - a[1])
    .map(([c, n]) => `<span title="${esc(c)}">${CARRIER_ICON[c] ?? "•"} ${fmtInt(n)}</span>`).join("");
}

function renderStatus() {
  if (!lastMeta) {
    status.dataset.state = "waiting";
    status.textContent = "Czekam na dane…";
    return;
  }
  const age = Date.now() - Date.parse(lastMeta.updatedAtUtc);
  const live = age < config.staleAfterMs;
  status.dataset.state = live ? "live" : "stale";
  status.textContent = live ? "Na żywo" : "Wstrzymane";
  status.title = `Ostatnia aktualizacja: ${new Date(lastMeta.updatedAtUtc).toLocaleTimeString("pl-PL")}`;
}

new DataFeed({
  onMeta(meta) {
    lastMeta = meta;
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
    map.setDynamic(dynamic);
    sidebar.setDynamic(dynamic);
    renderHeader(dynamic);
  },
  onError(err) {
    console.warn(err);
    if (!lastMeta) {
      status.dataset.state = "error";
      status.textContent = "Brak danych";
      status.title = `Nie udało się pobrać ${config.dataBaseUrl}meta.json – czy Tf3Bridge działa?`;
    }
  },
}).start();

renderStatus();
setInterval(renderStatus, 5000);
