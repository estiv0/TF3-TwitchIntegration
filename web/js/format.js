// Formatowanie i nazwy do wyświetlania danych gry (w bieżącym języku, patrz i18n.js).

import { dict, locale, t } from "./i18n.js";

// Formatery liczb per język (tworzenie Intl.NumberFormat jest kosztowne).
const formatters = new Map();
function nf(kind) {
  const key = `${locale()}|${kind}`;
  if (!formatters.has(key)) {
    const opts = kind === "dec" ? { maximumFractionDigits: 1 }
      : kind === "compact" ? { notation: "compact", maximumFractionDigits: 1 } : {};
    formatters.set(key, new Intl.NumberFormat(locale(), opts));
  }
  return formatters.get(key);
}

export const fmtInt = (n) => (n == null ? "–" : nf("int").format(Math.round(n)));
export const fmtDec = (n) => (n == null ? "–" : nf("dec").format(n));
export const fmtMoney = (n) => (n == null ? "∞" : `$ ${nf("int").format(n)}`);
/** Kwota skrócona ("$ 1,5 mln" / "$ 1.5M") - do tabel i osi wykresów. */
export const fmtMoneyShort = (n) => (n == null ? "–" : `$ ${nf("compact").format(n)}`);
export const fmtShort = (n) => (n == null ? "–" : nf("compact").format(n));
export const fmtPercent = (n) => (n == null ? "–" : `${Math.round(n * 100)}%`);
export const fmtSignedPercent = (n) => (n == null ? "–" : `${n > 0 ? "+" : ""}${Math.round(n * 100)}%`);
/** Wiek w latach: "1 rok", "3 lata", "2,5 roku" / "1 year", "2.5 years". */
export function fmtAge(years) {
  if (years == null) return "–";
  const n = Math.round(years * 10) / 10;
  const forms = dict().years;
  return `${fmtDec(n)} ${forms[new Intl.PluralRules(locale()).select(n)] ?? forms.other}`;
}
export const fmtDate = (d) => (d ? `${d.day} ${dict().months[d.month - 1] ?? d.month} ${d.year}` : "–");

const cargoKey = (res) => String(res ?? "").split("/").pop().replace(/\.cargo$/, "");

/** "::/cargos/fish/fish.cargo" -> "Ryby" / "Fish" */
export function cargoName(res) {
  if (!res) return "–";
  const key = cargoKey(res);
  return dict().cargo[key] ?? key.replace(/_/g, " ");
}

/** Kolorowa ikonka ładunku z gry (web/img/resources); brakujący plik po prostu znika. */
export function cargoIcon(res) {
  const src = cargoImg(res);
  return src ? `<img class="cargo-ico" src="${src}" alt="" onerror="this.remove()">` : "";
}

/** Adres ikonki ładunku albo null (nazwa spoza [a-z0-9_]). */
export function cargoImg(res) {
  const key = cargoKey(res);
  return /^[a-z0-9_]+$/.test(key) ? `img/resources/${key}.png` : null;
}

/**
 * Obrazek modelu pojazdu z boku (web/img/vehicles, z plików gry przez tools/extract_game_icons.py).
 * model = dynamic.models[id] ({ name, icon }); brak ikonki albo pliku -> nic.
 */
export function vehicleImgHtml(model, cls = "veh-img") {
  const icon = model?.icon;
  return icon && /^[A-Za-z0-9_.-]+$/.test(icon)
    ? `<img class="${cls}" src="img/vehicles/${icon}.png" alt="" loading="lazy" onerror="this.remove()">` : "";
}

/** m/s -> "87 km/h" / "87 km/h". */
export const fmtSpeed = (ms) => (ms == null ? "–" : `${nf("int").format(Math.round(ms * 3.6))} km/h`);

/** Ikonka + nazwa ładunku (do chipów i list). */
export const cargoLabel = (res) => `${cargoIcon(res)}${escapeHtml(cargoName(res))}`;

/** Nazwa nośnika (RAIL -> "Kolej" / "Rail") albo undefined dla nieznanego. */
export const carrierName = (carrier) => dict().carrier[carrier];

// Ikonki pojazdów z gry (web/img/stations): białe maski, barwione kolorem (CSS mask / canvas).
const CARRIER_IMG = {
  RAIL: "vehicle_train_front_32", ROAD: "vehicle_bus_front_32", TRAM: "vehicle_tram_front_32",
  WATER: "vehicle_ship_front_32", AIR: "vehicle_airplane_front_32", TRUCK: "vehicle_truck_front_32",
};
const imgUrl = (key) => (CARRIER_IMG[key] ? `img/stations/${CARRIER_IMG[key]}.png` : null);

