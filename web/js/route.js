// Trasa linii po sieci: graf z krawędzi network.json, przystanki dociągane do najbliższych
// węzłów właściwego typu (tor / droga) w tym samym spójnym kawałku sieci, odcinki między
// przystankami szukane przez A*.
// Gra nie eksportuje ścieżki linii, więc to przybliżenie: najkrótsza droga; po torach z kierunkiem
// jazdy (bez zawracania na rozjazdach, przez przystanek dalej w tę samą stronę), po drogach bez.
// Układ współrzędnych jak w map.js: (x, -y) ze świata gry.

const SNAP_DISTANCE = 300; // m - dalej od sieci przystanek łączymy linią prostą
const SNAP_CANDIDATES = 8; // tyle najbliższych węzłów przystanku bierzemy pod uwagę przy łączeniu odcinka
const STOP_RADIUS = 60; // m - węzły sieci kierunkowej tak blisko przystanku należą do stacji (perony)

// Nośnik linii -> typ krawędzi, po której jeździ (WATER/AIR: linia prosta).
const ROAD_FOR_CARRIER = { RAIL: "TRACK", ROAD: "STREET", TRAM: "STREET" };
// Sieci, po których pojazd nie zawraca w dowolnym miejscu (pociąg: tylko na stacji końcowej).
const DIRECTED = new Set(["TRACK"]);

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

      // kierunek wyjazdu z węzła (dx, dy) i wjazdu do węzła docelowego (ax, ay): styczne krawędzi
      const t0x = e[f.tx0], t0y = -e[f.ty0], t1x = e[f.tx1], t1y = -e[f.ty1];
      const a = node(n0, x0, y0), b = node(n1, x1, y1);
      a.roads.add(edge.road);
      b.roads.add(edge.road);
      a.adj.push({ to: b, edge, len, forward: true, dx: t0x, dy: t0y, ax: t1x, ay: t1y });
      b.adj.push({ to: a, edge, len, forward: false, dx: -t1x, dy: -t1y, ax: -t0x, ay: -t0y });
    }

    // Siatka węzłów (komórka = SNAP_DISTANCE): przy wszystkich liniach na mapie przystanków jest dużo.
    this.grid = new Map();
    for (const n of this.nodes.values()) {
      const k = cellKey(Math.floor(n.x / SNAP_DISTANCE), Math.floor(n.y / SNAP_DISTANCE));
      const cell = this.grid.get(k);
      if (cell) cell.push(n);
      else this.grid.set(k, [n]);
    }

    // Spójne kawałki sieci osobno dla dróg i torów (n.comp[road] = numer kawałka). Eksport ma
    // odcięte fragmenty (np. dojazdy do stacji, których krawędzie łączące należą do konstrukcji),
    // a najbliższy przystankowi węzeł bywa w takim fragmencie - wtedy A* nie znalazłby drogi.
    let comp = 0;
    for (const start of this.nodes.values()) {
      start.comp ??= {};
      for (const road of start.roads) {
        if (start.comp[road] != null) continue;
        start.comp[road] = ++comp;
        const stack = [start];
        while (stack.length) {
          for (const a of stack.pop().adj) {
            if (a.edge.road !== road) continue;
            a.to.comp ??= {};
            if (a.to.comp[road] != null) continue;
            a.to.comp[road] = comp;
            stack.push(a.to);
          }
        }
      }
    }
  }

  // Do limit najbliższych węzłów danego typu w promieniu SNAP_DISTANCE: [{ n, d }] od najbliższego.
  nearestNodes(x, y, road, limit = SNAP_CANDIDATES) {
    const out = [];
    const cx = Math.floor(x / SNAP_DISTANCE), cy = Math.floor(y / SNAP_DISTANCE);
    for (let i = cx - 1; i <= cx + 1; i++) {
      for (let j = cy - 1; j <= cy + 1; j++) {
        for (const n of this.grid.get(cellKey(i, j)) ?? []) {
          if (!n.roads.has(road)) continue;
          const d = Math.hypot(n.x - x, n.y - y);
          if (d < SNAP_DISTANCE) out.push({ n, d });
        }
      }
    }
    return out.sort((a, b) => a.d - b.d).slice(0, limit);
  }

  // Para węzłów (po jednym z kandydatów każdego przystanku) w tym samym kawałku sieci, z najmniejszą
  // sumą odległości od przystanków; null, gdy przystanków nie łączy żaden kawałek.
  snapPair(ca, cb, road) {
    let best = null, bestD = Infinity;
    for (const a of ca) {
      if (a.d >= bestD) break;
      for (const b of cb) {
        if (a.d + b.d >= bestD) break;
        if (a.n.comp[road] === b.n.comp[road]) { best = [a.n, b.n]; bestD = a.d + b.d; }
      }
    }
    return best;
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

  // Węzły przystanku na sieci kierunkowej: wszystkie w promieniu STOP_RADIUS od niego (stacja ma kilka
  // torów peronowych, a pociąg staje na tym, na który mu po drodze), a gdy żadnego - najbliższe.
  // Zbiór z położeniem przystanku (x, y) dla heurystyki A*.
  stopNodes(p, road) {
    const near = this.nearestNodes(p.x, p.y, road, Infinity);
    const inStation = near.filter((c) => c.d <= STOP_RADIUS);
    const set = new Set((inStation.length ? inStation : near.slice(0, SNAP_CANDIDATES)).map((c) => c.n));
    return Object.assign(set, { x: p.x, y: p.y });
  }

  // A* z kierunkiem jazdy z dowolnego węzła zbioru from (kierunek jazdy: heading = { x, y }, { 0, 0 } =
  // dowolny; perony stacji są równoległe, więc kierunek pasuje do każdego) przez kolejne zbiory węzłów
  // targets (stopNodes). Z węzła tylko krawędziami, które nie zawracają względem kierunku wjazdu.
  // Dalsze zbiory to podgląd następnego odcinka: dojazd do targets[0] ma pozwolić pojechać dalej bez
  // zawracania. Zwraca { from, steps, heading } dla odcinka do targets[0] (from: węzeł startu,
  // heading: kierunek wjazdu do targets[0]) albo null.
  directedPath(from, targets, road, heading) {
    const last = targets.length - 1;
    const rest = targets.map((_, k) => {
      let d = 0;
      for (let m = k; m < last; m++) d += Math.hypot(targets[m + 1].x - targets[m].x, targets[m + 1].y - targets[m].y);
      return Math.max(0, d - 2 * STOP_RADIUS * (last - k));
    });
    // stan: krawędź (wpis adj), którą wjechano do węzła, + numer zbioru, do którego jedziemy
    const states = targets.map(() => new Map());
    const heap = new MinHeap();
    const reach = (a, k, g, prev) => {
      while (k < last && targets[k].has(a.to)) k++; // wjazd na przystanek: dalej do następnego
      let s = states[k].get(a);
      if (!s) states[k].set(a, (s = { a, k, g: Infinity, prev: null }));
      if (g >= s.g) return;
      s.g = g;
      s.prev = prev;
      const t = targets[k];
      heap.push(s, g + Math.max(0, Math.hypot(a.to.x - t.x, a.to.y - t.y) - STOP_RADIUS) + rest[k]);
    };
    for (const n of from) if (!targets[0].has(n)) reach({ to: n, ax: heading.x, ay: heading.y }, 0, 0, null);

    while (heap.size) {
      const cur = heap.pop();
      if (cur.k === last && targets[last].has(cur.a.to)) {
        const path = [];
        let s = cur;
        for (; s.prev; s = s.prev) path.push(s);
        path.reverse();
        const steps = [];
        for (const p of path) {
          steps.push({ edge: p.a.edge, forward: p.a.forward });
          if (targets[0].has(p.a.to)) return { from: s.a.to, steps, heading: { x: p.a.ax, y: p.a.ay } };
        }
        return null;
      }
      for (const a of cur.a.to.adj) {
        if (a.edge.road !== road || a.dx * cur.a.ax + a.dy * cur.a.ay < 0) continue;
        reach(a, cur.k, cur.g + a.len, cur);
      }
    }
    return null;
  }

  // Odcinek po sieci kierunkowej ze zbioru from do zbioru to (next: zbiór następnego przystanku).
  // Pociąg jedzie dalej w kierunku, w którym dojechał do przystanku, i tak, żeby z to ruszyć dalej
  // bez zawracania; gdy się nie da - bez podglądu; potem tak samo z zawracaniem na przystanku
  // (stacja końcowa). Zwraca wynik directedPath albo null.
  directedLeg(from, to, next, road, heading) {
    const hd = heading ?? { x: 0, y: 0 };
    for (const dir of heading ? [hd, { x: -hd.x, y: -hd.y }] : [hd]) {
      for (const targets of [[to, next], [to]]) {
        const r = this.directedPath(from, targets, road, dir);
        if (r) return r;
      }
    }
    return null;
  }

  // Odcinki pętli linii po sieci kierunkowej: [{ from, steps }] (null: odcinka nie da się poprowadzić).
  // Kierunek jazdy przy pierwszym przystanku nieznany: pierwsze okrążenie tylko po to, żeby poznać
  // kierunek dojazdu do niego na ostatnim odcinku, drugie już naprawdę.
  directedLegs(stops, road) {
    const n = stops.length;
    const sets = stops.map((s) => this.stopNodes(s, road));
    const out = [];
    let heading = null;
    for (let lap = 0; lap < 2; lap++) {
      for (let i = 0; i < n; i++) {
        out[i] = this.directedLeg(sets[i], sets[(i + 1) % n], sets[(i + 2) % n], road, heading);
        heading = out[i]?.heading ?? null;
      }
    }
    return out;
  }

  // Path2D trasy linii (zamknięta pętla, jak w grze) + bbox + łamana po kolei (poly: do sprawdzania,
  // czy linia jest na ekranie, i do ruchu pojazdów; cum = odległość od startu do każdego punktu,
  // length = długość pętli) + legs: indeks przystanku na linii -> { start, end, first, last }, odcinek
  // dojazdu do niego od poprzedniego przystanku (odległości wzdłuż pętli i indeksy w poly). Przystanki bez pozycji pomijamy.
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
    if (stops.length < 2) return { path, stops, bbox: bboxOf(stops), poly: stops, cum: [0], length: 0, legs };

    const road = ROAD_FOR_CARRIER[line.carrier] ?? (line.carrier ? null : this.guessRoad(stops[0]));
    const candidates = stops.map((s) => (road ? this.nearestNodes(s.x, s.y, road) : []));
    const directed = DIRECTED.has(road) ? this.directedLegs(stops, road) : [];
    const poly = [];

    for (let i = 0; i < stops.length; i++) {
      const j = (i + 1) % stops.length;
      const a = stops[i], b = stops[j];
      // gdy po sieci kierunkowej się nie da: najkrótsza droga bez kierunku
      let na = directed[i]?.from, steps = directed[i]?.steps;
      if (!directed[i]) {
        const pair = this.snapPair(candidates[i], candidates[j], road);
        na = pair?.[0];
        steps = pair ? this.path(pair[0], pair[1], road) : null;
      }
      const first = poly.length;
      poly.push(a);
      path.moveTo(a.x, a.y);
      if (steps) {
        path.lineTo(na.x, na.y);
        poly.push(na);
        for (const { edge: e, forward } of steps) {
          if (forward) path.bezierCurveTo(e.c0x, e.c0y, e.c1x, e.c1y, e.x1, e.y1);
          else path.bezierCurveTo(e.c1x, e.c1y, e.c0x, e.c0y, e.x0, e.y0);
          // kilka punktów z krzywej, żeby pojazdy na długich łukach nie ścinały zakrętu
          for (const t of BEZIER_SAMPLES) poly.push(bezierPoint(e, forward ? t : 1 - t));
          poly.push(forward ? { x: e.x1, y: e.y1 } : { x: e.x0, y: e.y0 });
        }
      }
      path.lineTo(b.x, b.y);
      legs.set(index[j], { first, last: poly.length }); // last = indeks punktu b (początek następnego)
    }
    poly.push(stops[0]);

    const cum = new Float64Array(poly.length);
    for (let i = 1; i < poly.length; i++) cum[i] = cum[i - 1] + Math.hypot(poly[i].x - poly[i - 1].x, poly[i].y - poly[i - 1].y);
    for (const [k, { first, last }] of legs) legs.set(k, { first, last, start: cum[first], end: cum[last] });
    return { path, stops, bbox: bboxOf(poly), poly, cum, length: cum[poly.length - 1], legs };
  }

  // Linia bez pojazdów nie ma znanego nośnika - wybierz typ sieci bliższy pierwszemu przystankowi.
  guessRoad(p) {
    const t = this.nearestNode(p.x, p.y, "TRACK"), s = this.nearestNode(p.x, p.y, "STREET");
    if (!t) return s ? "STREET" : null;
    if (!s) return "TRACK";
    return Math.hypot(t.x - p.x, t.y - p.y) <= Math.hypot(s.x - p.x, s.y - p.y) ? "TRACK" : "STREET";
  }
}

