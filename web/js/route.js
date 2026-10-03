// Trasa linii po sieci: graf z krawędzi network.json, przystanki dociągane do najbliższego
// węzła właściwego typu (tor / droga), odcinki między przystankami szukane przez A*.
// Gra nie eksportuje ścieżki linii, więc to przybliżenie (najkrótsza droga, bez kierunkowości).
// Układ współrzędnych jak w map.js: (x, -y) ze świata gry.

const SNAP_DISTANCE = 300; // m - dalej od sieci przystanek łączymy linią prostą

// Nośnik linii -> typ krawędzi, po której jeździ (WATER/AIR: linia prosta).
const ROAD_FOR_CARRIER = { RAIL: "TRACK", ROAD: "STREET", TRAM: "STREET" };

export class RouteGraph {
  constructor(network) {
    const f = Object.fromEntries(network.fields.map((name, i) => [name, i]));
    this.nodes = new Map(); // id -> { x, y, adj: [{ to, edge, len }] }
    this.edges = [];

    const node = (id, x, y) => {
      let n = this.nodes.get(id);
      if (!n) this.nodes.set(id, (n = { id, x, y, adj: [], roads: new Set() }));
      return n;
    };

    for (const e of network.edges) {
      const n0 = e[f.node0], n1 = e[f.node1];
      if (!n0 || !n1) continue;
      const x0 = e[f.x0], y0 = -e[f.y0], x1 = e[f.x1], y1 = -e[f.y1];
      const edge = {
        road: e[f.road],
        x0, y0, x1, y1,
        c0x: x0 + e[f.tx0] / 3, c0y: y0 - e[f.ty0] / 3,
        c1x: x1 - e[f.tx1] / 3, c1y: y1 + e[f.ty1] / 3,
      };
      // długość: średnia cięciwy i łamanej przez punkty kontrolne (wystarczy dla krzywych TF)
      const chord = Math.hypot(x1 - x0, y1 - y0);
      const poly = Math.hypot(edge.c0x - x0, edge.c0y - y0) + Math.hypot(edge.c1x - edge.c0x, edge.c1y - edge.c0y)
        + Math.hypot(x1 - edge.c1x, y1 - edge.c1y);
      const len = (chord + poly) / 2;
      this.edges.push(edge);

      const a = node(n0, x0, y0), b = node(n1, x1, y1);
      a.roads.add(edge.road);
      b.roads.add(edge.road);
      a.adj.push({ to: b, edge, len, forward: true });
      b.adj.push({ to: a, edge, len, forward: false });
    }

    // Siatka węzłów (komórka = SNAP_DISTANCE): przy wszystkich liniach na mapie przystanków jest dużo.
    this.grid = new Map();
    for (const n of this.nodes.values()) {
      const k = cellKey(Math.floor(n.x / SNAP_DISTANCE), Math.floor(n.y / SNAP_DISTANCE));
      const cell = this.grid.get(k);
      if (cell) cell.push(n);
      else this.grid.set(k, [n]);
    }
  }

  nearestNode(x, y, road) {
    let best = null, bestD = SNAP_DISTANCE;
    const cx = Math.floor(x / SNAP_DISTANCE), cy = Math.floor(y / SNAP_DISTANCE);
    for (let i = cx - 1; i <= cx + 1; i++) {
      for (let j = cy - 1; j <= cy + 1; j++) {
        for (const n of this.grid.get(cellKey(i, j)) ?? []) {
          if (road && !n.roads.has(road)) continue;
          const d = Math.hypot(n.x - x, n.y - y);
          if (d < bestD) { best = n; bestD = d; }
        }
      }
    }
    return best;
  }

  // A* między węzłami, tylko po krawędziach danego typu. Zwraca listę kroków {edge, forward} albo null.
  path(from, to, road) {
    if (from === to) return [];
    const h = (n) => Math.hypot(n.x - to.x, n.y - to.y);
    const g = new Map([[from, 0]]);
    const came = new Map();
    const heap = new MinHeap();
    heap.push(from, h(from));

    while (heap.size) {
      const cur = heap.pop();
      if (cur === to) {
        const steps = [];
        for (let n = to; n !== from;) {
          const step = came.get(n);
          steps.push(step);
          n = step.prev;
        }
        return steps.reverse();
      }
      const gc = g.get(cur);
      for (const a of cur.adj) {
        if (a.edge.road !== road) continue;
        const ng = gc + a.len;
        if (ng < (g.get(a.to) ?? Infinity)) {
          g.set(a.to, ng);
          came.set(a.to, { prev: cur, edge: a.edge, forward: a.forward });
          heap.push(a.to, ng + h(a.to));
        }
      }
    }
    return null;
  }

