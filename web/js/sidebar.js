// Panel boczny: listy (miasta, przemysł, stacje, linie) i szczegóły zaznaczonego obiektu.

import {
  STATION_ORDER, carrierIcon, carrierName, cargoLabel, escapeHtml as esc, iconHtml, landUseName, stationName,
  fmtDec, fmtInt, fmtMoney, fmtPercent, industryOfStation, linesByIndustry, linesByStation, rgb, stationImg, stationUse, townSize,
} from "./format.js";
import { locale, t } from "./i18n.js";
import { industryKind, industryOutputs } from "./map.js";

export class Sidebar {
  constructor(root, { onPick, onStop, onLinesView, onHoverLine }) {
    this.root = root;
    this.onPick = onPick;
    this.onStop = onStop;
    this.onLinesView = onLinesView; // { match } albo null - co mapa ma rysować na zakładce Linie
    this.viewportOnly = loadViewportOnly(); // "oko": linie na mapie i na liście tylko z widoku mapy
    this.visibleLines = null;               // Set id linii na ekranie mapy (od mapy), null = bez filtra
    this.tab = "towns";
    this.filter = "";
    this.dynamic = null;
    this.selected = null;
    this.focusedStop = null;    // indeks przystanku zaznaczonej linii, na który przeniesiono mapę

    this.list = root.querySelector("[data-list]");
    this.detail = root.querySelector("[data-detail]");
    this.search = root.querySelector("[data-search]");
    this.carriers = root.querySelector("[data-carriers]");
    this.viewportBtn = root.querySelector("[data-viewport]");
    this.typeFilter = { lines: "", stations: "" }; // filtr nośnika / rodzaju stacji, "" = wszystkie
    this.stationLines = new Map();                 // id stacji -> linie
    this.industryLines = new Map();                // id zakładu -> linie jego stacji
    this.stationIndustry = new Map();              // id stacji -> id zakładu (stacje przemysłu)

    root.querySelectorAll("[data-tab]").forEach((btn) =>
      btn.addEventListener("click", () => {
        this.tab = btn.dataset.tab;
        root.querySelectorAll("[data-tab]").forEach((b) => b.setAttribute("aria-selected", String(b === btn)));
        this.carriers.hidden = !(this.tab in this.typeFilter);
        this.renderSearch();
        this.renderCarriers();
        this.emitLinesView();
        this.renderList();
      }));

    this.carriers.addEventListener("click", (e) => {
      const btn = e.target.closest("[data-carrier]");
      if (!btn) return;
      this.typeFilter[this.tab] = btn.dataset.carrier;
      this.renderCarriers();
      this.emitLinesView();
      this.renderList();
    });

    this.search.addEventListener("input", () => {
      this.filter = this.search.value.trim().toLowerCase();
      this.emitLinesView();
      this.renderList();
    });

    this.viewportBtn.addEventListener("click", () => {
      this.viewportOnly = !this.viewportOnly;
      saveViewportOnly(this.viewportOnly);
      this.renderSearch();
      this.emitLinesView();
      this.renderList();
    });

    this.list.addEventListener("click", (e) => {
      const row = e.target.closest("[data-kind]");
      if (row) this.onPick(row.dataset.kind, Number(row.dataset.id));
    });

    // Najechanie na linię na liście podświetla ją na mapie.
    this.list.addEventListener("mouseover", (e) => {
      const row = e.target.closest('[data-kind="line"]');
      onHoverLine?.(row ? Number(row.dataset.id) : null);
    });
    this.list.addEventListener("mouseleave", () => onHoverLine?.(null));

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

    this.renderSearch();
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
    this.render();
  }

  // Mapa zgłasza linie widoczne na ekranie (tylko gdy emitLinesView przekazał jej filtr).
  setVisibleLines(ids) {
    this.visibleLines = ids;
    if (this.tab === "lines") this.renderList();
  }

  // Na zakładce Linie z włączonym okiem mapa rysuje wszystkie linie z ekranu, które przechodzą
  // filtry panelu (typ + szukanie), i odsyła ich listę; poza tym - tylko zaznaczoną linię.
  emitLinesView() {
    const active = this.tab === "lines" && this.viewportOnly;
    if (!active) this.visibleLines = null;
    this.onLinesView?.(active ? { match: (l) => this.matchLine(l) !== null } : null);
  }

