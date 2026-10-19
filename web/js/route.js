// A line's route over the network: a graph from the network.json edges, stops snapped to the nearest
// nodes of the right type (track / street) in the same connected piece of the network, segments between
// stops searched with A*.
// The game doesn't export the line path, so this is an approximation: the shortest way; on tracks with the direction
// of travel (no U-turns at switches, through a stop onwards in the same direction), on streets without it.
// Coordinates as in map.js: (x, -y) from the game world.

const SNAP_DISTANCE = 300; // m - further from the network a stop is connected with a straight line
const SNAP_CANDIDATES = 8; // how many of a stop's nearest nodes we consider when connecting a segment
const STOP_RADIUS = 60; // m - nodes of a directional network this close to the stop belong to the station (platforms)

// Line carrier -> the type of edge it runs on (WATER/AIR: a straight line).
const ROAD_FOR_CARRIER = { RAIL: "TRACK", ROAD: "STREET", TRAM: "STREET" };
// Networks where a vehicle doesn't turn around just anywhere (train: only at a terminal station).
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
      // length: the average of the chord and the polyline through the control points (enough for TF curves)
      const chord = Math.hypot(x1 - x0, y1 - y0);
      const poly = Math.hypot(edge.c0x - x0, edge.c0y - y0) + Math.hypot(edge.c1x - edge.c0x, edge.c1y - edge.c0y)
        + Math.hypot(x1 - edge.c1x, y1 - edge.c1y);
      const len = (chord + poly) / 2;
      this.edges.push(edge);

      // the direction leaving the node (dx, dy) and entering the target node (ax, ay): the edge tangents
      const t0x = e[f.tx0], t0y = -e[f.ty0], t1x = e[f.tx1], t1y = -e[f.ty1];
      const a = node(n0, x0, y0), b = node(n1, x1, y1);
      a.roads.add(edge.road);
      b.roads.add(edge.road);
      a.adj.push({ to: b, edge, len, forward: true, dx: t0x, dy: t0y, ax: t1x, ay: t1y });
      b.adj.push({ to: a, edge, len, forward: false, dx: -t1x, dy: -t1y, ax: -t0x, ay: -t0y });
    }

    // A node grid (cell = SNAP_DISTANCE): with all lines on the map there are many stops.
    this.grid = new Map();
    for (const n of this.nodes.values()) {
      const k = cellKey(Math.floor(n.x / SNAP_DISTANCE), Math.floor(n.y / SNAP_DISTANCE));
      const cell = this.grid.get(k);
      if (cell) cell.push(n);
      else this.grid.set(k, [n]);
    }

    // Connected pieces of the network, separately for streets and tracks (n.comp[road] = piece number). The export has
    // cut-off fragments (e.g. station access roads whose connecting edges belong to the construction),
    // and the node nearest to a stop is sometimes in such a fragment - then A* wouldn't find a way.
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

  // Up to limit nearest nodes of a given type within SNAP_DISTANCE: [{ n, d }] from the nearest.
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

  // A pair of nodes (one from each stop's candidates) in the same network piece, with the smallest
  // sum of distances from the stops; null when no piece connects the stops.
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

  // A* between nodes, only along edges of a given type. Returns a list of {edge, forward} steps, or null.
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

  // A stop's nodes on a directional network: all within STOP_RADIUS of it (a station has several
  // platform tracks, and the train stops at the one on its way), and when there are none - the nearest.
  // A set with the stop position (x, y) for the A* heuristic.
  stopNodes(p, road) {
    const near = this.nearestNodes(p.x, p.y, road, Infinity);
    const inStation = near.filter((c) => c.d <= STOP_RADIUS);
    const set = new Set((inStation.length ? inStation : near.slice(0, SNAP_CANDIDATES)).map((c) => c.n));
    return Object.assign(set, { x: p.x, y: p.y });
  }

  // A* with the direction of travel from any node of the from set (direction of travel: heading = { x, y }, { 0, 0 } =
  // any; station platforms are parallel, so the direction fits each of them) through successive node sets
  // targets (stopNodes). From a node only along edges that don't turn back relative to the entry direction.
  // The further sets are a preview of the next segment: reaching targets[0] must allow continuing without
  // turning back. Returns { from, steps, heading } for the segment to targets[0] (from: the start node,
  // heading: the direction of entry into targets[0]), or null.
  directedPath(from, targets, road, heading) {
    const last = targets.length - 1;
    const rest = targets.map((_, k) => {
      let d = 0;
      for (let m = k; m < last; m++) d += Math.hypot(targets[m + 1].x - targets[m].x, targets[m + 1].y - targets[m].y);
      return Math.max(0, d - 2 * STOP_RADIUS * (last - k));
    });
    // state: the edge (an adj entry) the node was entered by, + the number of the set we're heading to
    const states = targets.map(() => new Map());
    const heap = new MinHeap();
    const reach = (a, k, g, prev) => {
      while (k < last && targets[k].has(a.to)) k++; // entering a stop: on to the next one
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

  // A segment on a directional network from the from set to the to set (next: the next stop's set).
  // The train keeps going in the direction it reached the stop, and so that it can continue from to
  // without turning back; when that's impossible - without the preview; then the same with turning back at the stop
  // (a terminal station). Returns the directedPath result, or null.
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

  // The line loop segments on a directional network: [{ from, steps }] (null: the segment can't be routed).
  // The direction of travel at the first stop is unknown: the first lap only serves to learn
  // the direction of approach to it on the last segment, the second one is for real.
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

  // The Path2D of the line route (a closed loop, as in the game) + bbox + the polyline in order (poly: for checking
  // whether the line is on screen, and for vehicle movement; cum = the distance from the start to each point,
  // length = the loop length) + legs: stop index on the line -> { start, end, first, last }, the segment
  // leading to it from the previous stop (distances along the loop and indices in poly). Stops without a position are skipped.
  routeLine(line) {
    const all = line.stops ?? [];
    const index = [];  // index in stops (after filtering) -> stop index on the line
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
      // when it's impossible on the directional network: the shortest way without direction
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
          // a few points from the curve, so vehicles on long arcs don't cut the corner
          for (const t of BEZIER_SAMPLES) poly.push(bezierPoint(e, forward ? t : 1 - t));
          poly.push(forward ? { x: e.x1, y: e.y1 } : { x: e.x0, y: e.y0 });
        }
      }
      path.lineTo(b.x, b.y);
      legs.set(index[j], { first, last: poly.length }); // last = index of point b (the start of the next one)
    }
    poly.push(stops[0]);

    const cum = new Float64Array(poly.length);
    for (let i = 1; i < poly.length; i++) cum[i] = cum[i - 1] + Math.hypot(poly[i].x - poly[i - 1].x, poly[i].y - poly[i - 1].y);
    for (const [k, { first, last }] of legs) legs.set(k, { first, last, start: cum[first], end: cum[last] });
    return { path, stops, bbox: bboxOf(poly), poly, cum, length: cum[poly.length - 1], legs };
  }

  // A line without vehicles has no known carrier - pick the network type closer to the first stop.
  guessRoad(p) {
    const t = this.nearestNode(p.x, p.y, "TRACK"), s = this.nearestNode(p.x, p.y, "STREET");
    if (!t) return s ? "STREET" : null;
    if (!s) return "TRACK";
    return Math.hypot(t.x - p.x, t.y - p.y) <= Math.hypot(s.x - p.x, s.y - p.y) ? "TRACK" : "STREET";
  }
}

