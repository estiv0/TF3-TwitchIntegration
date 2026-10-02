// Panel boczny: listy (miasta, przemysł, linie) i szczegóły zaznaczonego obiektu.

import {
  CARRIER_ICON, CARRIER_PL, LAND_USE_PL, cargoName, escapeHtml as esc,
  fmtDec, fmtInt, fmtPercent, rgb, townSize,
} from "./format.js";
import { industryKind } from "./map.js";

export class Sidebar {
  constructor(root, { onPick }) {
    this.root = root;
    this.onPick = onPick;
    this.tab = "towns";
    this.filter = "";
    this.dynamic = null;
    this.selected = null;

    this.list = root.querySelector("[data-list]");
    this.detail = root.querySelector("[data-detail]");
    this.search = root.querySelector("[data-search]");

    root.querySelectorAll("[data-tab]").forEach((btn) =>
      btn.addEventListener("click", () => {
        this.tab = btn.dataset.tab;
        root.querySelectorAll("[data-tab]").forEach((b) => b.setAttribute("aria-selected", String(b === btn)));
        this.renderList();
      }));

    this.search.addEventListener("input", () => {
      this.filter = this.search.value.trim().toLowerCase();
      this.renderList();
    });

    this.list.addEventListener("click", (e) => {
      const row = e.target.closest("[data-kind]");
      if (row) this.onPick(row.dataset.kind, Number(row.dataset.id));
    });
  }

  setDynamic(dynamic) {
    this.dynamic = dynamic;
    this.root.querySelector('[data-count="towns"]').textContent = dynamic.towns?.length ?? 0;
    this.root.querySelector('[data-count="industries"]').textContent = dynamic.industries?.length ?? 0;
    this.root.querySelector('[data-count="lines"]').textContent = dynamic.lines?.length ?? 0;
    this.renderList();
    this.renderDetail();
  }

  setSelected(kind, id) {
    this.selected = kind ? { kind, id } : null;
    this.renderList();
    this.renderDetail();
  }

  // ---------------------------------------------------------------- listy
  renderList() {
    const d = this.dynamic;
    if (!d) return;
    const match = (name) => !this.filter || String(name ?? "").toLowerCase().includes(this.filter);
    const isSel = (kind, id) => this.selected?.kind === kind && this.selected?.id === id;
    let html = "";

    if (this.tab === "towns") {
      html = [...d.towns].filter((t) => match(t.name)).sort((a, b) => townSize(b) - townSize(a))
        .map((t) => `
          <li class="row" data-kind="town" data-id="${t.id}" aria-current="${isSel("town", t.id)}">
            <span class="dot dot-town"></span>
            <span class="row-name">${esc(t.name)}</span>
            <span class="row-value">${fmtInt(townSize(t))}</span>
          </li>`).join("");
    } else if (this.tab === "industries") {
      html = [...d.industries].filter((i) => match(i.name)).sort((a, b) => (b.level ?? 0) - (a.level ?? 0))
        .map((i) => `
          <li class="row" data-kind="industry" data-id="${i.id}" aria-current="${isSel("industry", i.id)}">
            <span class="dot dot-${industryKind(i)}"></span>
            <span class="row-name">${esc(i.name)}</span>
            <span class="row-value">poz. ${i.level ?? "–"}</span>
          </li>`).join("");
    } else {
      html = [...d.lines].filter((l) => match(l.name)).sort((a, b) => (b.vehicleCount ?? 0) - (a.vehicleCount ?? 0))
        .map((l) => `
          <li class="row" data-kind="line" data-id="${l.id}" aria-current="${isSel("line", l.id)}">
            <span class="swatch" style="background:${rgb(l.color)}"></span>
            <span class="row-name">${CARRIER_ICON[l.carrier] ?? ""} ${esc(l.name)}</span>
            <span class="row-value">${fmtInt(l.vehicleCount)} poj. · ${l.stops?.length ?? 0} przyst.</span>
          </li>`).join("");
    }

    this.list.innerHTML = html || `<li class="empty">Brak wyników</li>`;
  }

