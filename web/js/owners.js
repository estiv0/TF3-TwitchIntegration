// Majątek widzów: miasta, zakłady, stacje i linie z właścicielem w nazwie ("Nazwa | Widz", splitAllOwners).
// Ten sam widz bywa zapisany różną wielkością liter ("Kinek" / "kinek") - grupujemy bez jej rozróżniania.

import { industryOfStation, townSize } from "./format.js";

/** Klucz właściciela (do grupowania, zaznaczenia i #owner= w adresie). */
export const ownerKey = (name) => String(name ?? "").trim().toLocaleLowerCase();

/**
 * dynamic (po splitAllOwners) -> Map(klucz -> { key, name, towns[], industries[], stations[], lines[],
 * main, size, profit12m, score }). main = największe miasto widza, size = suma wielkości jego miast,
 * profit12m = zysk jego linii z 12 miesięcy (null bez statystyk), score = majątek do sortowania.
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
    o.profit12m = o.lines.reduce((sum, l) => (l.stats?.profit12m == null ? sum : (sum ?? 0) + l.stats.profit12m), null);
    // Miasto i zakład są warte więcej niż stacja czy linia.
    o.score = 3 * (o.towns.length + o.industries.length) + o.stations.length + o.lines.length;
  }
  return owners;
}
