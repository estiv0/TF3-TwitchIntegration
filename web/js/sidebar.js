// Panel boczny: listy (miasta, przemysł, stacje, linie) i szczegóły zaznaczonego obiektu.

import {
  CARRIER_PL, LAND_USE_PL, STATION_ORDER, STATION_PL, carrierIcon, cargoLabel, escapeHtml as esc, iconHtml,
  fmtDec, fmtInt, fmtPercent, industryOfStation, linesByIndustry, linesByStation, rgb, stationImg, stationUse, townSize,
} from "./format.js";
import { industryKind, industryOutputs } from "./map.js";

export class Sidebar {
  constructor(root, { onPick, onStop }) {
    this.root = root;
    this.onPick = onPick;
    this.onStop = onStop;
    this.tab = "towns";
    this.filter = "";
    this.dynamic = null;
    this.selected = null;
    this.focusedStop = null;    // indeks przystanku zaznaczonej linii, na który przeniesiono mapę

    this.list = root.querySelector("[data-list]");
    this.detail = root.querySelector("[data-detail]");
    this.search = root.querySelector("[data-search]");
    this.carriers = root.querySelector("[data-carriers]");
    this.typeFilter = { lines: "", stations: "" }; // filtr nośnika / rodzaju stacji, "" = wszystkie
    this.stationLines = new Map();                 // id stacji -> linie
    this.industryLines = new Map();                // id zakładu -> linie jego stacji
    this.stationIndustry = new Map();              // id stacji -> id zakładu (stacje przemysłu)

    root.querySelectorAll("[data-tab]").forEach((btn) =>
      btn.addEventListener("click", () => {
        this.tab = btn.dataset.tab;
        root.querySelectorAll("[data-tab]").forEach((b) => b.setAttribute("aria-selected", String(b === btn)));
        this.carriers.hidden = !(this.tab in this.typeFilter);
        this.search.placeholder = this.tab === "lines" ? "Szukaj linii lub stacji…" : "Szukaj…";
        this.renderCarriers();
        this.renderList();
      }));

    this.carriers.addEventListener("click", (e) => {
      const btn = e.target.closest("[data-carrier]");
      if (!btn) return;
      this.typeFilter[this.tab] = btn.dataset.carrier;
      this.renderCarriers();
      this.renderList();
    });

    this.search.addEventListener("input", () => {
      this.filter = this.search.value.trim().toLowerCase();
      this.renderList();
    });

    this.list.addEventListener("click", (e) => {
      const row = e.target.closest("[data-kind]");
      if (row) this.onPick(row.dataset.kind, Number(row.dataset.id));
    });

    this.detail.addEventListener("click", (e) => {
      const line = e.target.closest("[data-line]");
      if (line) {
        this.onPick("line", Number(line.dataset.line));
        return;
      }
      const stop = e.target.closest("[data-stop]");
      if (!stop || this.selected?.kind !== "line") return;
      this.focusedStop = Number(stop.dataset.stop);
      this.onStop?.(this.selected.id, this.focusedStop);
      this.renderDetail();
    });
  }

  setDynamic(dynamic) {
    this.dynamic = dynamic;
    this.stationLines = linesByStation(dynamic);
    this.stationIndustry = industryOfStation(dynamic);
    this.industryLines = linesByIndustry(dynamic, this.stationLines, this.stationIndustry);
    this.root.querySelector('[data-count="towns"]').textContent = dynamic.towns?.length ?? 0;
    this.root.querySelector('[data-count="industries"]').textContent = dynamic.industries?.length ?? 0;
    this.root.querySelector('[data-count="stations"]').textContent = dynamic.stations?.length ?? 0;
    this.root.querySelector('[data-count="lines"]').textContent = dynamic.lines?.length ?? 0;
    this.renderCarriers();
    this.renderList();
    this.renderDetail();
  }

  setSelected(kind, id) {
    this.selected = kind ? { kind, id } : null;
    this.focusedStop = null;
    this.renderList();
    this.renderDetail();
  }

