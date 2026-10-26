// Side panel: lists (towns, industry, stations, lines, owners) and the details of the selected object
// (the details in a separate floating window over the map - the detail element, js/float.js).

import {
  STATION_ORDER, cargoIcon, carrierIcon, carrierName, cargoImg, cargoLabel, cargoName, escapeHtml as esc, iconHtml, landUseName, stationName,
  fmtAge, fmtDate, vehicleImgHtml, fmtDec, fmtInt, fmtMoney, fmtMoneyShort, fmtPercent, fmtSignedPercent, industryOfStation, linesByIndustry, linesByStation, rgb, stationImg, warehouseOfStation,
  lineWaitingAt, stationUse, townGrowth, townSize,
} from "./format.js";
import { companyHtml } from "./company.js";
import { townEarnings } from "./earnings.js";
import { locale, t } from "./i18n.js";
import { industryInputs, industryKind, industryOutputs, isInputStock } from "./map.js";
import { ownerKey, ownersIndex } from "./owners.js";

export class Sidebar {
  constructor(root, detail, { windows, onPick, onStop, onClose, onPins, onLinesView, onHoverItem, onHoverCargo, onShowList }) {
    this.root = root;
    this.windows = windows;         // FloatingWindows (js/float.js): positions of the detail windows
    this.onPick = onPick;
    this.onShowList = onShowList;   // the panel opened a list by itself (phone: slide the panel out)
    this.onStop = onStop;
    this.onPins = onPins;           // a change of pinned objects -> the map highlights them
    this.onLinesView = onLinesView; // { match } or null - what the map should draw on the Lines tab
    this.viewportOnly = loadViewportOnly(); // the "eye": lines on the map and in the list only from the map view
    this.visibleLines = null;               // Set of line ids on the map screen (from the map), null = no filter
    this.tab = "towns";
    this.filter = "";
    this.dynamic = null;
    this.selected = null;
    // Pinned detail windows ({ kind, id }): they stay open while other objects are selected.
    this.pinned = [];
    this.collapsed = new Set();
    this.cardHtml = new WeakMap(); // window -> the last content written (the same = no redraw)  // keys ("kind:id") of pinned windows collapsed to the header
    this.focusedStop = null;    // { line, index } - the line stop the map was moved to

    this.list = root.querySelector("[data-list]");
    this.company = root.querySelector("[data-company]");
    this.searchRow = root.querySelector(".search-row");
    this.detail = detail;
    this.search = root.querySelector("[data-search]");
    this.carriers = root.querySelector("[data-carriers]");
    this.viewportBtn = root.querySelector("[data-viewport]");
    this.typeFilter = { lines: "", stations: "" }; // carrier / station kind filter, "" = all
    this.industryFilter = { mode: "out", cargo: "" }; // industry list: produces ("out") / consumes ("in") a cargo
    this.industryOrigin = null;                    // { kind, id }: industry list by distance from it (a cargo clicked in its details)
    this.townSort = "size";                        // town list: "size", "profit" (line earnings), "growth"
    this.earnings = { towns: new Map(), outside: null }; // townEarnings()
    this.stationLines = new Map();                 // station id -> lines
    this.industryLines = new Map();                // industry id -> the lines of its stations
    this.warehouseLines = new Map();               // warehouse id -> the lines of its stations
    this.stationIndustry = new Map();              // station id -> industry id (industry stations)
    this.owners = new Map();                       // viewer key -> estate (ownersIndex)
    this.ownerSort = { key: "assets", desc: true }; // owner table: the "name", "assets", "profit", "total" column

    root.querySelectorAll("[data-tab]").forEach((btn) =>
      btn.addEventListener("click", () => this.showTab(btn.dataset.tab)));
    // Tabs that don't fit: the mouse wheel scrolls the bar horizontally (touchpad and finger scroll on their own).
    const tabBar = root.querySelector(".tabs");
    tabBar.addEventListener("wheel", (e) => {
      if (tabBar.scrollWidth <= tabBar.clientWidth || Math.abs(e.deltaX) > Math.abs(e.deltaY)) return;
      e.preventDefault();
      tabBar.scrollLeft += e.deltaY;
    }, { passive: false });

    this.carriers.addEventListener("click", (e) => {
      const sort = e.target.closest("[data-sort]");
      if (sort) {
        this.townSort = sort.dataset.sort;
        this.renderCarriers();
        this.renderList();
        return;
      }
      if (e.target.closest("[data-near-clear]")) {
        this.industryOrigin = null;
        this.renderCarriers();
        this.renderList();
        return;
      }
      const mode = e.target.closest("[data-ind-mode]");
      const cargo = e.target.closest("[data-ind-cargo]");
      if (mode || cargo) {
        if (mode) this.industryFilter.mode = mode.dataset.indMode;
        // clicking the selected cargo again removes the filter
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
      // Owner table header: the same column reverses the order, a new one starts from the largest (name from A).
      const sort = e.target.closest("[data-owner-sort]");
      if (sort) {
        const key = sort.dataset.ownerSort;
        this.ownerSort = key === this.ownerSort.key ? { key, desc: !this.ownerSort.desc } : { key, desc: key !== "name" };
        this.renderList();
        return;
      }
      const row = e.target.closest("[data-kind]");
      if (row) this.onPick(row.dataset.kind, selId(row.dataset.kind, row.dataset.id));
    });

    // Hovering a list item (town, industry, station, line) highlights it on the map.
    // An owner isn't a single point on the map.
    this.list.addEventListener("mouseover", (e) => {
      const row = e.target.closest("[data-kind]");
      const kind = row && row.dataset.kind !== "owner" ? row.dataset.kind : null;
      onHoverItem?.(kind, kind ? Number(row.dataset.id) : null);
    });
    this.list.addEventListener("mouseleave", () => onHoverItem?.(null));

    // The same for lines in the details (of a town, station, industry): the pointed line on the map against the selected object.
    // In an owner's details the same for their towns, industries and stations (data-pick).
    // A cargo of a town or industry (data-cargo, data-dir): what it needs - its possible suppliers on the map,
    // what it produces - its possible consumers.
    this.detail.addEventListener("mouseover", (e) => {
      const chip = e.target.closest("[data-cargo]");
      const [cardKind, cardId] = splitPick(chip?.closest("[data-card]"), "card");
      const hasLinks = cardKind === "town" || cardKind === "industry";
      onHoverCargo?.(hasLinks ? cardKind : null, Number(cardId), chip?.dataset.cargo, chip?.dataset.dir);
      const line = e.target.closest("[data-line]");
      if (line) return onHoverItem?.("line", Number(line.dataset.line));
      const [kind, id] = splitPick(e.target.closest("[data-pick]"));
      const onMap = kind && kind !== "owner";
      onHoverItem?.(onMap ? kind : null, onMap ? Number(id) : null);
    });
    this.detail.addEventListener("mouseleave", () => {
      onHoverItem?.(null);
      onHoverCargo?.(null);
    });

    this.detail.addEventListener("click", (e) => {
      const [cardKind, cardRaw] = splitPick(e.target.closest("[data-card]"), "card");
      if (!cardKind) return;
      const cardId = selId(cardKind, cardRaw);
      const isActive = this.selected?.kind === cardKind && this.selected?.id === cardId;
      if (e.target.closest("[data-close]")) {
        // × of a pinned window unpins it; the selected object (like Esc) is also deselected
        if (this.isPinned(cardKind, cardId)) this.togglePin(cardKind, cardId);
        if (isActive) onClose?.();
        return;
      }
      if (e.target.closest("[data-pin]")) {
        this.togglePin(cardKind, cardId);
        return;
      }
      if (e.target.closest("[data-collapse]")) {
        const key = `${cardKind}:${cardId}`;
        if (!this.collapsed.delete(key)) this.collapsed.add(key);
        this.renderDetail();
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
      // A cargo of a town / industry: the Industry list of who can supply it (a need) or take it (a product), nearest first.
      const cargo = e.target.closest("[data-cargo]");
      if (cargo && (cardKind === "town" || cardKind === "industry")) {
        this.industryFilter = { mode: cargo.dataset.dir === "in" ? "out" : "in", cargo: cargo.dataset.cargo };
        this.industryOrigin = { kind: cardKind, id: cardId };
        this.search.value = this.filter = "";
        this.showTab("industries");
        this.onShowList?.();
        return;
      }
      // The owner in an object's details and the objects in an owner's details.
      const [kind, id] = splitPick(e.target.closest("[data-pick]"));
      if (kind) {
        this.onPick(kind, selId(kind, id));
        return;
      }
      const stop = e.target.closest("[data-stop]");
      if (!stop || cardKind !== "line") return;
      this.focusedStop = { line: cardId, index: Number(stop.dataset.stop) };
      this.onStop?.(cardId, this.focusedStop.index);
      this.renderDetail();
    });

    this.renderSearch();
  }

  // Switches the tab (also from outside: a click on the balance in the top bar opens Company).
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
    this.warehouseLines = linesByIndustry(dynamic, this.stationLines, warehouseOfStation(dynamic));
    this.earnings = townEarnings(dynamic);
    this.owners = ownersIndex(dynamic, this.stationIndustry);
    // pinned objects that no longer exist (a deleted line, a demolished industry) disappear
    const pinned = this.pinned.filter((p) => this.findItem(p));
    if (pinned.length !== this.pinned.length) {
      this.pinned = pinned;
      this.onPins?.([...pinned]);
    }
    this.root.querySelector('[data-count="towns"]').textContent = dynamic.towns?.length ?? 0;
    this.root.querySelector('[data-count="industries"]').textContent = dynamic.industries?.length ?? 0;
    this.root.querySelector('[data-count="stations"]').textContent = dynamic.stations?.length ?? 0;
    this.root.querySelector('[data-count="lines"]').textContent = dynamic.lines?.length ?? 0;
    this.root.querySelector('[data-count="owners"]').textContent = this.owners.size;
    this.render();
  }

  // The map reports the lines visible on screen (only when emitLinesView gave it a filter).
  setVisibleLines(ids) {
    this.visibleLines = ids;
    if (this.tab === "lines") this.renderList();
  }

  // On the Lines tab with the eye on, the map draws all lines on screen that pass
  // the panel filters (type + search), and sends back their list; otherwise - only the selected line.
  emitLinesView() {
    const active = this.tab === "lines" && this.viewportOnly;
    if (!active) this.visibleLines = null;
    this.onLinesView?.(active ? { match: (l) => this.matchLine(l) !== null } : null);
  }

  // A line passes the type and search filters -> names of the matching stations (empty when the line name or owner matched); otherwise null.
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
    if (changed && kind) this.windows?.front(`${kind}:${id}`); // a new or pointed pinned window to the front
  }