/** Punkt trasy (wynik routeLine) w odległości d wzdłuż pętli (d dowolne, liczone modulo długość). */
export function pointAtDistance(route, d) {
  const { poly, cum, length } = route;
  if (!(length > 0)) return poly[0];
  d = ((d % length) + length) % length;
  let lo = 0, hi = cum.length - 1; // ostatni punkt z cum[i] <= d
  while (hi - lo > 1) {
    const mid = (lo + hi) >> 1;
    if (cum[mid] <= d) lo = mid;
    else hi = mid;
  }
  const a = poly[lo], b = poly[hi], seg = cum[hi] - cum[lo];
  const f = seg > 0 ? (d - cum[lo]) / seg : 0;
  return { x: a.x + (b.x - a.x) * f, y: a.y + (b.y - a.y) * f };
}

/**
 * Rzut punktu (układ ścieżki) na odcinek trasy dojazdu do przystanku stop: { d, dist } - odległość
 * wzdłuż pętli i odległość punktu od trasy; null, gdy linia nie ma takiego odcinka. Tylko ten odcinek,
 * bo tam i z powrotem pociąg jedzie po tych samych torach - cała pętla nie rozstrzygnęłaby kierunku.
 */
export function projectOnLeg(route, stop, p) {
  const leg = route.legs.get(stop);
  if (!leg) return null;
  const { poly, cum } = route;
  let best = null;
  for (let i = leg.first; i < leg.last; i++) {
    const a = poly[i], b = poly[i + 1];
    const dx = b.x - a.x, dy = b.y - a.y, len2 = dx * dx + dy * dy;
    const t = len2 > 0 ? Math.max(0, Math.min(1, ((p.x - a.x) * dx + (p.y - a.y) * dy) / len2)) : 0;
    const dist = Math.hypot(a.x + dx * t - p.x, a.y + dy * t - p.y);
    if (!best || dist < best.dist) best = { d: cum[i] + (cum[i + 1] - cum[i]) * t, dist };
  }
  return best;
}

const BEZIER_SAMPLES = [0.25, 0.5, 0.75];

function bezierPoint(e, t) {
  const u = 1 - t, a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
  return { x: a * e.x0 + b * e.c0x + c * e.c1x + d * e.x1, y: a * e.y0 + b * e.c0y + c * e.c1y + d * e.y1 };
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