  // Linia przechodzi filtry typu i szukania -> nazwy trafionych stacji (puste, gdy trafiła nazwa linii); inaczej null.
  matchLine(l) {
    const carrier = this.typeFilter.lines;
    if (carrier && carrierKey(l) !== carrier) return null;
    if (!this.filter || String(l.name ?? "").toLowerCase().includes(this.filter)) return [];
    const stations = matchingStations(l, this.filter);
    return stations.length ? stations : null;
  }

  setSelected(kind, id) {
    this.selected = kind ? { kind, id } : null;
    this.focusedStop = null;
    this.renderList();
    this.renderDetail();
  }

  // Wszystko, co zależy od danych albo języka (po zmianie języka wołane z main.js).
  render() {
    this.renderSearch();
    this.renderCarriers();
    this.renderList();
    this.renderDetail();
  }

  renderSearch() {
    this.search.placeholder = t(this.tab === "lines" ? "search.lines" : "search");
    this.viewportBtn.hidden = this.tab !== "lines";
    this.viewportBtn.setAttribute("aria-pressed", String(this.viewportOnly));
    this.viewportBtn.title = t(this.viewportOnly ? "lines.viewportOn" : "lines.viewportOff");
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
      html = [...d.towns].filter((x) => match(x.name)).sort((a, b) => townSize(b) - townSize(a))
        .map((x) => `
          <li class="row" data-kind="town" data-id="${x.id}" aria-current="${isSel("town", x.id)}">
            <span class="dot dot-town"></span>
            <span class="row-name">${esc(x.name)}</span>
            <span class="row-value">${fmtInt(townSize(x))}</span>
          </li>`).join("");
    } else if (this.tab === "industries") {
      html = [...d.industries].filter((i) => match(i.name)).sort((a, b) => (b.level ?? 0) - (a.level ?? 0))
        .map((i) => `
          <li class="row" data-kind="industry" data-id="${i.id}" aria-current="${isSel("industry", i.id)}">
            <span class="dot dot-${industryKind(i)}"></span>
            <span class="row-name">${esc(i.name)}</span>
            <span class="row-value">${esc(t("list.level", { n: i.level ?? "–" }))}</span>
          </li>`).join("");
    } else if (this.tab === "stations") {
      const kind = this.typeFilter.stations;
      const nLines = (s) => this.stationLines.get(s.id)?.length ?? 0;
      const industries = new Map(d.industries.map((i) => [i.id, i]));
      html = (d.stations ?? []).filter((s) => (!kind || this.stationKey(s) === kind) && match(s.name))
        .sort((a, b) => nLines(b) - nLines(a) || String(a.name).localeCompare(String(b.name), locale()))
        .map((s) => {
          // Stacja zakładu: ikonka zakładu, a kliknięcie otwiera zakład (tam są też jego linie).
          const ind = industries.get(this.stationIndustry.get(s.id));
          const [k, id] = ind ? ["industry", ind.id] : ["station", s.id];
          return `
          <li class="row" data-kind="${k}" data-id="${id}" aria-current="${isSel(k, id)}">
            ${ind ? `<span class="dot dot-${industryKind(ind)}"></span>` : iconHtml(stationImg(s), s.kind)}
            <span class="row-name">${ind ? iconHtml(stationImg(s), s.kind) : ""} ${esc(s.name ?? ind?.name ?? stationName(s.kind))}</span>
            <span class="row-value">${esc(t("list.lines", { n: fmtInt(nLines(s)) }))}</span>
          </li>`;
        }).join("");
    } else {
      // Szukanie po nazwie linii albo po nazwach jej stacji; trafione stacje pokazujemy pod nazwą.
      // Z okiem: tylko linie na ekranie mapy (zaznaczona zostaje zawsze).
      const onMap = (l) => !this.visibleLines || this.visibleLines.has(l.id) || isSel("line", l.id);
      html = d.lines.filter(onMap)
        .map((l) => ({ l, stations: this.matchLine(l) }))
        .filter(({ stations }) => stations !== null)
        .sort((a, b) => (b.l.vehicleCount ?? 0) - (a.l.vehicleCount ?? 0))
        .map(({ l, stations }) => `
          <li class="row" data-kind="line" data-id="${l.id}" aria-current="${isSel("line", l.id)}">
            <span class="swatch" style="background:${rgb(l.color)}"></span>
            <span class="row-main">
              <span class="row-name">${carrierIcon(l.carrier, l.carrier)} ${esc(l.name)}</span>
              ${stations.length ? `<span class="row-hint">${viaHtml(stations, this.filter)}</span>` : ""}
            </span>
            <span class="row-value">${esc(t("list.lineStats", { v: fmtInt(l.vehicleCount), s: l.stops?.length ?? 0 }))}</span>
          </li>`).join("");
    }