  // Klucz filtra stacji: stacje zakładów osobno, niezależnie od nośnika.
  stationKey(s) {
    return this.stationIndustry.has(s.id) ? "INDUSTRY" : s.kind;
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
    } else if (this.tab === "stations") {
      const kind = this.typeFilter.stations;
      const nLines = (s) => this.stationLines.get(s.id)?.length ?? 0;
      const industries = new Map(d.industries.map((i) => [i.id, i]));
      html = (d.stations ?? []).filter((s) => (!kind || this.stationKey(s) === kind) && match(s.name))
        .sort((a, b) => nLines(b) - nLines(a) || String(a.name).localeCompare(String(b.name), "pl"))
        .map((s) => {
          // Stacja zakładu: ikonka zakładu, a kliknięcie otwiera zakład (tam są też jego linie).
          const ind = industries.get(this.stationIndustry.get(s.id));
          const [k, id] = ind ? ["industry", ind.id] : ["station", s.id];
          return `
          <li class="row" data-kind="${k}" data-id="${id}" aria-current="${isSel(k, id)}">
            ${ind ? `<span class="dot dot-${industryKind(ind)}"></span>` : iconHtml(stationImg(s), s.kind)}
            <span class="row-name">${ind ? iconHtml(stationImg(s), s.kind) : ""} ${esc(s.name ?? ind?.name ?? STATION_PL[s.kind])}</span>
            <span class="row-value">${fmtInt(nLines(s))} lin.</span>
          </li>`;
        }).join("");
    } else {
      // Szukanie po nazwie linii albo po nazwach jej stacji; trafione stacje pokazujemy pod nazwą.
      const carrier = this.typeFilter.lines;
      html = d.lines.filter((l) => !carrier || carrierKey(l) === carrier)
        .map((l) => ({ l, stations: match(l.name) ? [] : matchingStations(l, this.filter) }))
        .filter(({ l, stations }) => match(l.name) || stations.length)
        .sort((a, b) => (b.l.vehicleCount ?? 0) - (a.l.vehicleCount ?? 0))
        .map(({ l, stations }) => `
          <li class="row" data-kind="line" data-id="${l.id}" aria-current="${isSel("line", l.id)}">
            <span class="swatch" style="background:${rgb(l.color)}"></span>
            <span class="row-main">
              <span class="row-name">${carrierIcon(l.carrier, l.carrier)} ${esc(l.name)}</span>
              ${stations.length ? `<span class="row-hint">przez ${stations.map((s) => highlight(s, this.filter)).join(", ")}</span>` : ""}
            </span>
            <span class="row-value">${fmtInt(l.vehicleCount)} poj. · ${l.stops?.length ?? 0} przyst.</span>
          </li>`).join("");
    }

    this.list.innerHTML = html || `<li class="empty">Brak wyników</li>`;
  }

  // Przyciski typów linii / rodzajów stacji: tylko typy obecne w danych, z liczbą pozycji.
  renderCarriers() {
    if (!(this.tab in this.typeFilter)) return;
    const stations = this.tab === "stations";
    const items = (stations ? this.dynamic?.stations : this.dynamic?.lines) ?? [];
    const keyOf = stations ? (s) => this.stationKey(s) : carrierKey;
    const order = stations ? STATION_ORDER : CARRIER_ORDER;
    const names = stations ? STATION_PL : CARRIER_PL;

    const counts = new Map();
    for (const x of items) counts.set(keyOf(x), (counts.get(keyOf(x)) ?? 0) + 1);
    const current = this.typeFilter[this.tab];
    if (current && !counts.has(current)) this.typeFilter[this.tab] = "";
    const btn = (key, label, n) =>
      `<button type="button" data-carrier="${key}" aria-pressed="${this.typeFilter[this.tab] === key}">${label}<span>${fmtInt(n)}</span></button>`;
    this.carriers.innerHTML = btn("", "Wszystkie", items.length)
      + order.filter((c) => counts.has(c))
        .map((c) => btn(c, `${carrierIcon(c, c)} ${esc(names[c] ?? "Bez pojazdów")}`, counts.get(c))).join("");
  }

  // ---------------------------------------------------------------- szczegóły
  renderDetail() {
    const d = this.dynamic;
    const sel = this.selected;
    if (!d || !sel) {
      this.detail.hidden = true;
      return;
    }
    const list = { town: d.towns, industry: d.industries, station: d.stations, line: d.lines }[sel.kind] ?? [];
    const item = list.find((x) => x.id === sel.id);
    if (!item) {
      this.detail.hidden = true;
      return;
    }
    this.detail.hidden = false;
    this.detail.innerHTML = sel.kind === "town" ? townDetail(item)
      : sel.kind === "line" ? lineDetail(item, this.focusedStop)
      : sel.kind === "station" ? stationDetail(item, this.stationLines.get(item.id) ?? [])
      : industryDetail(item, this.industryLines.get(item.id) ?? []);
  }
}

