// Viewers' estates: towns, industries, stations and lines with an owner in the name ("Name | Viewer", splitAllOwners).
// The same viewer is sometimes written with different letter case ("Ala" / "ala") - we group case-insensitively.

import { industryOfStation, townGrowth, townSize } from "./format.js";

/** The owner key (for grouping, selection and #owner= in the address). */
export const ownerKey = (name) => String(name ?? "").trim().toLocaleLowerCase();

/**
 * dynamic (after splitAllOwners) -> Map(key -> { key, name, towns[], industries[], stations[], lines[],
 * main, size, profit12m, lineStats, fleet, score }). main = the viewer's largest town, size = the sum of the sizes
 * of their towns, profit12m = the 12-month profit of their lines (null without statistics), lineStats = the statistics of their
 * lines combined, fleet = vehicles on their lines, score = the estate for sorting.
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
  // A station of the same viewer's industry is already counted with the industry.
  for (const s of dynamic.stations ?? []) {
    const industry = stationIndustry.get(s.id);
    const o = owners.get(ownerKey(s.owner));
    if (industry != null && o?.industries.some((i) => i.id === industry)) continue;
    add("stations", s);
  }

  for (const o of owners.values()) {
    // The display name: the most common spelling.
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
 * The estate for sorting (the same formula as OwnerIndex.Score in Tf3Bridge, the !ranking command): town 100 pts plus
 * size / 25 plus growth since game start × 100 (a doubled town +100, a shrunk one no penalty), industry 50, station and
 * line 1 each. Every town is worth a lot, a big and developed one much more. Plus vehicles on the viewer's lines by line carrier (vehicleWorth).
 */
const ownerScore = (o) =>
  o.towns.reduce((n, t) => n + 100 + townSize(t) / 25 + 100 * Math.max(0, townGrowth(t) ?? 0), 0) + 50 * o.industries.length + o.stations.length + o.lines.length
  + o.lines.reduce((n, l) => n + (l.vehicleCount ?? 0) * vehicleWorth(l.carrier), 0);

// Vehicle: train and plane 10 pts, ship 2, road (also tram and an unknown carrier) 1.
const vehicleWorth = (carrier) => (carrier === "RAIL" || carrier === "AIR" ? 10 : carrier === "WATER" ? 2 : 1);

// Sum of the values, null when none of the items has it (missing statistics aren't zero).
const sumOf = (items, get) => items.reduce((sum, x) => (get(x) == null ? sum : (sum ?? 0) + get(x)), null);

/**
 * The statistics of a viewer's lines combined (lines[].stats from the mod): profit (also the total over finished years), transported and capacity - sums;
 * load factor - the average weighted by the lines' vehicle counts; cargo in vehicles now - the sum per cargo.
 * null when no line has statistics.
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
 * Vehicles on the viewer's lines: { total, byCarrier: Map(carrier -> count), enRoute, atStop, stopped }.
 * Without dynamic.vehicles (vehicles turned off in the mod) only total from lines[].vehicleCount, the rest null.
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
