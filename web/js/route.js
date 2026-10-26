// A line's route over the network: a graph from the network.json edges, stops snapped to the nearest
// nodes of the right type (track / street) in the same connected piece of the network, segments between
// stops searched with A*.
// Legs the mod read from the vehicles' paths (network.routes, routes.lua) are drawn as the game drives them; the
// rest is an approximation: the shortest way; on tracks with the direction of travel (no U-turns at switches,
// through a stop onwards in the same direction), on streets without it.
// Coordinates as in map.js: (x, -y) from the game world.

const SNAP_DISTANCE = 300; // m - further from the network a stop is connected with a straight line
const SNAP_CANDIDATES = 8; // how many of a stop's nearest nodes we consider when connecting a segment
const STOP_RADIUS = 60; // m - nodes of a directional network this close to the stop belong to the station (platforms)
const STOP_EDGE_MARGIN = 10; // m - a stop this far beyond the edge's half width lies on that edge (a street stop at the curb)
const STOP_EDGE_STEPS = 3; // how many steps at each end of a leg can carry its stop (a path starts a bit before the stop)
// A station stands beside its platform tracks (up to a few tens of m from the stop), and the game's path of a train
// starts a dozen steps before the platform - on a directional network the stop is searched for further.
const STATION_REACH = { steps: 16, margin: STOP_RADIUS };

// Line carrier -> the type of edge it runs on (WATER/AIR: a straight line).
const ROAD_FOR_CARRIER = { RAIL: "TRACK", ROAD: "STREET", TRAM: "STREET" };
// Networks where a vehicle doesn't turn around just anywhere (train: only at a terminal station).
const DIRECTED = new Set(["TRACK"]);