/** Obrazek nośnika albo null. */
export const carrierImg = (carrier) => imgUrl(carrier);

/** Obrazek stacji: dworzec drogowy tylko dla ładunków dostaje ciężarówkę zamiast autobusu. */
export const stationImg = (s) => imgUrl(s.kind === "ROAD" && s.cargo && !s.passengers ? "TRUCK" : s.kind);

/**
 * Ikonka jako <span> z maską CSS, barwiona przez `color` (domyślnie kolor tekstu).
 * `tint` = rodzaj z kolorem z motywu (np. "RAIL" -> var(--station-rail)).
 */
export function iconHtml(src, tint) {
  if (!src) return "";
  const color = tint ? ` ico-${String(tint).toLowerCase()}` : "";
  return `<span class="ico${color}" style="-webkit-mask-image:url('${src}');mask-image:url('${src}')" aria-hidden="true"></span>`;
}

export const carrierIcon = (carrier, tint) => iconHtml(carrierImg(carrier), tint);

/** Rodzaj stacji (stations[].kind, też "INDUSTRY") -> nazwa albo undefined. */
export const stationName = (kind) => dict().station[kind];
export const STATION_ORDER = ["RAIL", "ROAD", "TRAM", "WATER", "AIR", "INDUSTRY", "OTHER"];

/** Pasażerska / towarowa / mieszana */
export const stationUse = (s) =>
  s.passengers && s.cargo ? t("use.both") : s.cargo ? t("use.cargo") : s.passengers ? t("use.passengers") : "";

/**
 * Pasażerowie czekający na linię `l` na jej przystankach spełniających `atStop(stop)` (stops[].lineWaiting z moda).
 * null, gdy mod tego nie podaje albo linia nie wozi pasażerów (zero na linii towarowej nic nie mówi).
 */
export function lineWaitingAt(l, atStop) {
  let n = null;
  for (const s of l.stops ?? []) if (s.lineWaiting != null && atStop(s)) n = (n ?? 0) + s.lineWaiting;
  const passengers = (l.stats?.cargo ?? []).some((c) => String(c.cargo).includes("passengers"));
  return n && n > 0 ? n : n === 0 && passengers ? 0 : null;
}

/** id stacji (konstrukcji) -> linie, które się na niej zatrzymują (stop.entity ∈ station.stations). */
export function linesByStation(dynamic) {
  const constrOf = new Map();
  for (const s of dynamic.stations ?? []) for (const e of s.stations ?? []) constrOf.set(e, s.id);
  const out = new Map();
  for (const l of dynamic.lines ?? []) {
    const ids = new Set((l.stops ?? []).map((st) => constrOf.get(st.entity)).filter((id) => id != null));
    for (const id of ids) {
      if (!out.has(id)) out.set(id, []);
      out.get(id).push(l);
    }
  }
  return out;
}

/**
 * id stacji -> id zakładu, do którego stacja należy. Z moda (stations[].industry), a dla starszych
 * eksportów po tej samej konstrukcji: ten sam plik .con i ta sama pozycja (środek konstrukcji).
 */
export function industryOfStation(dynamic) {
  const ids = new Set((dynamic.industries ?? []).map((i) => i.id));
  const byPlace = new Map((dynamic.industries ?? []).filter((i) => i.pos)
    .map((i) => [placeKey(i.file, i.pos), i.id]));
  const out = new Map();
  for (const s of dynamic.stations ?? []) {
    const id = ids.has(s.industry) ? s.industry : byPlace.get(placeKey(s.file, s.pos));
    if (id != null) out.set(s.id, id);
  }
  return out;
}

/** id stacji -> id magazynu (dynamic.warehouses), do którego stacja należy (stations[].warehouse z moda). */
export function warehouseOfStation(dynamic) {
  const ids = new Set((dynamic.warehouses ?? []).map((w) => w.id));
  return new Map((dynamic.stations ?? []).filter((s) => ids.has(s.warehouse)).map((s) => [s.id, s.warehouse]));
}

const placeKey = (file, pos) =>
  pos ? `${String(file ?? "").replace(/^::\//, "")}@${Math.round(pos[0])},${Math.round(pos[1])}` : "";

/** id zakładu -> linie obsługujące jego stacje, bez powtórzeń. */
export function linesByIndustry(dynamic, stationLines = linesByStation(dynamic), stationIndustry = industryOfStation(dynamic)) {
  const out = new Map();
  for (const [stationId, industryId] of stationIndustry) {
    const set = out.get(industryId) ?? new Set();
    for (const l of stationLines.get(stationId) ?? []) set.add(l);
    out.set(industryId, set);
  }
  return new Map([...out].map(([id, set]) => [id, [...set]]));
}