/** A route point (a routeLine result) at distance d along the loop (any d, taken modulo the length). */
export function pointAtDistance(route, d) {
  const { poly, cum, length } = route;
  if (!(length > 0)) return poly[0];
  d = ((d % length) + length) % length;
  let lo = 0, hi = cum.length - 1; // the last point with cum[i] <= d
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
 * Projection of a point (path coordinates) onto the route segment leading to stop: { d, dist } - the distance
 * along the loop and the point's distance from the route; null when the line has no such segment. Only this segment,
 * because a train runs on the same tracks there and back - the whole loop wouldn't settle the direction.
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

/** Whether a route (a routeLine result) crosses the rectangle { minX, minY, maxX, maxY } in path coordinates. */
export function routeInRect(route, r) {
  const b = route?.bbox;
  if (!b || b.maxX < r.minX || b.minX > r.maxX || b.maxY < r.minY || b.minY > r.maxY) return false;
  if (b.minX >= r.minX && b.maxX <= r.maxX && b.minY >= r.minY && b.maxY <= r.maxY) return true;
  const p = route.poly;
  if (p.length === 1) return true; // a single stop: the bbox is a point, already checked
  for (let i = 1; i < p.length; i++) if (segmentInRect(p[i - 1], p[i], r)) return true;
  return false;
}

// A segment crosses a rectangle when their bboxes overlap and the segment's line doesn't leave
// all corners of the rectangle on one side.
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