    const empty = this.tab === "lines" && this.visibleLines ? "list.emptyView" : "list.empty";
    this.list.innerHTML = html || `<li class="empty">${esc(t(empty))}</li>`;
  }

  // Przyciski typów linii / rodzajów stacji: tylko typy obecne w danych, z liczbą pozycji.
  renderCarriers() {
    if (!(this.tab in this.typeFilter)) return;
    const stations = this.tab === "stations";
    const items = (stations ? this.dynamic?.stations : this.dynamic?.lines) ?? [];
    const keyOf = stations ? (s) => this.stationKey(s) : carrierKey;
    const order = stations ? STATION_ORDER : CARRIER_ORDER;
    const nameOf = stations ? stationName : carrierName;

    const counts = new Map();
    for (const x of items) counts.set(keyOf(x), (counts.get(keyOf(x)) ?? 0) + 1);
    const current = this.typeFilter[this.tab];
    if (current && !counts.has(current)) this.typeFilter[this.tab] = "";
    const btn = (key, label, n) =>
      `<button type="button" data-carrier="${key}" aria-pressed="${this.typeFilter[this.tab] === key}">${label}<span>${fmtInt(n)}</span></button>`;
    this.carriers.innerHTML = btn("", esc(t("filter.all")), items.length)
      + order.filter((c) => counts.has(c))
        .map((c) => btn(c, `${carrierIcon(c, c)} ${esc(nameOf(c) ?? t("filter.none"))}`, counts.get(c))).join("");
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
  return rows ? `<h3>${esc(t("lines"))}</h3><ul class="line-links">${rows}</ul>` : "";
}

function stationDetail(s, lines) {
  const use = stationUse(s);
  return `
    <h2>${iconHtml(stationImg(s), s.kind)} ${esc(s.name ?? stationName(s.kind))}</h2>
    <p class="detail-sub">${esc(stationName(s.kind) ?? t("station"))}${use ? ` · ${esc(use)}` : ""}</p>
    <dl class="stats">
      ${stat(t("lines"), fmtInt(lines.length))}
      ${stat(t("station.vehicles"), fmtInt(lines.reduce((n, l) => n + (l.vehicleCount ?? 0), 0)))}
    </dl>
    ${lineLinks(lines)}`;
}

function townDetail(town) {
  const caps = (town.capacities ?? [])
    .map((c, i) => stat(landUseName(i) ?? t("town.zone", { n: i + 1 }), fmtInt(c))).join("");
  const needs = (town.cargoNeeds ?? []).flatMap((group) => Object.entries(group ?? {}))
    .map(([cargo, v]) => `<li>${cargoLabel(cargo)}<span>${fmtDec(v)}</span></li>`).join("");
  return `
    <h2>${esc(town.name)}</h2>
    <p class="detail-sub">${esc(t("town"))} · ${esc(t(town.growing ? "town.growing" : "town.stopped"))}</p>
    <dl class="stats">
      ${caps}
      ${stat(t("town.noise"), fmtDec(town.noise))}
      ${stat(t("town.pollution"), fmtInt(town.pollution))}
    </dl>
    ${needs ? `<h3>${esc(t("town.demand"))}</h3><ul class="chips">${needs}</ul>` : ""}`;
}