  isPinned(kind, id) {
    return this.pinned.some((p) => p.kind === kind && p.id === id);
  }

  // Pins / unpins an object's window. An unpinned window of an unselected object closes.
  togglePin(kind, id) {
    if (this.isPinned(kind, id)) {
      this.pinned = this.pinned.filter((p) => p.kind !== kind || p.id !== id);
      this.collapsed.delete(`${kind}:${id}`);
      this.windows?.unpin(`${kind}:${id}`);
    } else {
      this.pinned.push({ kind, id });
    }
    this.onPins?.([...this.pinned]);
    this.renderList();
    this.renderDetail();
    // a pinned window remembers its spot (the next selected object opens in the default one)
    if (this.isPinned(kind, id)) this.windows?.pin(`${kind}:${id}`);
  }

  // The object from the data for { kind, id }, or undefined.
  findItem(sel) {
    const d = this.dynamic;
    if (sel.kind === "owner") return this.owners.get(sel.id);
    const list = { town: d?.towns, industry: d?.industries, warehouse: d?.warehouses, station: d?.stations, line: d?.lines }[sel.kind] ?? [];
    return list.find((x) => x.id === sel.id);
  }

  // Everything that depends on the data or the language (called from main.js after a language change).
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

  // The station filter key: industry stations separately, regardless of the carrier.
  stationKey(s) {
    return this.stationIndustry.has(s.id) ? "INDUSTRY" : s.kind;
  }

