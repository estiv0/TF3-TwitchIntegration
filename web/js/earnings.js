// Zarobek linii przypisany do miast: zysk linii dzielony według jej przystanków.
// Przystanek należy do miasta, którego nazwę zawiera (najdłuższa pasująca, np. "Jelenia Góra", nie "Góra");
// bez nazwy miasta (przystanki od ulic: "Mickiewicza", "Rynek") - do najbliższego miasta w promieniu
// TOWN_RADIUS; dalej (porty, kopalnie w polu) - "poza miastami".

const TOWN_RADIUS = 1500; // m

/**
 * -> { towns: Map(id miasta -> wpis), outside: wpis }, wpis = { profit12m, profitPrevYear, prevYear,
 *    lines: [{ line, share, profit12m }] }; share = część przystanków linii w mieście (0..1).
 */
export function townEarnings(dynamic) {
  const townOf = stopTownResolver(dynamic.towns ?? []);
  const towns = new Map();
  const outside = entry();

  for (const line of dynamic.lines ?? []) {
    const stops = line.stops ?? [];
    if (!stops.length || !line.stats) continue;
    const counts = new Map(); // id miasta (null = poza miastami) -> liczba przystanków
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

// Przystanek -> id miasta albo null. Wyniki po nazwie trzymamy, bo stacje powtarzają się na wielu liniach.
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

// Nazwa miasta jako całe słowo(a), bez rozróżniania wielkości liter ("Góra" nie trafia w "Górna").
const wordRegex = (name) =>
  new RegExp(`(?<![\\p{L}\\p{N}])${name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}(?![\\p{L}\\p{N}])`, "iu");
