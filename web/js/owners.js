// Majątek widzów: miasta, zakłady, stacje i linie z właścicielem w nazwie ("Nazwa | Widz", splitAllOwners).
// Ten sam widz bywa zapisany różną wielkością liter ("Kinek" / "kinek") - grupujemy bez jej rozróżniania.

import { industryOfStation, townGrowth, townSize } from "./format.js";

/** Klucz właściciela (do grupowania, zaznaczenia i #owner= w adresie). */
export const ownerKey = (name) => String(name ?? "").trim().toLocaleLowerCase();

/**
 * dynamic (po splitAllOwners) -> Map(klucz -> { key, name, towns[], industries[], stations[], lines[],
 * main, size, profit12m, lineStats, fleet, score }). main = największe miasto widza, size = suma wielkości
 * jego miast, profit12m = zysk jego linii z 12 miesięcy (null bez statystyk), lineStats = statystyki jego
 * linii razem, fleet = pojazdy na jego liniach, score = majątek do sortowania.
 */
export function ownersIndex(dynamic, stationIndustry = industryOfStation(dynamic)) {
  const owners = new Map();
  const add = (kind, x) => {
    if (!x.owner) return;
    const key = ownerKey(x.owner);
    if (!owners.has(key)) owners.set(key, { key, spellings: new Map(), towns: [], industries: [], stations: [], lines: [] });
    const o = owners.get(key);
    o.spellings.set(x.owner, (o.spellings.get(x.owner) ?? 0) + 1);
    o[kind].push(x);
  };
  for (const x of dynamic.towns ?? []) add("towns", x);
  for (const x of dynamic.industries ?? []) add("industries", x);
  for (const x of dynamic.lines ?? []) add("lines", x);
  // Stacja zakładu tego samego widza jest już przy zakładzie.
  for (const s of dynamic.stations ?? []) {
    const industry = stationIndustry.get(s.id);
    const o = owners.get(ownerKey(s.owner));
    if (industry != null && o?.industries.some((i) => i.id === industry)) continue;
    add("stations", s);
  }

  for (const o of owners.values()) {
    // Nazwa do wyświetlania: najczęstszy zapis.
    o.name = [...o.spellings].sort((a, b) => b[1] - a[1])[0][0];
    delete o.spellings;
    o.towns.sort((a, b) => townSize(b) - townSize(a));
    o.industries.sort((a, b) => (b.level ?? 0) - (a.level ?? 0));
    o.lines.sort((a, b) => (b.vehicleCount ?? 0) - (a.vehicleCount ?? 0));
    o.main = o.towns[0] ?? null;
    o.size = o.towns.reduce((n, x) => n + townSize(x), 0);
    o.profit12m = sumOf(o.lines, (l) => l.stats?.profit12m);
    o.lineStats = lineTotals(o.lines);
    o.fleet = fleetOf(o.lines, dynamic.vehicles);
    o.score = ownerScore(o);
  }
  return owners;
}

/**
 * Majątek do sortowania (ten sam wzór co OwnerIndex.Score w Tf3Bridge, ranking !ranking): miasto 100 pkt plus
 * wielkość / 25 plus wzrost od startu gry × 100 (podwojone miasto +100, zmalałe bez kary), zakład 50, stacja i
 * linia po 1. Każde miasto dużo daje, duże i rozwinięte waży znacznie więcej. Do tego pojazdy na liniach widza według nośnika linii (vehicleWorth).
 */
const ownerScore = (o) =>
  o.towns.reduce((n, t) => n + 100 + townSize(t) / 25 + 100 * Math.max(0, townGrowth(t) ?? 0), 0) + 50 * o.industries.length + o.stations.length + o.lines.length
  + o.lines.reduce((n, l) => n + (l.vehicleCount ?? 0) * vehicleWorth(l.carrier), 0);

// Pojazd: pociąg i samolot 10 pkt, statek 2, drogowy (też tramwaj i nieznany nośnik) 1.
const vehicleWorth = (carrier) => (carrier === "RAIL" || carrier === "AIR" ? 10 : carrier === "WATER" ? 2 : 1);

// Suma wartości, null gdy żadna pozycja jej nie ma (brak statystyk to nie zero).
const sumOf = (items, get) => items.reduce((sum, x) => (get(x) == null ? sum : (sum ?? 0) + get(x)), null);

/**
 * Statystyki linii widza razem (lines[].stats z moda): zysk (też sumaryczny z zakończonych lat), przewiezione i przepustowość - sumy;
 * zapełnienie - średnia ważona liczbą pojazdów linii; ładunek w pojazdach teraz - suma per ładunek.
 * null, gdy żadna linia nie ma statystyk.
 */
function lineTotals(lines) {
  const withStats = lines.filter((l) => l.stats);
  if (!withStats.length) return null;
  const prevYear = withStats.find((l) => l.stats.prevYear != null)?.stats.prevYear ?? null;
  let loadSum = 0, loadWeight = 0;
  const cargo = new Map();
  for (const { stats: s, vehicleCount } of withStats) {
    if (s.load != null) {
      const w = vehicleCount || 1;
      loadSum += s.load * w;
      loadWeight += w;
    }
    for (const c of s.cargo ?? []) {
      const e = cargo.get(c.cargo) ?? { cargo: c.cargo, used: 0, capacity: 0 };
      e.used += c.used ?? 0;
      e.capacity += c.capacity ?? 0;
      cargo.set(c.cargo, e);
    }
  }
  return {
    profit12m: sumOf(withStats, (l) => l.stats.profit12m),
    prevYear,
    profitPrevYear: prevYear == null ? null : sumOf(withStats, (l) => (l.stats.prevYear === prevYear ? l.stats.profitPrevYear : null)),
    profitTotal: prevYear == null ? null : sumOf(withStats, (l) => (l.stats.prevYear === prevYear ? l.stats.profitTotal : null)),
    transported12m: sumOf(withStats, (l) => l.stats.transported12m),
    rate: sumOf(withStats, (l) => l.stats.rate),
    load: loadWeight ? loadSum / loadWeight : null,
    cargo: [...cargo.values()].sort((a, b) => b.capacity - a.capacity),
  };
}

/**
 * Pojazdy na liniach widza: { total, byCarrier: Map(nośnik -> liczba), enRoute, atStop, stopped }.
 * Bez dynamic.vehicles (pojazdy wyłączone w modzie) tylko total z lines[].vehicleCount, reszta null.
 */
function fleetOf(lines, vehicles) {
  if (!Array.isArray(vehicles)) {
    return { total: sumOf(lines, (l) => l.vehicleCount), byCarrier: null, enRoute: null, atStop: null, stopped: null };
  }
  const ids = new Set(lines.map((l) => l.id));
  const fleet = { total: 0, byCarrier: new Map(), enRoute: 0, atStop: 0, stopped: 0 };
  for (const v of vehicles) {
    if (!ids.has(v.line)) continue;
    fleet.total++;
    fleet.byCarrier.set(v.carrier, (fleet.byCarrier.get(v.carrier) ?? 0) + 1);
    if (v.stopped) fleet.stopped++;
    else if (v.state === "AT_TERMINAL") fleet.atStop++;
    else fleet.enRoute++;
  }
  return fleet;
}