  // ---------------------------------------------------------------- lists
  renderList() {
    const d = this.dynamic;
    if (!d) return;
    const match = (name) => !this.filter || String(name ?? "").toLowerCase().includes(this.filter);
    const matchItem = (x) => match(x.name) || (x.owner && match(x.owner));
    const isSel = (kind, id) => (this.selected?.kind === kind && this.selected?.id === id) || this.isPinned(kind, id);
    let html = "";

    // Company: a separate section instead of a list (the finance table, charts).
    const company = this.tab === "company";
    this.list.hidden = company;
    this.company.hidden = !company;
    if (company) {
      this.company.innerHTML = companyHtml(d.company);
      return;
    }

    if (this.tab === "towns") {
      // Sorting by earnings: towns without lines at the end, and below the list the lines outside towns.
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
      // Searching by the viewer's name and by the names of their objects ("Gdańsk" shows the owner of Gdańsk).
      const matchOwner = (o) => match(o.name)
        || [o.towns, o.industries, o.stations, o.lines].some((xs) => xs.some((x) => match(x.name)));
      const rows = [...this.owners.values()].filter(matchOwner).sort(ownerOrder(this.ownerSort))
        .map((o) => `
          <li class="row own-row" data-kind="owner" data-id="${esc(o.key)}" aria-current="${isSel("owner", o.key)}">
            <span class="row-main"><span class="row-name">${esc(o.name)}</span>${assetCounts(o)}</span>
            <span class="own-num">${scoreHtml(o)}</span>
            <span class="own-num">${moneyShort(o.profit12m)}</span>
            <span class="own-num">${moneyShort(ownerTotal(o))}</span>
          </li>`).join("");
      html = rows && ownerHeadHtml(this.ownerSort) + rows;
    } else if (this.tab === "industries") {
      const { mode, cargo } = this.industryFilter;
      const hasCargo = (i) => !cargo || industryCargos(i, mode).has(cargo);
      // From a cargo clicked in the details: nearest to that town / industry first, with the distance; otherwise by level.
      const from = this.industryOrigin && this.findItem(this.industryOrigin)?.pos;
      const dist = (i) => (from && i.pos ? Math.hypot(i.pos[0] - from[0], i.pos[1] - from[1]) : Infinity);
      const order = from ? (a, b) => dist(a) - dist(b) : (a, b) => (b.level ?? 0) - (a.level ?? 0);
      const value = (i) => (from ? t("list.km", { n: fmtDec(dist(i) / 1000) }) : t("list.level", { n: i.level ?? "–" }));
      html = [...d.industries].filter((i) => hasCargo(i) && matchItem(i)).sort(order)
        .map((i) => `
          <li class="row" data-kind="industry" data-id="${i.id}" aria-current="${isSel("industry", i.id)}">
            ${industryBadge(i)}
            <span class="row-name">${esc(i.name)}</span>
            ${effectBadges(i)}
            <span class="row-value">${esc(value(i))}</span>
          </li>`).join("");
    } else if (this.tab === "stations") {
      const kind = this.typeFilter.stations;
      const nLines = (s) => this.stationLines.get(s.id)?.length ?? 0;
      const industries = new Map(d.industries.map((i) => [i.id, i]));
      html = (d.stations ?? []).filter((s) => (!kind || this.stationKey(s) === kind) && matchItem(s))
        .sort((a, b) => nLines(b) - nLines(a) || String(a.name).localeCompare(String(b.name), locale()))
        .map((s) => {
          // An industry station: the industry icon, and a click opens the industry (its lines are there too).
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
      // Searching by line name or by the names of its stations; matching stations are shown below the name.
      // With the eye: only lines on the map screen (the selected one always stays).
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

  // Line type / station kind buttons: only the types present in the data, with the number of items.
  renderCarriers() {
    this.carriers.hidden = !(this.tab in this.typeFilter || ["towns", "industries"].includes(this.tab));
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

  // Industry filter: the mode (production / consumption) and one cargo with the number of industries that produce / consume it.
  industryFilterHtml() {
    const f = this.industryFilter;
    const counts = new Map();
    for (const i of this.dynamic?.industries ?? [])
      for (const c of industryCargos(i, f.mode)) counts.set(c, (counts.get(c) ?? 0) + 1);
    if (f.cargo && !counts.has(f.cargo)) counts.set(f.cargo, 0); // the chosen cargo stays after the mode changes
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
    const origin = this.industryOrigin && this.findItem(this.industryOrigin);
    const near = origin
      ? `<div class="filter-near">${esc(t("industry.near", { name: origin.name ?? "" }))}`
        + `<button type="button" data-near-clear title="${esc(t("industry.nearClear"))}" aria-label="${esc(t("industry.nearClear"))}">×</button></div>`
      : "";
    return `${near}<div class="filter-modes">${modeBtn("out")}${modeBtn("in")}</div>${all}${cargos.map(cargoBtn).join("")}`;
  }

  // ---------------------------------------------------------------- details
  // Separate windows: the pinned ones (in pinning order) and the selected object, unless it's pinned.
  // The window elements survive data refreshes (position, scroll, dragging),
  // only their content changes; the window of an object we no longer show disappears.
  renderDetail() {
    const sel = this.selected;
    const cards = [...this.pinned];
    if (sel && !this.isPinned(sel.kind, sel.id)) cards.push(sel);
    const old = new Map([...this.detail.children].map((el) => [el.dataset.card, el]));
    const els = [];
    for (const c of this.dynamic ? cards : []) {
      const item = this.findItem(c);
      if (!item) continue;
      const key = `${c.kind}:${c.id}`;
      let el = old.get(key);
      old.delete(key);
      if (!el) {
        el = document.createElement("article");
        el.dataset.card = key;
      }
      this.fillCard(el, c, item);
      els.push(el);
    }
    for (const el of old.values()) el.remove();
    // the order only matters on a phone (windows one below the other); we only move the ones out of place
    // (a moved element loses its scroll position)
    els.forEach((el, i) => {
      if (this.detail.children[i] !== el) this.detail.insertBefore(el, this.detail.children[i] ?? null);
    });
    this.detail.hidden = !els.length;
    this.windows?.layout();
  }

  // One window: buttons (collapse - pinned only, pin, close) and the object's details.
  fillCard(el, c, item) {
    const key = `${c.kind}:${c.id}`;
    const pinned = this.isPinned(c.kind, c.id);
    const active = this.selected?.kind === c.kind && this.selected?.id === c.id;
    const collapsed = pinned && this.collapsed.has(key);
    const btn = (attr, cls, label, text) =>
      `<button type="button" class="detail-tool ${cls}" ${attr} title="${esc(label)}" aria-label="${esc(label)}">${text}</button>`;
    const tools = `<div class="detail-tools">`
      + (pinned ? btn("data-collapse", "", t(collapsed ? "detail.expand" : "detail.collapse"), collapsed ? "▸" : "▾") : "")
      + btn(`data-pin aria-pressed="${pinned}"`, "detail-pin", t(pinned ? "detail.unpin" : "detail.pin"), "📌")
      + btn("data-close", "detail-close", t(active ? "detail.close" : "detail.unpin"), "×")
      + `</div>`;
    const stop = c.kind === "line" && this.focusedStop?.line === c.id ? this.focusedStop.index : null;
    const body = c.kind === "owner" ? ownerDetail(item, this.stationIndustry)
      : c.kind === "town" ? townDetail(item, this.earnings.towns.get(item.id))
      : c.kind === "line" ? lineDetail(item, stop, this.dynamic)
      : c.kind === "station" ? stationDetail(item, this.stationLines.get(item.id) ?? [])
      : c.kind === "warehouse" ? warehouseDetail(item, this.warehouseLines.get(item.id) ?? [])
      : industryDetail(item, this.industryLines.get(item.id) ?? []);
    el.className = `detail-card${collapsed ? " collapsed" : ""}${el.classList.contains("dragging") ? " dragging" : ""}`;
    el.dataset.pinned = String(pinned);
    el.setAttribute("aria-current", String(active));
    const html = tools + body;
    if (this.cardHtml.get(el) === html) return;
    const scroll = el.scrollTop;
    el.innerHTML = html;
    this.cardHtml.set(el, html);
    el.scrollTop = scroll;
  }
}

// A button for copying the name next to the header (the header is used for dragging the window, so its text can't be selected).
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
    // without the Clipboard API (e.g. http without localhost): the old way through a hidden text field
    const ta = document.createElement("textarea");
    ta.value = text;
    ta.style.cssText = "position:fixed;opacity:0";
    document.body.append(ta);
    ta.select();
    try { ok = document.execCommand("copy"); } catch { /* nothing */ }
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

// The tab bar scrolls horizontally (narrow panel, phone): the selected tab fully in view.
// No scrollIntoView, because it also moves the page when it's wider than the screen.
function scrollTabIntoView(btn) {
  const bar = btn.parentElement;
  const pad = parseFloat(getComputedStyle(bar).paddingLeft) || 0;
  const left = btn.offsetLeft - bar.offsetLeft - pad, right = btn.offsetLeft - bar.offsetLeft + btn.offsetWidth + pad;
  if (left < bar.scrollLeft) bar.scrollLeft = left;
  else if (right > bar.scrollLeft + bar.clientWidth) bar.scrollLeft = right - bar.clientWidth;
}

// A selection from the list or a link: an owner has a text key, the rest numeric ids.
const selId = (kind, id) => (kind === "owner" ? id : Number(id));

// data-pick="kind:id" (an owner key may contain ":") -> [kind, id] or [].
// attr: another attribute in this format (data-card of the detail windows).
function splitPick(el, attr = "pick") {
  const v = el?.dataset[attr] ?? "";
  const i = v.indexOf(":");
  return i > 0 ? [v.slice(0, i), v.slice(i + 1)] : [];
}

// The owner from the game name ("Name | Owner", splitAllOwners) and effects from viewers (attachEffects).
// The owner's name opens their estate.
function ownerHtml(x) {
  const owner = x.owner
    ? `${esc(t("owner"))}: <button type="button" class="owner-link" data-pick="owner:${esc(ownerKey(x.owner))}" title="${esc(t("owner.show"))}">${esc(x.owner)}</button>`
    : esc(t("owner.none"));
  return `<p class="detail-owner">${owner}</p>` + effectsHtml(x);
}

// Counters of the viewer's objects in the list (non-zero only).
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

// Vehicles on the viewer's lines: "Vehicles (N)", split by carrier and state (owners.js: fleet).
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

// The viewer's lines: statistics of all of them together (owners.js: lineStats), the list of lines and the cargo in vehicles now.
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

// The viewer's estate: a summary and clickable objects (data-pick, lines: data-line); the main (largest) town first.
// The viewer's estate in points (owners.js: score), with the formula explained in the tooltip.
const scoreHtml = (o) => `<span title="${esc(t("owners.scoreHint"))}">${esc(t("owners.points", { n: fmtInt(o.score) }))}</span>`;

// The viewer's total earnings: their lines over finished years (owners.js: lineStats.profitTotal).
const ownerTotal = (o) => o.lineStats?.profitTotal ?? null;

// Owner table order; a missing value (e.g. a viewer without lines) always last, ties - by estate and name.
function ownerOrder({ key, desc }) {
  const byName = (a, b) => a.name.localeCompare(b.name, locale());
  if (key === "name") return (a, b) => (desc ? -1 : 1) * byName(a, b);
  const value = { assets: (o) => o.score, profit: (o) => o.profit12m, total: ownerTotal }[key];
  return (a, b) => {
    const x = value(a), y = value(b);
    if (x == null || y == null) return (x == null) - (y == null) || b.score - a.score || byName(a, b);
    return (desc ? y - x : x - y) || b.score - a.score || byName(a, b);
  };
}

// Owner table header: clickable columns with a direction arrow, a "?" explaining the estate points
// (hovering or tapping - focus - shows the tooltip).
function ownerHeadHtml(sort) {
  const col = (key) => {
    const on = sort.key === key;
    return `<button type="button" class="own-col" data-owner-sort="${key}" title="${esc(t(`owners.sort.${key}`))}"
      aria-pressed="${on}">${esc(t(`owners.col.${key}`))}${on ? (sort.desc ? " ▼" : " ▲") : ""}</button>`;
  };
  const lines = t("owners.scoreHint").split("\n").map((l) => `<span>${esc(l)}</span>`).join("");
  const help = `<span class="help" tabindex="0" role="button" aria-label="${esc(t("owners.scoreHelp"))}">?`
    + `<span class="help-tip" role="tooltip"><b>${esc(t("owners.scoreHelp"))}</b>${lines}</span></span>`;
  return `<li class="own-head">${col("name")}<span class="own-col-help">${help}${col("assets")}</span>${col("profit")}${col("total")}</li>`;
}

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
  // An industry station (of another viewer or unowned) opens the industry, as in the station list.
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
      ${stat(t("owners.sort.assets"), scoreHtml(o))}
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

// The effect name with its strength: "Town boost +50%", "Strike −99%".
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

// Badges in the list (towns, industries, lines): ⚡ boost, ⏸ waits at stations, ⛔ stopped,
// ✊ strike, 🔍 tax raid, ⛽ premium fuel, 💧 watered-down fuel.
const EFFECT_ICON = {
  townBoost: "⚡", industryBoost: "⚡", lineStop: "⛔", lineHold: "⏸",
  industryStrike: "✊", industryTaxRaid: "🔍", lineFuelPremium: "⛽", lineFuelBad: "💧",
};
function effectBadges(x) {
  return (x.effects ?? []).map((e) =>
    `<span class="fx-badge fx-${esc(e.type)}" title="${esc(effectTip(e))}">${EFFECT_ICON[e.type] ?? "★"}</span>`).join("");
}

// In the details: each effect with its in-game end date, the buying viewer and a time bar.
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

// Lines are clickable (data-line) - they switch the selection to the line.
// waitingOf(line) -> passengers waiting for it here, or null: then a number next to the line and sorting by it.
function lineLinks(lines, waitingOf) {
  const w = new Map(waitingOf ? lines.map((l) => [l, waitingOf(l)]) : []);
  const rows = [...lines].sort((a, b) => (w.get(b) ?? -1) - (w.get(a) ?? -1) || (b.vehicleCount ?? 0) - (a.vehicleCount ?? 0)).map((l) => `
    <li><button type="button" data-line="${l.id}">
      <span class="swatch" style="background:${rgb(l.color)}"></span>${carrierIcon(l.carrier, l.carrier)}
      <span class="link-name">${esc(l.name)}</span>
      ${w.get(l) != null ? `<span class="link-value" title="${esc(t("station.waiting"))}">${cargoIcon("passengers")}${fmtInt(w.get(l))}</span>` : ""}
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
      ${s.waiting != null ? stat(t("station.waiting"), fmtInt(s.waiting)) : ""}
      ${s.waitingBad ? stat(t("station.waitingBad"), fmtInt(s.waitingBad)) : ""}
    </dl>
    ${lineLinks(lines, (l) => { const own = new Set(s.stations ?? []); return lineWaitingAt(l, (stop) => own.has(stop.entity)); })}`;
}

// Line profit attributed to a town (townEarnings): sums and the lines with the town's share in their stops.
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

// Growth with a sign, in color ("+12%" green, "-3%" red); no data -> "–".
const growthHtml = (g) => (g == null ? "–" : `<span class="${g < 0 ? "neg" : g > 0 ? "pos" : ""}">${esc(fmtSignedPercent(g))}</span>`);

// Needs satisfaction (town.supply from the mod, like the game's town statistics): cargo with a limit > 0
// or without a limit (-1), but delivered; coverage = sum of min(delivery, limit) / sum of limits.
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
      return `<li data-cargo="${esc(s.cargo)}" data-dir="in">${cargoLabel(s.cargo)}<span>${esc(v)}</span></li>`;
    }).join("")}</ul>`;
  } else {
    const chips = (town.cargoNeeds ?? []).flatMap((group) => Object.entries(group ?? {}))
      .map(([cargo, v]) => `<li data-cargo="${esc(cargo)}" data-dir="in">${cargoLabel(cargo)}<span>${fmtDec(v)}</span></li>`).join("");
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

// Industry: demand and production (per year, when the mod provides them - stocks[].consumedYear,
// producedYear), stock levels (stocks[].amount) and the counters from the last year (year).
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
    ${inputs.length ? `<h3>${esc(t("industry.needs"))}</h3><ul class="chips">${inputs.map((s) =>
      `<li data-cargo="${esc(s.cargo)}" data-dir="in">${cargoLabel(s.cargo)}${perYear(s.consumedYear)}</li>`).join("")}</ul>` : ""}
    ${outputs.size ? `<h3>${esc(t("industry.produces"))}</h3><ul class="chips">${[...outputs].map(([c, a]) =>
      `<li data-cargo="${esc(c)}" data-dir="out">${cargoLabel(c)}${hasYear ? perYear(yearOut[c] ?? 0) : `<span>${fmtInt(a)}</span>`}</li>`).join("")}</ul>` : ""}
    ${storage}
    ${year}
    ${lineLinks(lines)}`;
}

// Warehouse: fill level, cargo now, stores (allowed cargo, level / capacity) and traffic over the last year.
function warehouseDetail(w, lines) {
  const fillBar = (amount, capacity) => {
    const fill = capacity ? Math.min(1, (amount ?? 0) / capacity) : 0;
    return `<i class="stock-bar"><b style="width:${(fill * 100).toFixed(0)}%"></b></i>`;
  };
  const cargo = Object.entries(w.cargo ?? {}).sort((a, b) => b[1] - a[1]);
  const stocks = (w.stocks ?? []).map((s) => {
    const name = s.cargos?.length ? s.cargos.map(cargoLabel).join(", ") : esc(t("warehouse.anyCargo"));
    return `<li><span class="stock-name">${name}</span><em></em>`
      + `<span>${fmtInt(s.amount)} / ${fmtInt(s.capacity)}</span>${fillBar(s.amount, s.capacity)}</li>`;
  }).join("");
  const y = w.year ?? {};
  return `
    <h2>📦 ${esc(w.name ?? t("warehouse"))}${copyBtn(w.name)}</h2>
    <p class="detail-sub">${esc(t("warehouse"))}</p>
    ${ownerHtml(w)}
    <dl class="stats">
      ${stat(t("industry.stock"), `${fmtInt(w.amount ?? 0)} / ${fmtInt(w.capacity)}`)}
      ${stat(t("warehouse.fill"), w.capacity ? fmtPercent((w.amount ?? 0) / w.capacity) : "–")}
      ${y.unloaded != null ? stat(t("warehouse.unloaded"), esc(t("industry.perYear", { n: fmtInt(y.unloaded) }))) : ""}
      ${y.loaded != null ? stat(t("warehouse.loaded"), esc(t("industry.perYear", { n: fmtInt(y.loaded) }))) : ""}
    </dl>
    ${fillBar(w.amount, w.capacity)}
    ${cargo.length ? `<ul class="chips">${cargo.map(([c, n]) => `<li>${cargoLabel(c)}<span>${fmtInt(n)}</span></li>`).join("")}</ul>`
      : `<p class="detail-sub">${esc(t("warehouse.empty"))}</p>`}
    ${stocks ? `<h3>${esc(t("warehouse.stocks"))}</h3><ul class="stock">${stocks}</ul>` : ""}
    ${lineLinks(lines)}`;
}

/** Cargo the industry produces (mode "out") or consumes (mode "in"). */
function industryCargos(i, mode) {
  return mode === "out" ? new Set(industryOutputs(i).keys()) : industryInputs(i);
}

// The industry square in the kind's color with icons of the produced cargo (like the kind icon at stations).
function industryBadge(i) {
  const imgs = [...industryOutputs(i).keys()].map(cargoImg).filter(Boolean)
    .map((src) => `<img src="${src}" alt="" onerror="this.remove()">`).join("");
  return imgs ? `<span class="ind-badge ind-${industryKind(i)}" aria-hidden="true">${imgs}</span>`
    : `<span class="dot dot-${industryKind(i)}"></span>`;
}

// The "eye" state remembered in the browser; on by default.
const VIEWPORT_KEY = "tf3-lines-viewport";
function loadViewportOnly() {
  try { return localStorage.getItem(VIEWPORT_KEY) !== "0"; } catch { return true; }
}
function saveViewportOnly(on) {
  try { localStorage.setItem(VIEWPORT_KEY, on ? "1" : "0"); } catch { /* no localStorage */ }
}

// A line's carrier comes from its vehicles or the station kind; without either (e.g. only stops) it ends up in "NONE".
const CARRIER_ORDER = ["RAIL", "ROAD", "TRAM", "WATER", "AIR", "OTHER", "NONE"];
const carrierKey = (l) => l.carrier ?? "NONE";

// Unique station names of a line matching the phrase (a station can be on a line twice, e.g. in a loop).
function matchingStations(l, filter) {
  if (!filter) return [];
  const names = new Set((l.stops ?? []).map((s) => s.name).filter(Boolean));
  return [...names].filter((n) => n.toLowerCase().includes(filter));
}

// "via A, B" / "przez A, B" with the phrase highlighted; {names} is substituted after escaping the rest of the text.
function viaHtml(stations, filter) {
  const [before, after = ""] = t("list.via").split("{names}");
  return esc(before) + stations.map((s) => highlight(s, filter)).join(", ") + esc(after);
}

function highlight(text, filter) {
  const i = text.toLowerCase().indexOf(filter);
  if (i < 0) return esc(text);
  return esc(text.slice(0, i)) + `<mark>${esc(text.slice(i, i + filter.length))}</mark>` + esc(text.slice(i + filter.length));
}

// A stop with a position is clickable (data-stop = index on the line) - it moves the map to it.
function lineDetail(l, focusedStop, dynamic) {
  const fleet = lineFleet(l, dynamic);
  const stops = (l.stops ?? []).map((s, i) => {
    const n = lineWaitingAt(l, (stop) => stop === s);
    const name = esc(s.name ?? t("line.stop", { n: i + 1 }))
      + (n != null ? ` <span class="stop-waiting" title="${esc(t("station.waiting"))}">${cargoIcon("passengers")}${fmtInt(n)}</span>` : "");
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
      ${fleet ? fleetStats(fleet) : ""}
      ${l.stats ? lineStats(l.stats) : ""}
    </dl>
    ${l.stats?.cargo?.length ? `<h3>${esc(t("line.loadNow"))}</h3><ul class="chips">${l.stats.cargo.map((c) => `<li>${cargoLabel(c.cargo)}<span>${fmtInt(c.used)} / ${fmtInt(c.capacity)}</span></li>`).join("")}</ul>` : ""}
    ${fleet ? fleetTable(fleet) : ""}
    ${stops ? `<h3>${esc(t("line.stops"))}</h3><ol class="stops">${stops}</ol>` : ""}`;
}

/**
 * Details of a line's vehicles (lua_mod: collectors.vehicleDetails): average age and condition, the number past their lifespan
 * and groups by model (and consist length). null when the data has no details (turned off in the mod).
 */
function lineFleet(l, dynamic) {
  const vs = (dynamic?.vehicles ?? []).filter((v) => v.line === l.id && (v.age != null || v.maint != null));
  if (!vs.length) return null;
  const avg = (key) => {
    const xs = vs.map((v) => v[key]).filter((x) => x != null);
    return xs.length ? xs.reduce((a, b) => a + b, 0) / xs.length : null;
  };
  const groups = new Map();
  for (const v of vs) {
    const key = `${v.model}|${v.parts ?? 1}`;
    let g = groups.get(key);
    if (!g) groups.set(key, g = { model: dynamic.models?.[v.model], parts: v.parts, count: 0, ages: [], worst: null, overLife: false });
    g.count++;
    if (v.age != null) g.ages.push(v.age);
    if (v.maint != null && (g.worst == null || v.maint < g.worst)) g.worst = v.maint;
    if (v.wear >= 1) g.overLife = true;
  }
  return {
    avgAge: avg("age"),
    avgMaint: avg("maint"),
    overLife: vs.filter((v) => v.wear >= 1).length,
    groups: [...groups.values()].sort((a, b) => b.count - a.count),
  };
}

function fleetStats(f) {
  return `
      ${stat(t("line.avgAge"), esc(fmtAge(f.avgAge)))}
      ${stat(t("line.avgCondition"), `<span class="${f.avgMaint < 0.5 ? "neg" : ""}">${esc(fmtPercent(f.avgMaint))}</span>`)}
      ${f.overLife ? stat(t("line.overLife"), `<span class="neg">${fmtInt(f.overLife)}</span>`) : ""}`;
}

function fleetTable(f) {
  const rows = f.groups.map((g) => {
    const age = g.ages.length ? g.ages.reduce((a, b) => a + b, 0) / g.ages.length : null;
    return `<tr>
      <td>${vehicleImgHtml(g.model, "fleet-img")}${esc(g.model?.name ?? "?")}${g.parts ? ` <span class="muted">${esc(t("vehicle.parts", { n: g.parts }))}</span>` : ""}</td>
      <td class="num">${fmtInt(g.count)}</td>
      <td class="num${g.overLife ? " neg" : ""}">${esc(fmtAge(age))}</td>
      <td class="num${g.worst < 0.5 ? " neg" : ""}">${esc(fmtPercent(g.worst))}</td>
    </tr>`;
  }).join("");
  return `<h3>${esc(t("line.fleet"))}</h3>
    <table class="fleet-table">
      <thead><tr><th>${esc(t("fleet.model"))}</th><th class="num">${esc(t("line.vehicles"))}</th>`
      + `<th class="num" title="${esc(t("fleet.ageHint"))}">${esc(t("fleet.age"))}</th>`
      + `<th class="num" title="${esc(t("fleet.conditionHint"))}">${esc(t("fleet.condition"))}</th></tr></thead>
      <tbody>${rows}</tbody>
    </table>`;
}

// Statistics from the mod, refreshed once per game month (lua_mod: collectors.lineStats).
function lineStats(s) {
  return `
      ${stat(t("line.profit12m"), money(s.profit12m))}
      ${s.prevYear != null ? stat(t("line.profitYear", { year: s.prevYear }), money(s.profitPrevYear)) : ""}
      ${s.profitTotal != null ? stat(t(s.profitTotal < 0 ? "line.lossTotal" : "line.profitTotal", { year: s.prevYear }), money(s.profitTotal)) : ""}
      ${stat(t("line.transported12m"), fmtInt(s.transported12m))}
      ${stat(t("line.rate"), fmtInt(s.rate))}
      ${stat(t("line.load"), fmtPercent(s.load))}`;
}

// An amount in color: profit in green, loss in red; no data -> "–".
const money = (n) => (n == null ? "–" : `<span class="${n < 0 ? "neg" : "pos"}">${esc(fmtMoney(Math.round(n)))}</span>`);
// A short amount for narrow columns ("$ 87.5M"), the full one in the tooltip.
const moneyShort = (n) => (n == null ? "–"
  : `<span class="${n < 0 ? "neg" : "pos"}" title="${esc(fmtMoney(Math.round(n)))}">${esc(fmtMoneyShort(n))}</span>`);

const stat = (label, value) => `<div><dt>${esc(label)}</dt><dd>${value}</dd></div>`;