function industryDetail(i, lines) {
  const outputs = industryOutputs(i);
  const produced = new Set(outputs.keys());
  const inputs = (i.stocks ?? []).filter((s) => !produced.has(s.cargo));

  return `
    <h2>${esc(i.name)}</h2>
    <p class="detail-sub">${esc(t(industryKind(i) === "raw" ? "industry.raw" : "industry.processing"))}</p>
    <dl class="stats">
      ${stat(t("industry.level"), `${i.level ?? "–"} / ${i.maxLevel || "–"}`)}
      ${stat(t("industry.productivity"), fmtPercent(i.productivity))}
      ${stat(t("industry.upgrade"), fmtPercent(i.upgrade))}
    </dl>
    ${inputs.length ? `<h3>${esc(t("industry.needs"))}</h3><ul class="chips">${inputs.map((s) => `<li>${cargoLabel(s.cargo)}</li>`).join("")}</ul>` : ""}
    ${outputs.size ? `<h3>${esc(t("industry.produces"))}</h3><ul class="chips">${[...outputs].map(([c, a]) => `<li>${cargoLabel(c)}<span>${fmtInt(a)}</span></li>`).join("")}</ul>` : ""}
    ${lineLinks(lines)}`;
}

// Stan "oka" zapamiętany w przeglądarce; domyślnie włączone.
const VIEWPORT_KEY = "tf3-lines-viewport";
function loadViewportOnly() {
  try { return localStorage.getItem(VIEWPORT_KEY) !== "0"; } catch { return true; }
}
function saveViewportOnly(on) {
  try { localStorage.setItem(VIEWPORT_KEY, on ? "1" : "0"); } catch { /* brak localStorage */ }
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

// "przez A, B" / "via A, B" z podświetloną frazą; {names} podstawiamy po escapowaniu reszty napisu.
function viaHtml(stations, filter) {
  const [before, after = ""] = t("list.via").split("{names}");
  return esc(before) + stations.map((s) => highlight(s, filter)).join(", ") + esc(after);
}

function highlight(text, filter) {
  const i = text.toLowerCase().indexOf(filter);
  if (i < 0) return esc(text);
  return esc(text.slice(0, i)) + `<mark>${esc(text.slice(i, i + filter.length))}</mark>` + esc(text.slice(i + filter.length));
}

// Przystanek z pozycją jest klikalny (data-stop = indeks na linii) - przenosi mapę do niego.
function lineDetail(l, focusedStop) {
  const stops = (l.stops ?? []).map((s, i) => {
    const name = esc(s.name ?? t("line.stop", { n: i + 1 }));
    if (!s.pos) return `<li>${name}</li>`;
    return `<li data-stop="${i}" aria-current="${focusedStop === i}"><button type="button">${name}</button></li>`;
  }).join("");
  return `
    <h2><span class="swatch" style="background:${rgb(l.color)}"></span> ${esc(l.name)}</h2>
    <p class="detail-sub">${carrierIcon(l.carrier, l.carrier)} ${esc(carrierName(l.carrier) ?? t("line"))}</p>
    <dl class="stats">
      ${stat(t("line.vehicles"), fmtInt(l.vehicleCount))}
      ${stat(t("line.stops"), fmtInt(l.stops?.length ?? 0))}
      ${l.stats ? lineStats(l.stats) : ""}
    </dl>
    ${l.stats?.cargo?.length ? `<h3>${esc(t("line.loadNow"))}</h3><ul class="chips">${l.stats.cargo.map((c) => `<li>${cargoLabel(c.cargo)}<span>${fmtInt(c.used)} / ${fmtInt(c.capacity)}</span></li>`).join("")}</ul>` : ""}
    ${stops ? `<h3>${esc(t("line.stops"))}</h3><ol class="stops">${stops}</ol>` : ""}`;
}

// Statystyki z moda, odświeżane raz na miesiąc gry (lua_mod: collectors.lineStats).
function lineStats(s) {
  const money = (n) => (n == null ? "–" : `<span class="${n < 0 ? "neg" : "pos"}">${esc(fmtMoney(n))}</span>`);
  return `
      ${stat(t("line.profit12m"), money(s.profit12m))}
      ${s.prevYear != null ? stat(t("line.profitYear", { year: s.prevYear }), money(s.profitPrevYear)) : ""}
      ${stat(t("line.transported12m"), fmtInt(s.transported12m))}
      ${stat(t("line.rate"), fmtInt(s.rate))}
      ${stat(t("line.load"), fmtPercent(s.load))}`;
}

const stat = (label, value) => `<div><dt>${esc(label)}</dt><dd>${value}</dd></div>`;