// TF: initialLandUseCapacities = [mieszkalne, usługowe, przemysłowe]
export const landUseName = (i) => dict().landUse[i];

/**
 * Obiekty mają w grze nazwy "Nazwa | Właściciel" (bez "| ..." - bez właściciela). Rozdziela je w miejscu:
 * name = sama nazwa (mapa, listy), owner = właściciel albo null.
 */
function splitOwners(items) {
  for (const x of items ?? []) {
    x.owner = null;
    if (typeof x.name !== "string") continue;
    const i = x.name.lastIndexOf("|");
    if (i < 0) continue;
    x.owner = x.name.slice(i + 1).trim() || null;
    x.name = x.name.slice(0, i).trim() || x.name;
  }
}

/**
 * Miasta, zakłady, magazyny, stacje i linie (z przystankami) z dynamic.json.
 * Gra nazywa stacje od miasta, także kupionego: "Dworzec Szczecin | estiv0 Zachód" to nie stacja widza
 * "estiv0 Zachód", tylko "Dworzec Szczecin Zachód". Dlatego w nazwach stacji i przystanków pełne nazwy
 * miast i zakładów z właścicielem zamieniamy najpierw na same nazwy.
 */
export function splitAllOwners(dynamic) {
  const owned = [...(dynamic.towns ?? []), ...(dynamic.industries ?? [])]
    .map((x) => x.name).filter((n) => typeof n === "string" && n.includes("|"))
    .sort((a, b) => b.length - a.length) // dłuższa nazwa pierwsza ("Nowa Góra | x" przed "Góra | x")
    .map((full) => ({
      re: new RegExp(`${escapeRegex(full)}(?![\\p{L}\\p{N}_])`, "gu"), // "Bar | ab" nie trafia w "Bar | abc"
      plain: full.slice(0, full.lastIndexOf("|")).trim(),
    }));
  const unOwn = (items) => {
    if (!owned.length) return;
    for (const x of items ?? []) {
      if (typeof x.name !== "string" || !x.name.includes("|")) continue;
      for (const { re, plain } of owned) x.name = x.name.replace(re, plain);
    }
  };
  unOwn(dynamic.stations);
  unOwn(dynamic.warehouses);
  for (const l of dynamic.lines ?? []) unOwn(l.stops);

  for (const key of ["towns", "industries", "warehouses", "stations", "lines"]) splitOwners(dynamic[key]);
  for (const l of dynamic.lines ?? []) splitOwners(l.stops);
}

const escapeRegex = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

// Efekty z nagród za punkty kanału (dynamic.effects z moda): rodzaj efektu -> lista obiektów.
const EFFECT_TARGET = {
  townBoost: "towns", industryBoost: "industries", industryStrike: "industries", industryTaxRaid: "industries",
  lineStop: "lines", lineHold: "lines", lineFuelPremium: "lines", lineFuelBad: "lines",
};

/**
 * Dokleja aktywne efekty do miast, zakładów i linii (x.effects). Postęp efektu (0..1, ile już minęło)
 * z czasu gry eksportu: effect.progress.
 */
export function attachEffects(dynamic) {
  const effects = Array.isArray(dynamic.effects) ? dynamic.effects : [];
  const byId = {};
  for (const key of new Set(Object.values(EFFECT_TARGET))) byId[key] = new Map((dynamic[key] ?? []).map((x) => [x.id, x]));
  for (const key of Object.keys(byId)) for (const x of dynamic[key] ?? []) x.effects = [];
  for (const e of effects) {
    const x = byId[EFFECT_TARGET[e.type]]?.get(e.entity);
    if (!x) continue;
    const span = (e.untilTime ?? 0) - (e.startTime ?? 0);
    e.progress = span > 0 && dynamic.gameTime != null ? Math.min(1, Math.max(0, (dynamic.gameTime - e.startTime) / span)) : null;
    x.effects.push(e);
  }
}

export const townSize = (t) => (t.capacities ?? []).reduce((a, b) => a + b, 0);

/** Wzrost miasta od startu gry (obecna pojemność / initialCapacities - 1) albo null. */
export function townGrowth(t) {
  const start = (t.initialCapacities ?? []).reduce((a, b) => a + b, 0);
  return start > 0 ? townSize(t) / start - 1 : null;
}

/** Kolor linii z gry (0..1) -> CSS */
export function rgb(color, fallback = "#8a94a6") {
  if (!Array.isArray(color) || color.length < 3) return fallback;
  const c = color.map((v) => Math.round(Math.max(0, Math.min(1, v)) * 255));
  return `rgb(${c[0]}, ${c[1]}, ${c[2]})`;
}

export function escapeHtml(s) {
  return String(s ?? "").replace(/[&<>"']/g, (ch) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[ch]);
}
