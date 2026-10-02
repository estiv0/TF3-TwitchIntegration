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
};

/** "::/cargos/fish/fish.cargo" -> "Ryby" */
export function cargoName(res) {
  if (!res) return "–";
  const key = String(res).split("/").pop().replace(/\.cargo$/, "");
  return CARGO_PL[key] ?? key.replace(/_/g, " ");
}

export const CARRIER_PL = {
  RAIL: "Kolej", ROAD: "Drogowe", TRAM: "Tramwaj", WATER: "Wodne", AIR: "Lotnicze", OTHER: "Inne",
};

export const CARRIER_ICON = { RAIL: "🚆", ROAD: "🚌", TRAM: "🚋", WATER: "⛴", AIR: "✈", OTHER: "•" };

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
