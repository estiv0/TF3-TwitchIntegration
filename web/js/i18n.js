// Tłumaczenia strony (PL / EN).
//   * t("klucz", { n: 3 })  - napis interfejsu z podstawieniem {n}
//   * dict().cargo / .carrier / .station / ... - słowniki nazw z gry
//   * statyczny HTML: atrybuty data-i18n (tekst), data-i18n-title, data-i18n-placeholder, data-i18n-aria
// Język: ?lang=pl|en w adresie > zapamiętany wybór > język przeglądarki (polski -> pl, reszta -> en).

const DICT = {
  pl: {
    locale: "pl-PL",
    ui: {
      "brand.sub": "Transport Fever 3 · na żywo",
      "kpi.date": "Data",
      "kpi.balance": "Saldo",
      "kpi.fleet": "Flota",
      "kpi.edges": "Odcinki sieci",
      "status.waiting": "Czekam na dane…",
      "status.live": "Na żywo",
      "status.stale": "Wstrzymane",
      "status.error": "Brak danych",
      "status.updated": "Ostatnia aktualizacja: {time}",
      "status.fetchFailed": "Nie udało się pobrać {url} – czy Tf3Bridge działa?",
      "lang.label": "Język",
      "map.label": "Mapa",
      "map.fit": "Pokaż całą mapę",
      "map.terrain": "Teren (rzeźba)",
      "map.stations": "Stacje (dworce, porty, lotniska)",
      "map.pin": "Pinezka z linkiem – kliknij miejsce na mapie (albo prawy przycisk myszy)",
      "legend.label": "Legenda",
      "legend.track": "Tory",
      "legend.street": "Drogi",
      "legend.town": "Miasto",
      "legend.raw": "Surowce",
      "legend.processing": "Przetwórstwo",
      "legend.rail": "Dworzec",
      "legend.road": "Dworzec drogowy",
      "legend.water": "Port",
      "legend.air": "Lotnisko",
      "pin.title": "Pinezka",
      "pin.note": "Opis pinezki",
      "pin.placeholder": "np. Tu powstanie nowy port",
      "pin.create": "Utwórz link",
      "pin.cancel": "Anuluj",
      "pin.link": "Link do pinezki",
      "pin.copy": "Kopiuj",
      "pin.copied": "Skopiowano ✓",
      "tab.towns": "Miasta",
      "tab.industries": "Przemysł",
      "tab.stations": "Stacje",
      "tab.lines": "Linie",
      "search": "Szukaj…",
      "search.lines": "Szukaj linii lub stacji…",
      "filter.label": "Filtr typu",
      "filter.all": "Wszystkie",
      "filter.none": "Bez pojazdów",
      "list.empty": "Brak wyników",
      "list.emptyView": "Brak linii w widoku mapy",
      "lines.viewport": "Tylko linie z widoku mapy",
      "lines.viewportOn": "Linie z widoku mapy: na mapie i na liście (kliknij, żeby wyłączyć)",
      "lines.viewportOff": "Pokaż na mapie i na liście linie z widoku mapy",
      "list.level": "poz. {n}",
      "list.lines": "{n} lin.",
      "list.lineStats": "{v} poj. · {s} przyst.",
      "list.via": "przez {names}",
      "town": "Miasto",
      "town.capacity": "Miasto · pojemność {n}",
      "town.growing": "rośnie",
      "town.stopped": "rozwój wstrzymany",
      "town.zone": "Strefa {n}",
      "town.noise": "Hałas",
      "town.pollution": "Zanieczyszczenie",
      "town.demand": "Zapotrzebowanie",
      "industry.raw": "Surowce",
      "industry.processing": "Przetwórstwo",
      "industry.level": "Poziom",
      "industry.levelTip": "Poziom {level} · produktywność {p}",
      "industry.productivity": "Produktywność",
      "industry.upgrade": "Postęp rozbudowy",
      "industry.needs": "Potrzebuje",
      "industry.produces": "Produkuje",
      "needs": "Potrzebuje: {list}",
      "station": "Stacja",
      "lines": "Linie",
      "lines.n": "Linie: {n}",
      "station.vehicles": "Pojazdy linii",
      "line": "Linia",
      "line.vehicles": "Pojazdy",
      "line.stops": "Przystanki",
      "line.stop": "Przystanek {n}",
      "use.both": "pasażerska i towarowa",
      "use.cargo": "towarowa",
      "use.passengers": "pasażerska",
    },
    months: ["sty", "lut", "mar", "kwi", "maj", "cze", "lip", "sie", "wrz", "paź", "lis", "gru"],
    landUse: ["Mieszkalne", "Usługowe", "Przemysłowe"],
    carrier: { RAIL: "Kolej", ROAD: "Drogowe", TRAM: "Tramwaj", WATER: "Wodne", AIR: "Lotnicze", OTHER: "Inne" },
    station: {
      RAIL: "Dworzec kolejowy", ROAD: "Dworzec drogowy", TRAM: "Stacja tramwajowa", WATER: "Port", AIR: "Lotnisko",
      INDUSTRY: "Przemysł", OTHER: "Inne",
    },
    cargo: {
      beverages: "Napoje", books: "Książki", cement: "Cement", chemicals: "Chemikalia",
      clothes: "Ubrania", coal: "Węgiel", crude_oil: "Ropa naftowa", dyes: "Barwniki",
      fabric: "Tkaniny", fertilizer: "Nawóz", fish: "Ryby", fuel: "Paliwo",
      furniture: "Meble", glass: "Szkło", grain: "Zboże", iron_ore: "Ruda żelaza",
      logs: "Drewno", machines: "Maszyny", meat: "Mięso", paper: "Papier",
      planks: "Deski", plastic: "Tworzywa", sand: "Piasek", sawdust: "Trociny",
      sheet_metal: "Blacha", steel: "Stal", stone: "Kamień", tinned_food: "Konserwy",
      tools: "Narzędzia", vehicles: "Pojazdy", wool: "Wełna", passengers: "Pasażerowie",
      bricks: "Cegły", clay: "Glina", rubber: "Kauczuk", tires: "Opony", vegetables: "Warzywa",
    },
  },

  en: {
    locale: "en-GB",
    ui: {
      "brand.sub": "Transport Fever 3 · live",
      "kpi.date": "Date",
      "kpi.balance": "Balance",
      "kpi.fleet": "Fleet",
      "kpi.edges": "Network segments",
      "status.waiting": "Waiting for data…",
      "status.live": "Live",
      "status.stale": "Paused",
      "status.error": "No data",
      "status.updated": "Last update: {time}",
      "status.fetchFailed": "Could not load {url} – is Tf3Bridge running?",
      "lang.label": "Language",
      "map.label": "Map",
      "map.fit": "Show the whole map",
      "map.terrain": "Terrain (relief)",
      "map.stations": "Stations (train stations, harbors, airports)",
      "map.pin": "Pin with a link – click a spot on the map (or right-click)",
      "legend.label": "Legend",
      "legend.track": "Tracks",
      "legend.street": "Roads",
      "legend.town": "Town",
      "legend.raw": "Raw materials",
      "legend.processing": "Processing",
      "legend.rail": "Train station",
      "legend.road": "Road station",
      "legend.water": "Harbor",
      "legend.air": "Airport",
      "pin.title": "Pin",
      "pin.note": "Pin description",
      "pin.placeholder": "e.g. A new harbor goes here",
      "pin.create": "Create link",
      "pin.cancel": "Cancel",
      "pin.link": "Link to the pin",
      "pin.copy": "Copy",
      "pin.copied": "Copied ✓",
      "tab.towns": "Towns",
      "tab.industries": "Industry",
      "tab.stations": "Stations",
      "tab.lines": "Lines",
      "search": "Search…",
      "search.lines": "Search lines or stations…",
      "filter.label": "Type filter",
      "filter.all": "All",
      "filter.none": "No vehicles",
      "list.empty": "No results",
      "list.emptyView": "No lines in the map view",
      "lines.viewport": "Only lines in the map view",
      "lines.viewportOn": "Lines in the map view: on the map and in the list (click to turn off)",
      "lines.viewportOff": "Show lines in the map view on the map and in the list",
      "list.level": "lvl {n}",
      "list.lines": "{n} lines",
      "list.lineStats": "{v} veh. · {s} stops",
      "list.via": "via {names}",
      "town": "Town",
      "town.capacity": "Town · capacity {n}",
      "town.growing": "growing",
      "town.stopped": "growth paused",
      "town.zone": "Zone {n}",
      "town.noise": "Noise",
      "town.pollution": "Pollution",
      "town.demand": "Demand",
      "industry.raw": "Raw materials",
      "industry.processing": "Processing",
      "industry.level": "Level",
      "industry.levelTip": "Level {level} · productivity {p}",
      "industry.productivity": "Productivity",
      "industry.upgrade": "Upgrade progress",
      "industry.needs": "Needs",
      "industry.produces": "Produces",
      "needs": "Needs: {list}",
      "station": "Station",
      "lines": "Lines",
      "lines.n": "Lines: {n}",
      "station.vehicles": "Line vehicles",
      "line": "Line",
      "line.vehicles": "Vehicles",
      "line.stops": "Stops",
      "line.stop": "Stop {n}",
      "use.both": "passengers and cargo",
      "use.cargo": "cargo",
      "use.passengers": "passengers",
    },
    months: ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"],
    landUse: ["Residential", "Commercial", "Industrial"],
    carrier: { RAIL: "Rail", ROAD: "Road", TRAM: "Tram", WATER: "Water", AIR: "Air", OTHER: "Other" },
    station: {
      RAIL: "Train station", ROAD: "Road station", TRAM: "Tram station", WATER: "Harbor", AIR: "Airport",
      INDUSTRY: "Industry", OTHER: "Other",
    },
    cargo: {
      beverages: "Beverages", books: "Books", cement: "Cement", chemicals: "Chemicals",
      clothes: "Clothes", coal: "Coal", crude_oil: "Crude oil", dyes: "Dyes",
      fabric: "Fabric", fertilizer: "Fertilizer", fish: "Fish", fuel: "Fuel",
      furniture: "Furniture", glass: "Glass", grain: "Grain", iron_ore: "Iron ore",
      logs: "Logs", machines: "Machines", meat: "Meat", paper: "Paper",
      planks: "Planks", plastic: "Plastic", sand: "Sand", sawdust: "Sawdust",
      sheet_metal: "Sheet metal", steel: "Steel", stone: "Stone", tinned_food: "Tinned food",
      tools: "Tools", vehicles: "Vehicles", wool: "Wool", passengers: "Passengers",
      bricks: "Bricks", clay: "Clay", rubber: "Rubber", tires: "Tires", vegetables: "Vegetables",
    },
  },
};