export class RouteGraph {
  constructor(network) {
    const f = Object.fromEntries(network.fields.map((name, i) => [name, i]));
    this.nodes = new Map(); // id -> { x, y, adj: [{ to, edge, len }] }
    this.edges = [];
    this.edgeById = new Map(); // edge id -> edge (for the legs from the game)

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
        width: e[f.width] ?? 0,
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
      this.edgeById.set(e[f.id], edge);

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
  // leading to it from the previous stop (distances along the loop and indices in poly). Stops without a position are skipped;
  // stops = where the line stops (on its lane when known).
  // gameLegs (network.routes): per stop index the leg arriving at it as edges in driving order - [id, x, y] with the
  // middle of the vehicle's lane, or a bare id (older exports: signed) - or false; used only when it matches the line's stops, a leg with an edge missing from the network is computed instead.
  routeLine(line, gameLegs) {
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
    const game = gameLegs?.length === all.length ? gameLegs.map((ids) => this.gameSteps(ids)) : [];
    const allKnown = game.length > 0 && game.every(Boolean);
    const directed = DIRECTED.has(road) && !allKnown ? this.directedLegs(stops, road) : [];
    const reach = DIRECTED.has(road) ? STATION_REACH : undefined;
    const poly = [];

    // each leg as curves; it starts and ends at the stop itself or at the point on the edge the stop stands on
    // (clipLeg) - a leg without its own end point ends where the next one starts, so the loop stays continuous
    const ends = [];
    for (let i = 0; i < stops.length; i++) {
      const j = (i + 1) % stops.length;
      // the leg as the game drives it; otherwise computed, and when it's impossible on the directional network:
      // the shortest way without direction
      const known = game[index[j]];
      if (known) {
        // a path read mid-way (after loading a game the vehicles plan from where they are) misses the start of the leg
        const clip = clipLeg(known, stops[i], stops[j], reach);
        const start = clip.curves[0]?.[0];
        if (clip.from || (start && Math.hypot(start.x - stops[i].x, start.y - stops[i].y) <= 2 * STOP_RADIUS)) {
          ends.push({ na: null, ...clip });
          continue;
        }
      }
      let na = directed[i]?.from, steps = directed[i]?.steps;
      if (!directed[i]) {
        const pair = this.snapPair(candidates[i], candidates[j], road);
        na = pair?.[0];
        steps = pair ? this.path(pair[0], pair[1], road) : null;
      }
      ends.push({ na, ...clipLeg(steps, stops[i], stops[j], reach) });
    }
    // a stop at a node: the leg ends on one edge and the next one starts on another, on its own lane - like the curves
    // within a leg, they're cut where they run past each other and joined (link) instead of leaving a gap
    for (let i = 0; i < stops.length; i++) {
      const e = ends[i], n = ends[(i + 1) % stops.length];
      if (!e.to || !n.from || !e.curves.length || !n.curves.length) continue;
      const pair = [e.curves[e.curves.length - 1], n.curves[0]];
      trimOverlap(pair, 1);
      [e.curves[e.curves.length - 1], n.curves[0]] = pair;
      e.to = pair[0][3];
      n.from = pair[1][0];
      e.link = true;
    }

    for (let i = 0; i < stops.length; i++) {
      const j = (i + 1) % stops.length;
      const { na, curves, from, link } = ends[i];
      const a = from ?? ends[(i + stops.length - 1) % stops.length].to ?? stops[i];
      const b = link ? ends[j].from : ends[i].to ?? ends[j].from ?? stops[j];
      const first = poly.length;
      poly.push(a);
      path.moveTo(a.x, a.y);
      if (curves?.length) {
        if (!from) {
          path.lineTo(curves[0][0].x, curves[0][0].y);
          poly.push(curves[0][0]);
        }
        for (let k = 0; k < curves.length; k++) {
          const c = curves[k];
          // moved onto lanes, curves no longer meet at the node: joined by a turn across the junction
          if (k > 0) joinCurves(path, poly, curves[k - 1], c);
          path.bezierCurveTo(c[1].x, c[1].y, c[2].x, c[2].y, c[3].x, c[3].y);
          // a few points from the curve, so vehicles on long arcs don't cut the corner
          for (const t of BEZIER_SAMPLES) poly.push(curvePoint(c, t));
          poly.push(c[3]);
        }
        if (link) joinCurves(path, poly, curves[curves.length - 1], ends[j].curves[0]);
      } else if (curves && na && !from) {
        path.lineTo(na.x, na.y);
        poly.push(na);
      }
      const end = poly[poly.length - 1];
      if (end.x !== b.x || end.y !== b.y) {
        path.lineTo(b.x, b.y);
        poly.push(b);
      }
      legs.set(index[j], { first, last: poly.length - 1 }); // last = index of point b (the start of the next one)
      if (j !== 0) poly.pop(); // b is the first point of the next leg
    }
    // where the line stops: on the street at the lane of the stop when the leg was cut there, otherwise the stop itself
    const marks = stops.map((s, i) => ends[i].from ?? ends[(i + stops.length - 1) % stops.length].to ?? s);

    const cum = new Float64Array(poly.length);
    for (let i = 1; i < poly.length; i++) cum[i] = cum[i - 1] + Math.hypot(poly[i].x - poly[i - 1].x, poly[i].y - poly[i - 1].y);
    for (const [k, { first, last }] of legs) legs.set(k, { first, last, start: cum[first], end: cum[last] });
    return { path, stops: marks, bbox: bboxOf(poly), poly, cum, length: cum[poly.length - 1], legs };
  }

  // A leg from the game (edge ids in driving order) as [{ edge, forward }] steps; null when unknown or an edge isn't in
  // the network. The direction comes from continuity: an edge starts at the end of the previous one (the first one ends
  // next to the second). The game's own direction flag is per lane, not per edge, so it doesn't tell us.
  gameSteps(ids) {
    if (!ids?.length) return null;
    const edges = [], lanes = [];
    for (const entry of ids) {
      // [id, x, y]: the middle of the lane the vehicle drives on (game world coordinates); a bare id: lane unknown
      const [id, x, y] = Array.isArray(entry) ? entry : [entry];
      const edge = this.edgeById.get(Math.abs(id));
      if (!edge) return null;
      edges.push(edge);
      lanes.push(x != null ? { x, y: -y } : null);
    }
    const d = (ax, ay, bx, by) => Math.hypot(ax - bx, ay - by);
    const near = (x, y, e) => Math.min(d(x, y, e.x0, e.y0), d(x, y, e.x1, e.y1));
    const steps = [];
    edges.forEach((edge, i) => {
      let forward;
      if (i === 0) {
        const next = edges[1];
        forward = !next || near(edge.x1, edge.y1, next) <= near(edge.x0, edge.y0, next);
      } else {
        const p = steps[i - 1], px = p.forward ? p.edge.x1 : p.edge.x0, py = p.forward ? p.edge.y1 : p.edge.y0;
        forward = d(px, py, edge.x0, edge.y0) <= d(px, py, edge.x1, edge.y1);
      }
      steps.push({ edge, forward, lane: lanes[i] });
    });
    return steps;
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

// The steps of a leg as cubic curves [p0, c0, c1, p1] in driving order: { curves, from, to }. A stop standing on an edge
// near the start (end) of the leg cuts the leg there - a street stop is in the middle of an edge, and the game's path
// covers that whole edge, sometimes with the one before it: from (to) is that point on the edge and the steps before
// (after) it are dropped; a station cuts it on its platform track (reach: how many steps and how far from the edge).
// Otherwise from / to are null and the leg is joined to the stop with a straight line.
function clipLeg(steps, a, b, reach = { steps: STOP_EDGE_STEPS, margin: STOP_EDGE_MARGIN }) {
  if (!steps) return { curves: null, from: null, to: null };
  const n = steps.length;
  const onEdge = (p, k0, k1) => {
    let best = null;
    for (let k = Math.max(0, k0); k < Math.min(n, k1); k++) {
      const e = steps[k].edge, c = closestOnEdge(e, p);
      if (c.d <= e.width / 2 + reach.margin && (!best || c.d < best.d)) best = { k, t: c.t, d: c.d };
    }
    return best;
  };
  const sa = onEdge(a, 0, reach.steps);
  const sb = onEdge(b, Math.max(sa?.k ?? 0, n - reach.steps), n);
  const k0 = sa?.k ?? 0, k1 = sb?.k ?? n - 1;
  const curves = [];
  let from = null, to = null;
  for (let k = k0; k <= k1; k++) {
    const { edge: e, forward, lane } = steps[k];
    let t0 = forward ? 0 : 1, t1 = forward ? 1 : 0;
    if (k === k0 && sa) t0 = sa.t;
    if (k === k1 && sb) t1 = sb.t;
    // on the lane the vehicle drives on: the offset is to the right of the edge, the curve goes in driving order
    const off = laneOffset(e, lane);
    const c = offsetCurve(subCurve(e, t0, t1), t0 <= t1 ? off : -off);
    if (k === k0 && sa) from = c[0];
    if (k === k1 && sb) to = c[3];
    if (t0 !== t1) curves.push(c);
  }
  for (let k = 1; k < curves.length; k++) {
    trimOverlap(curves, k);
    easeJoin(curves, k);
  }
  return { curves, from, to };
}

// Curves k - 1 and k meet at a junction, where a vehicle crossing it changes lanes (or turns): it does so over the
// whole junction, not at its centre. Both curves are pulled back from the junction (by ~a junction's half: JOIN_EASE_M,
// more for a bigger jump, never more than JOIN_EASE_SHARE of a curve), so joinCurves spreads the change. Also the inside
// of a turn, where trimOverlap left the curves meeting in a sharp corner. Not for continuous edges (no jump, no bend)
// and U-turns.
function easeJoin(curves, k) {
  const prev = curves[k - 1], next = curves[k];
  const p = prev[3], q = next[0];
  const gap = Math.hypot(q.x - p.x, q.y - p.y);
  const dp = curveTangent(prev, 1), dq = curveTangent(next, 0);
  const lp = Math.hypot(dp.x, dp.y), lq = Math.hypot(dq.x, dq.y);
  if (!(lp > 0) || !(lq > 0)) return;
  const cos = (dp.x * dq.x + dp.y * dq.y) / (lp * lq);
  if (cos < -0.5 || (gap < 0.3 && cos > 0.995)) return;
  const d = Math.max(JOIN_EASE_M, 1.5 * gap);
  const tp = curveAtLength(prev, Math.min(d, JOIN_EASE_SHARE * curveLength(prev)), true);
  const tq = curveAtLength(next, Math.min(d, JOIN_EASE_SHARE * curveLength(next)), false);
  if (tp < 1) curves[k - 1] = splitCurve(prev, tp)[0];
  if (tq > 0) curves[k] = splitCurve(next, tq)[1];
}

const JOIN_EASE_M = 8;
const JOIN_EASE_SHARE = 0.4;

function curveLength(c, n = 16) {
  let len = 0, a = c[0];
  for (let i = 1; i <= n; i++) {
    const b = curvePoint(c, i / n);
    len += Math.hypot(b.x - a.x, b.y - a.y);
    a = b;
  }
  return len;
}

// The parameter d m along curve c from its start (fromEnd: from its end), sampled.
function curveAtLength(c, d, fromEnd) {
  const n = 32;
  let len = 0, a = curvePoint(c, fromEnd ? 1 : 0);
  for (let i = 1; i <= n; i++) {
    const t = fromEnd ? 1 - i / n : i / n, b = curvePoint(c, t);
    const step = Math.hypot(b.x - a.x, b.y - a.y);
    if (len + step >= d) {
      const f = step > 0 ? (d - len) / step : 0, t0 = fromEnd ? 1 - (i - 1) / n : (i - 1) / n;
      return t0 + (t - t0) * f;
    }
    len += step;
    a = b;
  }
  return fromEnd ? 0 : 1;
}

// Curves k - 1 and k moved onto lanes: on the inside of a turn they run past each other at the junction (one lane ends
// beyond the other street's lane). The one that runs past where their directions cross is cut there, so joinCurves
// turns at that point instead of looping back.
function trimOverlap(curves, k) {
  const prev = curves[k - 1], next = curves[k];
  const p = prev[3], q = next[0];
  const dp = curveTangent(prev, 1), dq = curveTangent(next, 0);
  const lp = Math.hypot(dp.x, dp.y), lq = Math.hypot(dq.x, dq.y);
  if (!(lp > 0) || !(lq > 0)) return;
  const ux = dp.x / lp, uy = dp.y / lp, vx = dq.x / lq, vy = dq.y / lq;
  const cross = ux * vy - uy * vx;
  if (Math.abs(cross) < 0.1) return; // (almost) parallel: a lane change or a U-turn
  const wx = q.x - p.x, wy = q.y - p.y;
  // the crossing X = p + s * u = q - r * v; s < 0: prev runs past X, r < 0: next starts past X
  const s = (wx * vy - wy * vx) / cross, r = (ux * wy - uy * wx) / cross;
  const x = { x: p.x + s * ux, y: p.y + s * uy };
  const reach = 2 * Math.hypot(wx, wy) + 10; // only a junction's worth, not a crossing far away
  if (s < 0 && -s < reach) {
    const t = closestOnCurve(prev, x);
    if (t > 0) curves[k - 1] = splitCurve(prev, t)[0];
  }
  if (r < 0 && -r < reach) {
    const t = closestOnCurve(next, x);
    if (t < 1) curves[k] = splitCurve(next, t)[1];
  }
}

// The parameter of the point of curve c nearest to p.
function closestOnCurve(c, p) {
  const N = 32;
  const dist = (t) => { const q = curvePoint(c, t); return Math.hypot(q.x - p.x, q.y - p.y); };
  let best = 0, bestD = dist(0);
  for (let i = 1; i <= N; i++) {
    const d = dist(i / N);
    if (d < bestD) { best = i / N; bestD = d; }
  }
  for (let h = 0.5 / N; h > 1e-4; h /= 2) {
    for (const t of [best - h, best + h]) {
      if (t < 0 || t > 1) continue;
      const d = dist(t);
      if (d < bestD) { best = t; bestD = d; }
    }
  }
  return best;
}

// How far (m) the lane through point lane lies to the right of edge e's centre line (looking from node0 to node1);
// 0 without a lane, or when the point isn't on the street (it belongs to another edge).
function laneOffset(e, lane) {
  if (!lane) return 0;
  const { t, d } = closestOnEdge(e, lane);
  if (d > e.width / 2 + 1) return 0;
  const p = edgePoint(e, t), tan = curveTangent(edgeCurve(e), t), len = Math.hypot(tan.x, tan.y);
  if (!(len > 0)) return 0;
  // right of the direction in map coordinates (y down): the normal (-ty, tx)
  return ((lane.x - p.x) * -tan.y + (lane.y - p.y) * tan.x) / len;
}

// Cubic curve c moved off sideways by off m (to the right of its direction): the ends along their normals, each
// control point with its end - close enough for street curves.
function offsetCurve(c, off) {
  if (!off) return c;
  const normal = (a, b, fa, fb) => {
    let dx = b.x - a.x, dy = b.y - a.y;
    if (Math.hypot(dx, dy) < 1e-6) { dx = fb.x - fa.x; dy = fb.y - fa.y; }
    const len = Math.hypot(dx, dy) || 1;
    return { x: -dy / len * off, y: dx / len * off };
  };
  const n0 = normal(c[0], c[1], c[0], c[3]), n1 = normal(c[2], c[3], c[0], c[3]);
  const move = (p, nv) => ({ x: p.x + nv.x, y: p.y + nv.y });
  return [move(c[0], n0), move(c[1], n0), move(c[2], n1), move(c[3], n1)];
}

// Joins the end of curve prev to the start of curve next (both on lanes, a junction or a lane change apart): a quadratic
// turn through where their directions meet, otherwise (parallel lanes, a sharp turn, a U-turn) a cubic leaving and
// entering along the lanes. Adds the points to poly.
function joinCurves(path, poly, prev, next) {
  const p = prev[3], q = next[0];
  const wx = q.x - p.x, wy = q.y - p.y, gap = Math.hypot(wx, wy);
  if (gap < 0.05) return;
  const unit = (a, b, fa, fb) => {
    let dx = b.x - a.x, dy = b.y - a.y;
    if (Math.hypot(dx, dy) < 1e-6) { dx = fb.x - fa.x; dy = fb.y - fa.y; }
    const len = Math.hypot(dx, dy) || 1;
    return { x: dx / len, y: dy / len };
  };
  const dp = unit(prev[2], prev[3], prev[0], prev[3]), dq = unit(next[0], next[1], next[0], next[3]);
  const cross = dp.x * dq.y - dp.y * dq.x;
  // p + s * dp = q - u * dq
  const s = (wx * dq.y - wy * dq.x) / cross, u = (dp.x * wy - dp.y * wx) / cross;
  if (Math.abs(cross) > 1e-3 && s > 0 && u > 0 && s <= 2 * gap + 5 && u <= 2 * gap + 5) {
    const cx = p.x + s * dp.x, cy = p.y + s * dp.y;
    path.quadraticCurveTo(cx, cy, q.x, q.y);
    poly.push({ x: (p.x + 2 * cx + q.x) / 4, y: (p.y + 2 * cy + q.y) / 4 });
  } else {
    const k = gap / 2;
    const c = [p, { x: p.x + dp.x * k, y: p.y + dp.y * k }, { x: q.x - dq.x * k, y: q.y - dq.y * k }, q];
    path.bezierCurveTo(c[1].x, c[1].y, c[2].x, c[2].y, q.x, q.y);
    for (const t of BEZIER_SAMPLES) poly.push(curvePoint(c, t));
  }
  poly.push(q);
}

function curveTangent([p0, p1, p2, p3], t) {
  const u = 1 - t, a = 3 * u * u, b = 6 * u * t, c = 3 * t * t;
  return { x: a * (p1.x - p0.x) + b * (p2.x - p1.x) + c * (p3.x - p2.x), y: a * (p1.y - p0.y) + b * (p2.y - p1.y) + c * (p3.y - p2.y) };
}

// The parameter t of the point of edge e nearest to p, and the distance: { t, d }.
function closestOnEdge(e, p) {
  const N = 32;
  const at = (t) => { const q = edgePoint(e, t); return { t, d: Math.hypot(q.x - p.x, q.y - p.y) }; };
  let best = at(0);
  for (let i = 1; i <= N; i++) {
    const c = at(i / N);
    if (c.d < best.d) best = c;
  }
  for (let h = 0.5 / N; h > 1e-4; h /= 2) { // refine around the best sample
    for (const t of [best.t - h, best.t + h]) {
      if (t < 0 || t > 1) continue;
      const c = at(t);
      if (c.d < best.d) best = c;
    }
  }
  return best;
}

const edgeCurve = (e) => [{ x: e.x0, y: e.y0 }, { x: e.c0x, y: e.c0y }, { x: e.c1x, y: e.c1y }, { x: e.x1, y: e.y1 }];
const edgePoint = (e, t) => curvePoint(edgeCurve(e), t);

// The part of edge e from t0 to t1 (t0 > t1: against the edge) as a cubic curve.
function subCurve(e, t0, t1) {
  const lo = Math.min(t0, t1), hi = Math.max(t0, t1);
  let c = edgeCurve(e);
  if (lo > 0) c = splitCurve(c, lo)[1];
  if (hi < 1) c = splitCurve(c, (hi - lo) / (1 - lo))[0];
  return t0 <= t1 ? c : c.reverse();
}

// de Casteljau: a cubic curve split at t into [left, right]
function splitCurve([p0, p1, p2, p3], t) {
  const lerp = (a, b) => ({ x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t });
  const a = lerp(p0, p1), b = lerp(p1, p2), c = lerp(p2, p3), d = lerp(a, b), e = lerp(b, c), m = lerp(d, e);
  return [[p0, a, d, m], [m, e, c, p3]];
}

function curvePoint([p0, p1, p2, p3], t) {
  const u = 1 - t, a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
  return { x: a * p0.x + b * p1.x + c * p2.x + d * p3.x, y: a * p0.y + b * p1.y + c * p2.y + d * p3.y };
}

/**
 * The route polyline simplified (Douglas-Peucker) so no point is more than tol from it: { path (Path2D of straight
 * segments), poly }. For drawing many lines at once - a fraction of the points and no curves to flatten.
 */
export function simplifyRoute(route, tol) {
  const p = route.poly, n = p.length;
  if (n < 3) return { path: route.path, poly: p };
  const keep = new Uint8Array(n);
  keep[0] = keep[n - 1] = 1;
  const tol2 = tol * tol;
  const stack = [0, n - 1];
  while (stack.length) {
    const b = stack.pop(), a = stack.pop();
    const ax = p[a].x, ay = p[a].y, dx = p[b].x - ax, dy = p[b].y - ay, len2 = dx * dx + dy * dy;
    let worst = -1, worstD = tol2;
    for (let i = a + 1; i < b; i++) {
      let ex = p[i].x - ax, ey = p[i].y - ay;
      if (len2 > 0) {
        const t = Math.max(0, Math.min(1, (ex * dx + ey * dy) / len2));
        ex -= t * dx;
        ey -= t * dy;
      }
      const d = ex * ex + ey * ey;
      if (d > worstD) { worstD = d; worst = i; }
    }
    if (worst < 0) continue;
    keep[worst] = 1;
    stack.push(a, worst, worst, b);
  }
  const path = new Path2D(), poly = [];
  for (let i = 0; i < n; i++) {
    if (!keep[i]) continue;
    if (poly.length) path.lineTo(p[i].x, p[i].y);
    else path.moveTo(p[i].x, p[i].y);
    poly.push(p[i]);
  }
  return { path, poly };
}

/**
 * Whether a route (a routeLine result) crosses the rectangle { minX, minY, maxX, maxY } in path coordinates.
 * poly: the route's polyline or a simplified one (simplifyRoute) - fewer segments to check.
 */
export function routeInRect(route, r, poly = route?.poly) {
  const b = route?.bbox;
  if (!b || b.maxX < r.minX || b.minX > r.maxX || b.maxY < r.minY || b.minY > r.maxY) return false;
  if (b.minX >= r.minX && b.maxX <= r.maxX && b.minY >= r.minY && b.maxY <= r.maxY) return true;
  const p = poly;
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