  // ---------------------------------------------------------------- szczegóły
  renderDetail() {
    const d = this.dynamic;
    const sel = this.selected;
    if (!d || !sel) {
      this.detail.hidden = true;
      return;
    }
    const list = { town: d.towns, industry: d.industries, line: d.lines }[sel.kind] ?? [];
    const item = list.find((x) => x.id === sel.id);
    if (!item) {
      this.detail.hidden = true;
      return;
    }
    this.detail.hidden = false;
    this.detail.innerHTML = sel.kind === "town" ? townDetail(item)
      : sel.kind === "line" ? lineDetail(item, d.vehicles ?? []) : industryDetail(item);
  }
}

function townDetail(t) {
  const caps = (t.capacities ?? []).map((c, i) => stat(LAND_USE_PL[i] ?? `Strefa ${i + 1}`, fmtInt(c))).join("");
  const needs = (t.cargoNeeds ?? []).flatMap((group) => Object.entries(group ?? {}))
    .map(([cargo, v]) => `<li>${esc(cargoName(cargo))}<span>${fmtDec(v)}</span></li>`).join("");
  return `
    <h2>${esc(t.name)}</h2>
    <p class="detail-sub">Miasto · ${t.growing ? "rośnie" : "rozwój wstrzymany"}</p>
    <dl class="stats">
      ${caps}
      ${stat("Hałas", fmtDec(t.noise))}
      ${stat("Zanieczyszczenie", fmtInt(t.pollution))}
    </dl>
    ${needs ? `<h3>Zapotrzebowanie</h3><ul class="chips">${needs}</ul>` : ""}`;
}

function industryDetail(i) {
  const outputs = new Map();
  for (const rule of i.rules ?? []) {
    if (rule.booster) continue;
    for (const [cargo, amount] of Object.entries(rule.output ?? {}))
      outputs.set(cargo, (outputs.get(cargo) ?? 0) + amount);
  }
  const produced = new Set(outputs.keys());
  const inputs = (i.stocks ?? []).filter((s) => !produced.has(s.cargo));

  return `
    <h2>${esc(i.name)}</h2>
    <p class="detail-sub">${industryKind(i) === "raw" ? "Surowce" : "Przetwórstwo"}</p>
    <dl class="stats">
      ${stat("Poziom", `${i.level ?? "–"} / ${i.maxLevel || "–"}`)}
      ${stat("Produktywność", fmtPercent(i.productivity))}
      ${stat("Postęp rozbudowy", fmtPercent(i.upgrade))}
    </dl>
    ${inputs.length ? `<h3>Potrzebuje</h3><ul class="chips">${inputs.map((s) => `<li>${esc(cargoName(s.cargo))}</li>`).join("")}</ul>` : ""}
    ${outputs.size ? `<h3>Produkuje</h3><ul class="chips">${[...outputs].map(([c, a]) => `<li>${esc(cargoName(c))}<span>${fmtInt(a)}</span></li>`).join("")}</ul>` : ""}`;
}

function lineDetail(l, vehicles) {
  const stopped = vehicles.filter((v) => v.line === l.id && v.stopped).length;
  const stops = (l.stops ?? []).map((s, i) => `<li>${esc(s.name ?? `Przystanek ${i + 1}`)}</li>`).join("");
  return `
    <h2><span class="swatch" style="background:${rgb(l.color)}"></span> ${esc(l.name)}</h2>
    <p class="detail-sub">${CARRIER_ICON[l.carrier] ?? ""} ${esc(CARRIER_PL[l.carrier] ?? "Linia")}</p>
    <dl class="stats">
      ${stat("Pojazdy", fmtInt(l.vehicleCount))}
      ${stat("Zatrzymane", fmtInt(stopped))}
      ${stat("Przystanki", fmtInt(l.stops?.length ?? 0))}
    </dl>
    ${stops ? `<h3>Przystanki</h3><ol class="stops">${stops}</ol>` : ""}`;
}

const stat = (label, value) => `<div><dt>${esc(label)}</dt><dd>${value}</dd></div>`;