export const LANGS = Object.keys(DICT);
const STORAGE_KEY = "tf3-lang";
const listeners = new Set();

function detect() {
  const fromUrl = new URLSearchParams(location.search).get("lang");
  if (DICT[fromUrl]) return fromUrl;
  try {
    const saved = localStorage.getItem(STORAGE_KEY);
    if (DICT[saved]) return saved;
  } catch { /* brak dostępu do localStorage (tryb prywatny itp.) */ }
  return (navigator.language ?? "").toLowerCase().startsWith("pl") ? "pl" : "en";
}

let current = detect();

export const lang = () => current;
export const dict = () => DICT[current];
export const locale = () => DICT[current].locale;

/** Napis interfejsu; {nazwa} zastępowane wartościami z vars. Brak klucza -> polski -> sam klucz. */
export function t(key, vars) {
  const s = DICT[current].ui[key] ?? DICT.pl.ui[key] ?? key;
  return vars ? s.replace(/\{(\w+)\}/g, (m, k) => (k in vars ? String(vars[k]) : m)) : s;
}

/** Przepisuje statyczne napisy oznaczone data-i18n* w podanym fragmencie dokumentu. */
export function applyStatic(root = document) {
  document.documentElement.lang = current;
  for (const el of root.querySelectorAll("[data-i18n]")) el.textContent = t(el.dataset.i18n);
  for (const el of root.querySelectorAll("[data-i18n-title]")) el.title = t(el.dataset.i18nTitle);
  for (const el of root.querySelectorAll("[data-i18n-placeholder]")) el.placeholder = t(el.dataset.i18nPlaceholder);
  for (const el of root.querySelectorAll("[data-i18n-aria]")) el.setAttribute("aria-label", t(el.dataset.i18nAria));
}

export function setLang(next) {
  if (!DICT[next] || next === current) return;
  current = next;
  try { localStorage.setItem(STORAGE_KEY, next); } catch { /* jw. */ }
  applyStatic();
  for (const fn of listeners) fn(next);
}

/** fn(lang) po każdej zmianie języka - do przerysowania części budowanych w JS. */
export function onLangChange(fn) {
  listeners.add(fn);
}
