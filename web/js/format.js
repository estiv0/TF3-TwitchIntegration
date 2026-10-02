// Formatowanie i słowniki do wyświetlania danych gry.

const numberFmt = new Intl.NumberFormat("pl-PL");
const decimalFmt = new Intl.NumberFormat("pl-PL", { maximumFractionDigits: 1 });

export const fmtInt = (n) => (n == null ? "–" : numberFmt.format(Math.round(n)));
export const fmtDec = (n) => (n == null ? "–" : decimalFmt.format(n));
export const fmtMoney = (n) => (n == null ? "∞" : `$ ${numberFmt.format(n)}`);
export const fmtPercent = (n) => (n == null ? "–" : `${Math.round(n * 100)}%`);

const MONTHS = ["sty", "lut", "mar", "kwi", "maj", "cze", "lip", "sie", "wrz", "paź", "lis", "gru"];
export const fmtDate = (d) => (d ? `${d.day} ${MONTHS[d.month - 1] ?? d.month} ${d.year}` : "–");

const CARGO_PL = {
  beverages: "Napoje", books: "Książki", cement: "Cement", chemicals: "Chemikalia",
  clothes: "Ubrania", coal: "Węgiel", crude_oil: "Ropa naftowa", dyes: "Barwniki",
  fabric: "Tkaniny", fertilizer: "Nawóz", fish: "Ryby", fuel: "Paliwo",
  furniture: "Meble", glass: "Szkło", grain: "Zboże", iron_ore: "Ruda żelaza",
  logs: "Drewno", machines: "Maszyny", meat: "Mięso", paper: "Papier",
  planks: "Deski", plastic: "Tworzywa", sand: "Piasek", sawdust: "Trociny",
  sheet_metal: "Blacha", steel: "Stal", stone: "Kamień", tinned_food: "Konserwy",
  tools: "Narzędzia", vehicles: "Pojazdy", wool: "Wełna", passengers: "Pasażerowie",
  bricks: "Cegły", clay: "Glina", rubber: "Kauczuk", tires: "Opony", vegetables: "Warzywa",
};

const cargoKey = (res) => String(res ?? "").split("/").pop().replace(/\.cargo$/, "");

/** "::/cargos/fish/fish.cargo" -> "Ryby" */
export function cargoName(res) {
  if (!res) return "–";
  const key = cargoKey(res);
  return CARGO_PL[key] ?? key.replace(/_/g, " ");
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

/** Ikonka + nazwa ładunku (do chipów i list). */
export const cargoLabel = (res) => `${cargoIcon(res)}${escapeHtml(cargoName(res))}`;

export const CARRIER_PL = {
  RAIL: "Kolej", ROAD: "Drogowe", TRAM: "Tramwaj", WATER: "Wodne", AIR: "Lotnicze", OTHER: "Inne",
};

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

// Rodzaj stacji (stations[].kind) -> nazwa.
export const STATION_PL = {
  RAIL: "Dworzec kolejowy", ROAD: "Dworzec drogowy", TRAM: "Stacja tramwajowa", WATER: "Port", AIR: "Lotnisko",
  INDUSTRY: "Przemysł", OTHER: "Inne",
};
export const STATION_ORDER = ["RAIL", "ROAD", "TRAM", "WATER", "AIR", "INDUSTRY", "OTHER"];

/** Pasażerska / towarowa / mieszana */
export const stationUse = (s) =>
  s.passengers && s.cargo ? "pasażerska i towarowa" : s.cargo ? "towarowa" : s.passengers ? "pasażerska" : "";

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
export const LAND_USE_PL = ["Mieszkalne", "Usługowe", "Przemysłowe"];

export const townSize = (t) => (t.capacities ?? []).reduce((a, b) => a + b, 0);

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
