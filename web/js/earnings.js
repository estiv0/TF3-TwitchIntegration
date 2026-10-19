// Line earnings attributed to towns: a line's profit split by its stops.
// A stop belongs to the town whose name it contains (the longest match, e.g. "Jelenia Góra", not "Góra");
// without a town name (stops named after streets: "Mickiewicza", "Rynek") - to the nearest town within
// TOWN_RADIUS; further away (harbors, mines in the countryside) - "outside towns".

const TOWN_RADIUS = 1500; // m

/**
 * -> { towns: Map(town id -> entry), outside: entry }, entry = { profit12m, profitPrevYear, prevYear,
 *    lines: [{ line, share, profit12m }] }; share = the share of the line's stops in the town (0..1).
 */
export function townEarnings(dynamic) {
  const townOf = stopTownResolver(dynamic.towns ?? []);
  const towns = new Map();
  const outside = entry();

  for (const line of dynamic.lines ?? []) {
    const stops = line.stops ?? [];
    if (!stops.length || !line.stats) continue;
    const counts = new Map(); // town id (null = outside towns) -> number of stops
    for (const stop of stops) {
      const id = townOf(stop);
      counts.set(id, (counts.get(id) ?? 0) + 1);
    }
    for (const [id, n] of counts) {
      let e = outside;
      if (id != null) {
        if (!towns.has(id)) towns.set(id, entry());
        e = towns.get(id);
      }
      add(e, line, n / stops.length);
    }
  }
  for (const e of [...towns.values(), outside]) e.lines.sort((a, b) => (b.profit12m ?? 0) - (a.profit12m ?? 0));
  return { towns, outside };
}

const entry = () => ({ profit12m: null, profitPrevYear: null, prevYear: null, lines: [] });

function add(e, line, share) {
  const s = line.stats;
  const part = (v) => (v == null ? null : v * share);
  const profit12m = part(s.profit12m);
  if (profit12m != null) e.profit12m = (e.profit12m ?? 0) + profit12m;
  if (s.profitPrevYear != null) {
    e.profitPrevYear = (e.profitPrevYear ?? 0) + part(s.profitPrevYear);
    e.prevYear = s.prevYear;
  }
  e.lines.push({ line, share, profit12m });
}

// Stop -> town id or null. Results are kept by name, because stations repeat on many lines.
function stopTownResolver(towns) {
  const named = towns.filter((t) => t.name).map((t) => ({ id: t.id, re: wordRegex(t.name), len: t.name.length }))
    .sort((a, b) => b.len - a.len);
  const placed = towns.filter((t) => t.pos);
  const byName = new Map();

  return (stop) => {
    const name = stop.name ?? "";
    if (!byName.has(name)) byName.set(name, named.find((t) => t.re.test(name))?.id ?? null);
    const id = byName.get(name);
    if (id != null || !stop.pos) return id;

    let best = null, bestD = TOWN_RADIUS;
    for (const t of placed) {
      const d = Math.hypot(t.pos[0] - stop.pos[0], t.pos[1] - stop.pos[1]);
      if (d <= bestD) { best = t.id; bestD = d; }
    }
    return best;
  };
}

// A town name as whole word(s), case-insensitive ("Góra" doesn't match "Górna").
const wordRegex = (name) =>
  new RegExp(`(?<![\\p{L}\\p{N}])${name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}(?![\\p{L}\\p{N}])`, "iu");
