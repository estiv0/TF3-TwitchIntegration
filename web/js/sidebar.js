// Panel boczny: listy (miasta, przemysł, stacje, linie, właściciele) i szczegóły zaznaczonego obiektu
// (szczegóły w osobnym, pływającym okienku nad mapą - element detail, js/float.js).

import {
  STATION_ORDER, carrierIcon, carrierName, cargoImg, cargoLabel, cargoName, escapeHtml as esc, iconHtml, landUseName, stationName,
  fmtDate, fmtDec, fmtInt, fmtMoney, fmtPercent, fmtSignedPercent, industryOfStation, linesByIndustry, linesByStation, rgb, stationImg,
  stationUse, townGrowth, townSize,
} from "./format.js";
import { companyHtml } from "./company.js";
import { townEarnings } from "./earnings.js";
import { locale, t } from "./i18n.js";
import { industryKind, industryOutputs } from "./map.js";
import { ownerKey, ownersIndex } from "./owners.js";

export class Sidebar {
  constructor(root, detail, { onPick, onStop, onClose, onLinesView, onHoverItem }) {
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
    this.company = root.querySelector("[data-company]");
    this.searchRow = root.querySelector(".search-row");
    this.detail = detail;
    this.search = root.querySelector("[data-search]");
    this.carriers = root.querySelector("[data-carriers]");
    this.viewportBtn = root.querySelector("[data-viewport]");
    this.typeFilter = { lines: "", stations: "" }; // filtr nośnika / rodzaju stacji, "" = wszystkie
    this.industryFilter = { mode: "out", cargo: "" }; // lista zakładów: produkuje ("out") / zużywa ("in") ładunek
    this.townSort = "size";                        // lista miast: "size" (wielkość), "profit" (zarobek linii), "growth" (wzrost)
    this.earnings = { towns: new Map(), outside: null }; // townEarnings()
    this.stationLines = new Map();                 // id stacji -> linie
    this.industryLines = new Map();                // id zakładu -> linie jego stacji
    this.stationIndustry = new Map();              // id stacji -> id zakładu (stacje przemysłu)
    this.owners = new Map();                       // klucz widza -> majątek (ownersIndex)
    this.ownerSort = "assets";                     // lista właścicieli: "assets" (majątek), "profit", "name"

    root.querySelectorAll("[data-tab]").forEach((btn) =>
      btn.addEventListener("click", () => this.showTab(btn.dataset.tab)));

    this.carriers.addEventListener("click", (e) => {
      const sort = e.target.closest("[data-sort]");
      if (sort) {
        if (this.tab === "owners") this.ownerSort = sort.dataset.sort;
        else this.townSort = sort.dataset.sort;
        this.renderCarriers();
        this.renderList();
        return;
      }
      const mode = e.target.closest("[data-ind-mode]");
      const cargo = e.target.closest("[data-ind-cargo]");
      if (mode || cargo) {
        if (mode) this.industryFilter.mode = mode.dataset.indMode;
        // ponowny klik w wybrany ładunek zdejmuje filtr
        else this.industryFilter.cargo = cargo.dataset.indCargo === this.industryFilter.cargo ? "" : cargo.dataset.indCargo;
        this.renderCarriers();
        this.renderList();
        return;
      }
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
      if (row) this.onPick(row.dataset.kind, selId(row.dataset.kind, row.dataset.id));
    });

    // Najechanie na pozycję listy (miasto, zakład, stacja, linia) podświetla ją na mapie.
    // Właściciel nie jest jednym punktem na mapie.
    this.list.addEventListener("mouseover", (e) => {
      const row = e.target.closest("[data-kind]");
      const kind = row && row.dataset.kind !== "owner" ? row.dataset.kind : null;
      onHoverItem?.(kind, kind ? Number(row.dataset.id) : null);
    });
    this.list.addEventListener("mouseleave", () => onHoverItem?.(null));

    // Tak samo linie w szczegółach (miasta, stacji, zakładu): wskazana linia na mapie na tle zaznaczonego obiektu.
    // W szczegółach właściciela tak samo jego miasta, zakłady i stacje (data-pick).
    this.detail.addEventListener("mouseover", (e) => {
      const line = e.target.closest("[data-line]");
      if (line) return onHoverItem?.("line", Number(line.dataset.line));
      const [kind, id] = splitPick(e.target.closest("[data-pick]"));
      const onMap = kind && kind !== "owner";
      onHoverItem?.(onMap ? kind : null, onMap ? Number(id) : null);
    });
    this.detail.addEventListener("mouseleave", () => onHoverItem?.(null));

    this.detail.addEventListener("click", (e) => {
      if (e.target.closest("[data-close]")) {
        onClose?.();
        return;
      }
      const copy = e.target.closest("[data-copy]");
      if (copy) {
        copyName(copy);
        return;
      }
      const line = e.target.closest("[data-line]");
      if (line) {
        this.onPick("line", Number(line.dataset.line));
        return;
      }
      // Właściciel w szczegółach obiektu i obiekty w szczegółach właściciela.
      const [kind, id] = splitPick(e.target.closest("[data-pick]"));
      if (kind) {
        this.onPick(kind, selId(kind, id));
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

  // Przełącza zakładkę (też z zewnątrz: klik w saldo w górnym pasku otwiera Firmę).
  showTab(tab) {
    this.tab = tab;
    this.root.querySelectorAll("[data-tab]").forEach((b) => {
      b.setAttribute("aria-selected", String(b.dataset.tab === tab));
      if (b.dataset.tab === tab) scrollTabIntoView(b);
    });
    this.renderSearch();
    this.renderCarriers();
    this.emitLinesView();
    this.renderList();
  }

  setDynamic(dynamic) {
    this.dynamic = dynamic;
    this.stationLines = linesByStation(dynamic);
    this.stationIndustry = industryOfStation(dynamic);
    this.industryLines = linesByIndustry(dynamic, this.stationLines, this.stationIndustry);
    this.earnings = townEarnings(dynamic);
    this.owners = ownersIndex(dynamic, this.stationIndustry);
    this.root.querySelector('[data-count="towns"]').textContent = dynamic.towns?.length ?? 0;
    this.root.querySelector('[data-count="industries"]').textContent = dynamic.industries?.length ?? 0;
    this.root.querySelector('[data-count="stations"]').textContent = dynamic.stations?.length ?? 0;
    this.root.querySelector('[data-count="lines"]').textContent = dynamic.lines?.length ?? 0;
    this.root.querySelector('[data-count="owners"]').textContent = this.owners.size;
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

  // Linia przechodzi filtry typu i szukania -> nazwy trafionych stacji (puste, gdy trafiła nazwa albo właściciel linii); inaczej null.
  matchLine(l) {
    const carrier = this.typeFilter.lines;
    if (carrier && carrierKey(l) !== carrier) return null;
    const has = (text) => String(text ?? "").toLowerCase().includes(this.filter);
    if (!this.filter || has(l.name) || (l.owner && has(l.owner))) return [];
    const stations = matchingStations(l, this.filter);
    return stations.length ? stations : null;
  }

  setSelected(kind, id) {
    const changed = this.selected?.kind !== kind || this.selected?.id !== id;
    this.selected = kind ? { kind, id } : null;
    this.focusedStop = null;
    this.renderList();
    this.renderDetail();
    if (changed) this.detail.scrollTop = 0; // nowy obiekt - okienko od góry
  }

  // Wszystko, co zależy od danych albo języka (po zmianie języka wołane z main.js).
  render() {
    this.renderSearch();
    this.renderCarriers();
    this.renderList();
    this.renderDetail();
  }

  renderSearch() {
    this.searchRow.hidden = this.tab === "company";
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
    const matchItem = (x) => match(x.name) || (x.owner && match(x.owner));
    const isSel = (kind, id) => this.selected?.kind === kind && this.selected?.id === id;
    let html = "";

    // Firma: zamiast listy osobna sekcja (tabela finansów, wykresy).
    const company = this.tab === "company";
    this.list.hidden = company;
    this.company.hidden = !company;
    if (company) {
      this.company.innerHTML = companyHtml(d.company);
      return;
    }

    if (this.tab === "towns") {
      // Sortowanie po zarobku: miasta bez linii na końcu, a pod listą linie poza miastami.
      const byProfit = this.townSort === "profit", byGrowth = this.townSort === "growth";
      const profit = (x) => this.earnings.towns.get(x.id)?.profit12m ?? null;
      const order = byProfit
        ? (a, b) => (profit(b) ?? -Infinity) - (profit(a) ?? -Infinity) || townSize(b) - townSize(a)
        : byGrowth
          ? (a, b) => (townGrowth(b) ?? -Infinity) - (townGrowth(a) ?? -Infinity) || townSize(b) - townSize(a)
          : (a, b) => townSize(b) - townSize(a);
      const value = (x) => (byProfit ? money(profit(x)) : byGrowth ? growthHtml(townGrowth(x)) : fmtInt(townSize(x)));
      html = [...d.towns].filter(matchItem).sort(order)
        .map((x) => `
          <li class="row" data-kind="town" data-id="${x.id}" aria-current="${isSel("town", x.id)}">
            <span class="dot dot-town"></span>
            <span class="row-name">${esc(x.name)}</span>
            ${effectBadges(x)}
            <span class="row-value">${value(x)}</span>
          </li>`).join("");
      const outside = this.earnings.outside;
      if (byProfit && !this.filter && outside?.lines.length) {
        html += `
          <li class="row row-static" title="${esc(t("town.outsideHint"))}">
            <span class="dot"></span>
            <span class="row-name">${esc(t("town.outside"))}</span>
            <span class="row-value">${money(outside.profit12m)}</span>
          </li>`;
      }
    } else if (this.tab === "owners") {
      // Szukanie po nazwie widza i po nazwach jego obiektów ("Gdańsk" pokaże właściciela Gdańska).
      const matchOwner = (o) => match(o.name)
        || [o.towns, o.industries, o.stations, o.lines].some((xs) => xs.some((x) => match(x.name)));
      const byName = (a, b) => a.name.localeCompare(b.name, locale());
      const order = this.ownerSort === "profit"
        ? (a, b) => (b.profit12m ?? -Infinity) - (a.profit12m ?? -Infinity) || b.score - a.score
        : this.ownerSort === "name" ? byName : (a, b) => b.score - a.score || byName(a, b);
      html = [...this.owners.values()].filter(matchOwner).sort(order)
        .map((o) => `
          <li class="row" data-kind="owner" data-id="${esc(o.key)}" aria-current="${isSel("owner", o.key)}">
            <span class="row-name">${esc(o.name)}</span>
            ${assetCounts(o)}
            <span class="row-value">${this.ownerSort === "profit" ? money(o.profit12m) : ""}</span>
          </li>`).join("");
    } else if (this.tab === "industries") {
      const { mode, cargo } = this.industryFilter;
      const hasCargo = (i) => !cargo || industryCargos(i, mode).has(cargo);
      html = [...d.industries].filter((i) => hasCargo(i) && matchItem(i)).sort((a, b) => (b.level ?? 0) - (a.level ?? 0))
        .map((i) => `
          <li class="row" data-kind="industry" data-id="${i.id}" aria-current="${isSel("industry", i.id)}">
            ${industryBadge(i)}
            <span class="row-name">${esc(i.name)}</span>
            ${effectBadges(i)}
            <span class="row-value">${esc(t("list.level", { n: i.level ?? "–" }))}</span>
          </li>`).join("");
    } else if (this.tab === "stations") {
      const kind = this.typeFilter.stations;
      const nLines = (s) => this.stationLines.get(s.id)?.length ?? 0;
      const industries = new Map(d.industries.map((i) => [i.id, i]));
      html = (d.stations ?? []).filter((s) => (!kind || this.stationKey(s) === kind) && matchItem(s))
        .sort((a, b) => nLines(b) - nLines(a) || String(a.name).localeCompare(String(b.name), locale()))
        .map((s) => {
          // Stacja zakładu: ikonka zakładu, a kliknięcie otwiera zakład (tam są też jego linie).
          const ind = industries.get(this.stationIndustry.get(s.id));
          const [k, id] = ind ? ["industry", ind.id] : ["station", s.id];
          return `
          <li class="row" data-kind="${k}" data-id="${id}" aria-current="${isSel(k, id)}">
            ${ind ? industryBadge(ind) : iconHtml(stationImg(s), s.kind)}
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
            ${effectBadges(l)}
            <span class="row-value">${esc(t("list.lineStats", { v: fmtInt(l.vehicleCount), s: l.stops?.length ?? 0 }))}</span>
          </li>`).join("");
    }

    const empty = this.tab === "lines" && this.visibleLines ? "list.emptyView"
      : this.tab === "owners" && !this.owners.size ? "owners.empty" : "list.empty";
    this.list.innerHTML = html || `<li class="empty">${esc(t(empty))}</li>`;
  }

  // Przyciski typów linii / rodzajów stacji: tylko typy obecne w danych, z liczbą pozycji.
  renderCarriers() {
    this.carriers.hidden = !(this.tab in this.typeFilter || ["towns", "industries", "owners"].includes(this.tab));
    if (this.tab === "industries") {
      this.carriers.innerHTML = this.industryFilterHtml();
      return;
    }
    if (this.tab === "towns") {
      const btn = (key) =>
        `<button type="button" data-sort="${key}" aria-pressed="${this.townSort === key}">${esc(t(`town.sort.${key}`))}</button>`;
      this.carriers.innerHTML = btn("size") + btn("profit") + btn("growth");
      return;
    }
    if (this.tab === "owners") {
      const btn = (key) =>
        `<button type="button" data-sort="${key}" aria-pressed="${this.ownerSort === key}">${esc(t(`owners.sort.${key}`))}</button>`;
      this.carriers.innerHTML = btn("assets") + btn("profit") + btn("name");
      return;
    }
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

  // Filtr zakładów: tryb (produkcja / zużycie) i jeden ładunek z liczbą zakładów, które go produkują / zużywają.
  industryFilterHtml() {
    const f = this.industryFilter;
    const counts = new Map();
    for (const i of this.dynamic?.industries ?? [])
      for (const c of industryCargos(i, f.mode)) counts.set(c, (counts.get(c) ?? 0) + 1);
    if (f.cargo && !counts.has(f.cargo)) counts.set(f.cargo, 0); // wybrany ładunek zostaje po zmianie trybu
    const modeBtn = (key) =>
      `<button type="button" data-ind-mode="${key}" aria-pressed="${f.mode === key}">${esc(t(key === "out" ? "industry.filterOut" : "industry.filterIn"))}</button>`;
    const cargoBtn = ([c, n]) => {
      const src = cargoImg(c);
      const label = src ? `<img class="cargo-ico" src="${src}" alt="" onerror="this.remove()">` : esc(cargoName(c));
      return `<button type="button" class="cargo-btn" data-ind-cargo="${esc(c)}" aria-pressed="${f.cargo === c}"`
        + ` title="${esc(cargoName(c))}" aria-label="${esc(cargoName(c))}">${label}<span>${fmtInt(n)}</span></button>`;
    };
    const cargos = [...counts].sort(([a], [b]) => cargoName(a).localeCompare(cargoName(b), locale()));
    const all = `<button type="button" data-ind-cargo="" aria-pressed="${!f.cargo}">${esc(t("filter.all"))}`
      + `<span>${fmtInt(this.dynamic?.industries?.length ?? 0)}</span></button>`;
    return `<div class="filter-modes">${modeBtn("out")}${modeBtn("in")}</div>${all}${cargos.map(cargoBtn).join("")}`;
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
    const item = sel.kind === "owner" ? this.owners.get(sel.id) : list.find((x) => x.id === sel.id);
    if (!item) {
      this.detail.hidden = true;
      return;
    }
    this.detail.hidden = false;
    const close = `<button type="button" class="detail-close" data-close title="${esc(t("detail.close"))}" aria-label="${esc(t("detail.close"))}">×</button>`;
    this.detail.innerHTML = close + (sel.kind === "owner" ? ownerDetail(item, this.stationIndustry)
      : sel.kind === "town" ? townDetail(item, this.earnings.towns.get(item.id))
      : sel.kind === "line" ? lineDetail(item, this.focusedStop)
      : sel.kind === "station" ? stationDetail(item, this.stationLines.get(item.id) ?? [])
      : industryDetail(item, this.industryLines.get(item.id) ?? []));
  }
}

// Przycisk kopiowania nazwy przy nagłówku (nagłówek służy do przeciągania okienka, więc tekstu nie da się w nim zaznaczyć).
function copyBtn(name) {
  if (!name) return "";
  return `<button type="button" class="detail-copy" data-copy="${esc(name)}" title="${esc(t("detail.copy"))}" aria-label="${esc(t("detail.copy"))}">⧉</button>`;
}

async function copyName(btn) {
  const text = btn.dataset.copy;
  let ok = false;
  try {
    await navigator.clipboard.writeText(text);
    ok = true;
  } catch {
    // bez Clipboard API (np. http bez localhost): stary sposób przez ukryte pole tekstowe
    const ta = document.createElement("textarea");
    ta.value = text;
    ta.style.cssText = "position:fixed;opacity:0";
    document.body.append(ta);
    ta.select();
    try { ok = document.execCommand("copy"); } catch { /* nic */ }
    ta.remove();
  }
  btn.textContent = ok ? "✓" : "✕";
  btn.title = t(ok ? "detail.copied" : "detail.copyFailed");
  setTimeout(() => {
    if (!btn.isConnected) return;
    btn.textContent = "⧉";
    btn.title = t("detail.copy");
  }, 1500);
}

// Pasek zakładek przewija się w poziomie (wąski panel, telefon): wybrana zakładka cała w widoku.
// Bez scrollIntoView, bo ten przesuwa też stronę, gdy jest szersza od ekranu.
function scrollTabIntoView(btn) {
  const bar = btn.parentElement;
  const pad = parseFloat(getComputedStyle(bar).paddingLeft) || 0;
  const left = btn.offsetLeft - bar.offsetLeft - pad, right = btn.offsetLeft - bar.offsetLeft + btn.offsetWidth + pad;
  if (left < bar.scrollLeft) bar.scrollLeft = left;
  else if (right > bar.scrollLeft + bar.clientWidth) bar.scrollLeft = right - bar.clientWidth;
}

// Zaznaczenie z listy albo linku: właściciel ma klucz tekstowy, reszta liczbowe id.
const selId = (kind, id) => (kind === "owner" ? id : Number(id));

// data-pick="rodzaj:id" (klucz właściciela może zawierać ":") -> [rodzaj, id] albo [].
function splitPick(el) {
  const v = el?.dataset.pick ?? "";
  const i = v.indexOf(":");
  return i > 0 ? [v.slice(0, i), v.slice(i + 1)] : [];
}

// Właściciel z nazwy w grze ("Nazwa | Właściciel", splitAllOwners) i efekty od widzów (attachEffects).
// Nazwa właściciela otwiera jego majątek.
function ownerHtml(x) {
  const owner = x.owner
    ? `${esc(t("owner"))}: <button type="button" class="owner-link" data-pick="owner:${esc(ownerKey(x.owner))}" title="${esc(t("owner.show"))}">${esc(x.owner)}</button>`
    : esc(t("owner.none"));
  return `<p class="detail-owner">${owner}</p>` + effectsHtml(x);
}

// Liczniki obiektów widza na liście (tylko niezerowe).
function assetCounts(o) {
  const n = (count, icon, label) =>
    (count ? `<span class="own-count" title="${esc(label)}">${icon}${fmtInt(count)}</span>` : "");
  return `<span class="own-counts">`
    + n(o.towns.length, `<span class="dot dot-town"></span>`, t("tab.towns"))
    + n(o.industries.length, `<span class="dot dot-processing"></span>`, t("tab.industries"))
    + n(o.stations.length, iconHtml(stationImg({ kind: "RAIL" }), "RAIL"), t("tab.stations"))
    + n(o.lines.length, `<span class="swatch own-line"></span>`, t("tab.lines"))
    + n(o.fleet.total, `<span class="dot dot-vehicle"></span>`, t("line.vehicles"))
    + `</span>`;
}

// Pojazdy na liniach widza: "Pojazdy (N)", podział według nośnika i stanu (owners.js: fleet).
function ownerFleetHtml(o) {
  const f = o.fleet;
  if (!o.lines.length || !f.total) return "";
  const carriers = f.byCarrier?.size
    ? `<p class="own-fleet">${[...f.byCarrier].sort((a, b) => b[1] - a[1]).map(([c, n]) =>
      `<span title="${esc(carrierName(c) ?? c)}">${carrierIcon(c, c) || "•"} ${fmtInt(n)}</span>`).join("")}</p>`
    : "";
  const states = f.byCarrier
    ? `<dl class="stats">${stat(t("owners.enRoute"), fmtInt(f.enRoute)) + stat(t("owners.atStop"), fmtInt(f.atStop))
      + (f.stopped ? stat(t("owners.stopped"), `<span class="neg">${fmtInt(f.stopped)}</span>`) : "")}</dl>`
    : "";
  return `<h3>${esc(t("owners.vehicles", { n: fmtInt(f.total) }))}</h3>${carriers}${states}`;
}

// Linie widza: statystyki wszystkich razem (owners.js: lineStats), lista linii i ładunek w pojazdach teraz.
function ownerLinesHtml(o) {
  if (!o.lines.length) return "";
  const s = o.lineStats;
  const rows = o.lines.map((l) => `
    <li><button type="button" data-line="${l.id}">
      <span class="swatch" style="background:${rgb(l.color)}"></span>${carrierIcon(l.carrier, l.carrier)}
      <span class="link-name">${esc(l.name)}</span>${effectBadges(l)}
      <span class="link-value">${money(l.stats?.profit12m)}</span>
    </button></li>`).join("");
  const cargo = s?.cargo.length
    ? `<h3>${esc(t("line.loadNow"))}</h3><ul class="chips">${s.cargo.map((c) =>
      `<li>${cargoLabel(c.cargo)}<span>${fmtInt(c.used)} / ${fmtInt(c.capacity)}</span></li>`).join("")}</ul>`
    : "";
  return `
    <h3 title="${esc(t("owners.linesHint"))}">${esc(t("tab.lines"))}</h3>
    ${s ? `<dl class="stats">${lineStats(s)}</dl>` : ""}
    <ul class="line-links own-lines">${rows}</ul>
    ${cargo}`;
}

// Majątek widza: podsumowanie i klikalne obiekty (data-pick, linie: data-line); główne (największe) miasto pierwsze.
function ownerDetail(o, stationIndustry) {
  const link = (pick, name, icon, value = "", x = null) => `
    <li><button type="button" data-pick="${pick}">
      ${icon}<span class="link-name">${esc(name)}</span>${x ? effectBadges(x) : ""}
      <span class="link-value">${value}</span>
    </button></li>`;
  const section = (title, rows) => (rows ? `<h3>${esc(title)}</h3><ul class="line-links">${rows}</ul>` : "");
  const main = `<span class="own-main" title="${esc(t("owners.main"))}">★</span> `;

  const towns = o.towns.map((x) =>
    link(`town:${x.id}`, x.name, `<span class="dot dot-town"></span>`, (x === o.main ? main : "") + fmtInt(townSize(x)), x)).join("");
  const industries = o.industries.map((x) =>
    link(`industry:${x.id}`, x.name, industryBadge(x), esc(t("list.level", { n: x.level ?? "–" })), x)).join("");
  // Stacja zakładu (innego widza albo bez właściciela) otwiera zakład, jak na liście stacji.
  const stations = o.stations.map((s) => {
    const ind = stationIndustry.get(s.id);
    return link(ind != null ? `industry:${ind}` : `station:${s.id}`, s.name ?? stationName(s.kind), iconHtml(stationImg(s), s.kind));
  }).join("");

  const mainTown = o.main
    ? ` · ${esc(t("owners.main"))}: <button type="button" class="owner-link" data-pick="town:${o.main.id}">${esc(o.main.name)}</button>`
    : "";
  return `
    <h2>${esc(o.name)}${copyBtn(o.name)}</h2>
    <p class="detail-sub">${esc(t("owner"))}${mainTown}</p>
    <dl class="stats">
      ${o.towns.length ? stat(t("tab.towns"), fmtInt(o.towns.length)) + stat(t("owners.size"), fmtInt(o.size)) : ""}
      ${o.industries.length ? stat(t("tab.industries"), fmtInt(o.industries.length)) : ""}
      ${o.stations.length ? stat(t("tab.stations"), fmtInt(o.stations.length)) : ""}
      ${o.lines.length ? stat(t("tab.lines"), fmtInt(o.lines.length)) : ""}
    </dl>
    ${section(t("tab.towns"), towns)}
    ${section(t("tab.industries"), industries)}
    ${section(t("tab.stations"), stations)}
    ${ownerFleetHtml(o)}
    ${ownerLinesHtml(o)}`;
}

// Nazwa efektu z siłą: "Boost miasta +50%", "Strajk −99%".
function effectName(e) {
  const name = t(`fx.${e.type}`);
  if (!e.factor) return name;
  const p = Math.round((e.factor - 1) * 100);
  return `${name} ${p >= 0 ? "+" : "−"}${Math.abs(p)}%`;
}

function effectTip(e) {
  return [effectName(e), e.until ? t("fx.until", { date: fmtDate(e.until) }) : "", e.user ? t("fx.by", { user: e.user }) : ""]
    .filter(Boolean).join(" · ");
}

// Znaczki na liście (miasta, zakłady, linie): ⚡ boost, ⏸ czeka na stacjach, ⛔ zatrzymana,
// ✊ strajk, 🔍 skarbówka, ⛽ paliwo premium, 💧 chrzczone paliwo.
const EFFECT_ICON = {
  townBoost: "⚡", industryBoost: "⚡", lineStop: "⛔", lineHold: "⏸",
  industryStrike: "✊", industryTaxRaid: "🔍", lineFuelPremium: "⛽", lineFuelBad: "💧",
};
function effectBadges(x) {
  return (x.effects ?? []).map((e) =>
    `<span class="fx-badge fx-${esc(e.type)}" title="${esc(effectTip(e))}">${EFFECT_ICON[e.type] ?? "★"}</span>`).join("");
}

// W szczegółach: każdy efekt z datą końca w grze, kupującym widzem i paskiem upływu czasu.
function effectsHtml(x) {
  if (!x.effects?.length) return "";
  const rows = x.effects.map((e) => `
    <li class="fx-${esc(e.type)}">
      <span class="fx-name">${EFFECT_ICON[e.type] ?? "★"} ${esc(effectName(e))}</span>
      <span class="fx-meta">${esc([e.until ? t("fx.until", { date: fmtDate(e.until) }) : "", e.user ? t("fx.by", { user: e.user }) : ""].filter(Boolean).join(" · "))}</span>
      ${e.progress != null ? `<i class="fx-bar" title="${esc(t("fx.left", { p: Math.round((1 - e.progress) * 100) }))}"><b style="width:${((1 - e.progress) * 100).toFixed(0)}%"></b></i>` : ""}
    </li>`).join("");
  return `<h3 title="${esc(t("fx.hint"))}">${esc(t("fx.title"))}</h3><ul class="effects">${rows}</ul>`;
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
    <h2>${iconHtml(stationImg(s), s.kind)} ${esc(s.name ?? stationName(s.kind))}${copyBtn(s.name ?? stationName(s.kind))}</h2>
    <p class="detail-sub">${esc(stationName(s.kind) ?? t("station"))}${use ? ` · ${esc(use)}` : ""}</p>
    ${ownerHtml(s)}
    <dl class="stats">
      ${stat(t("lines"), fmtInt(lines.length))}
      ${stat(t("station.vehicles"), fmtInt(lines.some((l) => l.vehicleCount == null) ? null : lines.reduce((n, l) => n + l.vehicleCount, 0)))}
    </dl>
    ${lineLinks(lines)}`;
}

// Zysk linii przypisany miastu (townEarnings): sumy i linie z udziałem miasta w ich przystankach.
function townEarningsHtml(e) {
  if (!e?.lines.length) return "";
  const rows = e.lines.map(({ line: l, share, profit12m }) => `
    <li><button type="button" data-line="${l.id}">
      <span class="swatch" style="background:${rgb(l.color)}"></span>${carrierIcon(l.carrier, l.carrier)}
      <span class="link-name">${esc(l.name)}</span>
      <span class="link-value">${fmtPercent(share)} · ${money(profit12m)}</span>
    </button></li>`).join("");
  return `
    <h3 title="${esc(t("town.earningsHint"))}">${esc(t("town.earnings"))}</h3>
    <dl class="stats">
      ${stat(t("line.profit12m"), money(e.profit12m))}
      ${e.prevYear != null ? stat(t("line.profitYear", { year: e.prevYear }), money(e.profitPrevYear)) : ""}
    </dl>
    <ul class="line-links">${rows}</ul>`;
}

// Wzrost ze znakiem, w kolorze ("+12%" zielony, "-3%" czerwony); brak danych -> "–".
const growthHtml = (g) => (g == null ? "–" : `<span class="${g < 0 ? "neg" : g > 0 ? "pos" : ""}">${esc(fmtSignedPercent(g))}</span>`);

// Zaspokojenie potrzeb (town.supply z moda, jak statystyki miast w grze): ładunki z limitem > 0
// albo bez limitu (-1), ale dostarczane; coverage = suma min(dostawa, limit) / suma limitów.
export function townSupply(town) {
  const list = (town.supply ?? []).filter((s) => s.demand > 0 || (s.demand === -1 && s.supply > 0));
  if (!list.length) return null;
  let supplied = 0, demand = 0;
  for (const s of list) {
    if (s.demand > 0) {
      supplied += Math.min(s.supply, s.demand);
      demand += s.demand;
    }
  }
  return { list, coverage: demand ? supplied / demand : null };
}

function townDetail(town, earnings) {
  const caps = (town.capacities ?? [])
    .map((c, i) => stat(landUseName(i) ?? t("town.zone", { n: i + 1 }), fmtInt(c))).join("");
  const supply = townSupply(town);
  let needs;
  if (supply) {
    needs = `<h3 title="${esc(t("town.supplyHint"))}">${esc(t("town.supply"))}</h3><ul class="chips">${supply.list.map((s) => {
      const v = s.demand > 0
        ? `${fmtInt(Math.min(s.supply, s.demand))} / ${fmtInt(s.demand)} · ${fmtPercent(Math.min(s.supply, s.demand) / s.demand)}`
        : fmtInt(s.supply);
      return `<li>${cargoLabel(s.cargo)}<span>${esc(v)}</span></li>`;
    }).join("")}</ul>`;
  } else {
    const chips = (town.cargoNeeds ?? []).flatMap((group) => Object.entries(group ?? {}))
      .map(([cargo, v]) => `<li>${cargoLabel(cargo)}<span>${fmtDec(v)}</span></li>`).join("");
    needs = chips ? `<h3>${esc(t("town.demand"))}</h3><ul class="chips">${chips}</ul>` : "";
  }
  return `
    <h2>${esc(town.name)}${copyBtn(town.name)}</h2>
    <p class="detail-sub">${esc(t("town"))} · ${esc(t(town.growing ? "town.growing" : "town.stopped"))}</p>
    ${ownerHtml(town)}
    <dl class="stats">
      ${caps}
      ${stat(t("town.growth"), growthHtml(townGrowth(town)))}
      ${supply?.coverage != null ? stat(t("town.supply"), fmtPercent(supply.coverage)) : ""}
      ${stat(t("town.noise"), fmtDec(town.noise))}
      ${stat(t("town.pollution"), fmtInt(town.pollution))}
    </dl>
    ${needs}
    ${townEarningsHtml(earnings)}`;
}

// Zakład: zapotrzebowanie i produkcja (na rok, gdy mod je podaje - stocks[].consumedYear,
// producedYear), stan magazynów (stocks[].amount) i liczniki z ostatniego roku (year).
function industryDetail(i, lines) {
  const outputs = industryOutputs(i);
  const produced = new Set(outputs.keys());
  const stocks = i.stocks ?? [];
  const isInput = (s) => isInputStock(s, produced);
  const inputs = stocks.filter(isInput);
  const perYear = (n) => (n == null ? "" : `<span>${esc(t("industry.perYear", { n: fmtInt(n) }))}</span>`);
  const yearOut = i.producedYear ?? {};
  const hasYear = Object.keys(yearOut).length > 0;

  const storage = stocks.some((s) => s.amount != null)
    ? `<h3>${esc(t("industry.stock"))}</h3><ul class="stock">${stocks.map((s) => {
      const fill = s.capacity ? Math.min(1, (s.amount ?? 0) / s.capacity) : 0;
      return `<li><span class="stock-name">${cargoLabel(s.cargo)}</span><em>${esc(t(isInput(s) ? "industry.input" : "industry.output"))}</em>`
        + `<span>${fmtInt(s.amount)} / ${fmtInt(s.capacity)}</span>`
        + `<i class="stock-bar"><b style="width:${(fill * 100).toFixed(0)}%"></b></i></li>`;
    }).join("")}</ul>`
    : "";
  const y = i.year ?? {};
  const year = y.produced != null || y.consumed != null || y.destroyed != null
    ? `<h3>${esc(t("industry.year"))}</h3><dl class="stats">
      ${y.consumed != null ? stat(t("industry.consumed"), fmtInt(y.consumed)) : ""}
      ${y.produced != null ? stat(t("industry.produced"), fmtInt(y.produced)) : ""}
      ${y.destroyed != null ? stat(t("industry.destroyed"), `<span class="${y.destroyed > 0 ? "neg" : ""}">${fmtInt(y.destroyed)}</span>`) : ""}
    </dl>`
    : "";

  return `
    <h2>${esc(i.name)}${copyBtn(i.name)}</h2>
    <p class="detail-sub">${esc(t(industryKind(i) === "raw" ? "industry.raw" : "industry.processing"))}</p>
    ${ownerHtml(i)}
    <dl class="stats">
      ${stat(t("industry.level"), `${i.level ?? "–"} / ${i.maxLevel || "–"}`)}
      ${stat(t("industry.productivity"), fmtPercent(i.productivity))}
      ${stat(t("industry.upgrade"), fmtPercent(i.upgrade))}
    </dl>
    ${inputs.length ? `<h3>${esc(t("industry.needs"))}</h3><ul class="chips">${inputs.map((s) => `<li>${cargoLabel(s.cargo)}${perYear(s.consumedYear)}</li>`).join("")}</ul>` : ""}
    ${outputs.size ? `<h3>${esc(t("industry.produces"))}</h3><ul class="chips">${[...outputs].map(([c, a]) =>
      `<li>${cargoLabel(c)}${hasYear ? perYear(yearOut[c] ?? 0) : `<span>${fmtInt(a)}</span>`}</li>`).join("")}</ul>` : ""}
    ${storage}
    ${year}
    ${lineLinks(lines)}`;
}

// Starszy eksport nie ma flagi input: wejściem jest magazyn ładunku, którego zakład nie produkuje.
const isInputStock = (s, produced) => s.input ?? !produced.has(s.cargo);

/** Ładunki, które zakład produkuje (mode "out") albo zużywa (mode "in"). */
function industryCargos(i, mode) {
  const produced = new Set(industryOutputs(i).keys());
  if (mode === "out") return produced;
  return new Set((i.stocks ?? []).filter((s) => isInputStock(s, produced)).map((s) => s.cargo));
}

// Kwadracik zakładu w kolorze rodzaju z ikonkami produkowanych ładunków (jak ikonka rodzaju przy stacjach).
function industryBadge(i) {
  const imgs = [...industryOutputs(i).keys()].map(cargoImg).filter(Boolean)
    .map((src) => `<img src="${src}" alt="" onerror="this.remove()">`).join("");
  return imgs ? `<span class="ind-badge ind-${industryKind(i)}" aria-hidden="true">${imgs}</span>`
    : `<span class="dot dot-${industryKind(i)}"></span>`;
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
    <h2><span class="swatch" style="background:${rgb(l.color)}"></span> ${esc(l.name)}${copyBtn(l.name)}</h2>
    <p class="detail-sub">${carrierIcon(l.carrier, l.carrier)} ${esc(carrierName(l.carrier) ?? t("line"))}</p>
    ${ownerHtml(l)}
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
  return `
      ${stat(t("line.profit12m"), money(s.profit12m))}
      ${s.prevYear != null ? stat(t("line.profitYear", { year: s.prevYear }), money(s.profitPrevYear)) : ""}
      ${s.profitTotal != null ? stat(t(s.profitTotal < 0 ? "line.lossTotal" : "line.profitTotal", { year: s.prevYear }), money(s.profitTotal)) : ""}
      ${stat(t("line.transported12m"), fmtInt(s.transported12m))}
      ${stat(t("line.rate"), fmtInt(s.rate))}
      ${stat(t("line.load"), fmtPercent(s.load))}`;
}

// Kwota w kolorze: zysk na zielono, strata na czerwono; brak danych -> "–".
const money = (n) => (n == null ? "–" : `<span class="${n < 0 ? "neg" : "pos"}">${esc(fmtMoney(Math.round(n)))}</span>`);

const stat = (label, value) => `<div><dt>${esc(label)}</dt><dd>${value}</dd></div>`;