  // Path2D trasy linii (zamknięta pętla, jak w grze) + bbox + łamana po kolei (do sprawdzania,
  // czy linia jest na ekranie) + legs: indeks przystanku na linii -> łamana dojazdu do niego od
  // poprzedniego przystanku (do stawiania pojazdów w drodze). Przystanki bez pozycji pomijamy.
  routeLine(line) {
    const all = line.stops ?? [];
    const index = [];  // indeks w stops (po odfiltrowaniu) -> indeks przystanku na linii
    const stops = [];
    all.forEach((s, i) => {
      if (!s.pos) return;
      index.push(i);
      stops.push({ x: s.pos[0], y: -s.pos[1] });
    });
    const path = new Path2D();
    const legs = new Map();
    if (stops.length < 2) return { path, stops, bbox: bboxOf(stops), poly: stops, legs };

    const road = ROAD_FOR_CARRIER[line.carrier] ?? (line.carrier ? null : this.guessRoad(stops[0]));
    const snapped = stops.map((s) => (road ? this.nearestNode(s.x, s.y, road) : null));
    const poly = [];

    for (let i = 0; i < stops.length; i++) {
      const j = (i + 1) % stops.length;
      const a = stops[i], b = stops[j];
      const na = snapped[i], nb = snapped[j];
      const steps = na && nb ? this.path(na, nb, road) : null;
      const leg = [a];
      path.moveTo(a.x, a.y);
      if (steps) {
        path.lineTo(na.x, na.y);
        leg.push(na);
        for (const { edge: e, forward } of steps) {
          if (forward) path.bezierCurveTo(e.c0x, e.c0y, e.c1x, e.c1y, e.x1, e.y1);
          else path.bezierCurveTo(e.c1x, e.c1y, e.c0x, e.c0y, e.x0, e.y0);
          leg.push(forward ? { x: e.x1, y: e.y1 } : { x: e.x0, y: e.y0 });
        }
      }
      path.lineTo(b.x, b.y);
      poly.push(...leg);
      leg.push(b);
      legs.set(index[j], leg);
    }
    poly.push(stops[0]);
    return { path, stops, bbox: bboxOf(poly), poly, legs };
  }

  // Linia bez pojazdów nie ma znanego nośnika - wybierz typ sieci bliższy pierwszemu przystankowi.
  guessRoad(p) {
    const t = this.nearestNode(p.x, p.y, "TRACK"), s = this.nearestNode(p.x, p.y, "STREET");
    if (!t) return s ? "STREET" : null;
    if (!s) return "TRACK";
    return Math.hypot(t.x - p.x, t.y - p.y) <= Math.hypot(s.x - p.x, s.y - p.y) ? "TRACK" : "STREET";
  }
}

/** Punkt w ułamku f (0..1) długości łamanej. */
export function pointAlong(pts, f) {
  let total = 0;
  for (let i = 1; i < pts.length; i++) total += Math.hypot(pts[i].x - pts[i - 1].x, pts[i].y - pts[i - 1].y);
  let left = total * f;
  for (let i = 1; i < pts.length; i++) {
    const a = pts[i - 1], b = pts[i];
    const d = Math.hypot(b.x - a.x, b.y - a.y);
    if (left <= d && d > 0) return { x: a.x + (b.x - a.x) * left / d, y: a.y + (b.y - a.y) * left / d };
    left -= d;
  }
  return pts[pts.length - 1];
}

/** Czy trasa (wynik routeLine) przecina prostokąt { minX, minY, maxX, maxY } w układzie ścieżki. */
export function routeInRect(route, r) {
  const b = route?.bbox;
  if (!b || b.maxX < r.minX || b.minX > r.maxX || b.maxY < r.minY || b.minY > r.maxY) return false;
  if (b.minX >= r.minX && b.maxX <= r.maxX && b.minY >= r.minY && b.maxY <= r.maxY) return true;
  const p = route.poly;
  if (p.length === 1) return true; // pojedynczy przystanek: bbox to punkt, już sprawdzony
  for (let i = 1; i < p.length; i++) if (segmentInRect(p[i - 1], p[i], r)) return true;
  return false;
}

// Odcinek przecina prostokąt, gdy ich bboxy się nakładają, a prosta odcinka nie zostawia
// wszystkich narożników prostokąta po jednej stronie.
function segmentInRect(a, b, r) {
  if (Math.max(a.x, b.x) < r.minX || Math.min(a.x, b.x) > r.maxX
    || Math.max(a.y, b.y) < r.minY || Math.min(a.y, b.y) > r.maxY) return false;
  const side = (x, y) => Math.sign((b.x - a.x) * (y - a.y) - (b.y - a.y) * (x - a.x));
  const s = [side(r.minX, r.minY), side(r.maxX, r.minY), side(r.minX, r.maxY), side(r.maxX, r.maxY)];
  return !(s.every((v) => v > 0) || s.every((v) => v < 0));
}

const cellKey = (i, j) => `${i},${j}`;

function bboxOf(pts) {
  if (!pts.length) return null;
  let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
  for (const p of pts) {
    if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x;
    if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y;
  }
  return { minX, minY, maxX, maxY };
}

class MinHeap {
  constructor() { this.items = []; }
  get size() { return this.items.length; }

  push(value, key) {
    const a = this.items;
    a.push({ value, key });
    for (let i = a.length - 1; i > 0;) {
      const p = (i - 1) >> 1;
      if (a[p].key <= a[i].key) break;
      [a[p], a[i]] = [a[i], a[p]];
      i = p;
    }
  }

  pop() {
    const a = this.items;
    const top = a[0];
    const last = a.pop();
    if (a.length) {
      a[0] = last;
      for (let i = 0; ;) {
        const l = 2 * i + 1, r = l + 1;
        let m = i;
        if (l < a.length && a[l].key < a[m].key) m = l;
        if (r < a.length && a[r].key < a[m].key) m = r;
        if (m === i) break;
        [a[m], a[i]] = [a[i], a[m]];
        i = m;
      }
    }
    return top.value;
  }
}