// Linie są klikalne (data-line) - przełączają wybór na linię.
function lineLinks(lines) {
  const rows = [...lines].sort((a, b) => (b.vehicleCount ?? 0) - (a.vehicleCount ?? 0)).map((l) => `
    <li><button type="button" data-line="${l.id}">
      <span class="swatch" style="background:${rgb(l.color)}"></span>${carrierIcon(l.carrier, l.carrier)} ${esc(l.name)}
    </button></li>`).join("");
  return rows ? `<h3>Linie</h3><ul class="line-links">${rows}</ul>` : "";
}

function stationDetail(s, lines) {
  const use = stationUse(s);
  return `
    <h2>${iconHtml(stationImg(s), s.kind)} ${esc(s.name ?? STATION_PL[s.kind])}</h2>
    <p class="detail-sub">${esc(STATION_PL[s.kind] ?? "Stacja")}${use ? ` · ${esc(use)}` : ""}</p>
    <dl class="stats">
      ${stat("Linie", fmtInt(lines.length))}
      ${stat("Pojazdy linii", fmtInt(lines.reduce((n, l) => n + (l.vehicleCount ?? 0), 0)))}
    </dl>
    ${lineLinks(lines)}`;
}

function townDetail(t) {
  const caps = (t.capacities ?? []).map((c, i) => stat(LAND_USE_PL[i] ?? `Strefa ${i + 1}`, fmtInt(c))).join("");
  const needs = (t.cargoNeeds ?? []).flatMap((group) => Object.entries(group ?? {}))
    .map(([cargo, v]) => `<li>${cargoLabel(cargo)}<span>${fmtDec(v)}</span></li>`).join("");
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

function industryDetail(i, lines) {
  const outputs = industryOutputs(i);
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
    ${inputs.length ? `<h3>Potrzebuje</h3><ul class="chips">${inputs.map((s) => `<li>${cargoLabel(s.cargo)}</li>`).join("")}</ul>` : ""}
    ${outputs.size ? `<h3>Produkuje</h3><ul class="chips">${[...outputs].map(([c, a]) => `<li>${cargoLabel(c)}<span>${fmtInt(a)}</span></li>`).join("")}</ul>` : ""}
    ${lineLinks(lines)}`;
}

// Nośnik linii jest z pojazdów albo z rodzaju stacji; bez obu (np. same przystanki) ląduje w "NONE".
const CARRIER_ORDER = ["RAIL", "ROAD", "TRAM", "WATER", "AIR", "OTHER", "NONE"];
const carrierKey = (l) => l.carrier ?? "NONE";

// Unikalne nazwy stacji linii pasujące do frazy (stacja bywa na linii dwa razy, np. w pętli).
function matchingStations(l, filter) {
  if (!filter) return [];
  const names = new Set((l.stops ?? []).map((s) => s.name).filter(Boolean));
  return [...names].filter((n) => n.toLowerCase().includes(filter));
}

function highlight(text, filter) {
  const i = text.toLowerCase().indexOf(filter);
  if (i < 0) return esc(text);
  return esc(text.slice(0, i)) + `<mark>${esc(text.slice(i, i + filter.length))}</mark>` + esc(text.slice(i + filter.length));
}

// Przystanek z pozycją jest klikalny (data-stop = indeks na linii) - przenosi mapę do niego.
function lineDetail(l, focusedStop) {
  const stops = (l.stops ?? []).map((s, i) => {
    const name = esc(s.name ?? `Przystanek ${i + 1}`);
    if (!s.pos) return `<li>${name}</li>`;
    return `<li data-stop="${i}" aria-current="${focusedStop === i}"><button type="button">${name}</button></li>`;
  }).join("");
  return `
    <h2><span class="swatch" style="background:${rgb(l.color)}"></span> ${esc(l.name)}</h2>
    <p class="detail-sub">${carrierIcon(l.carrier, l.carrier)} ${esc(CARRIER_PL[l.carrier] ?? "Linia")}</p>
    <dl class="stats">
      ${stat("Pojazdy", fmtInt(l.vehicleCount))}
      ${stat("Przystanki", fmtInt(l.stops?.length ?? 0))}
    </dl>
    ${stops ? `<h3>Przystanki</h3><ol class="stops">${stops}</ol>` : ""}`;
}

const stat = (label, value) => `<div><dt>${esc(label)}</dt><dd>${value}</dd></div>`;

