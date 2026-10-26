// Map: the network (Path2D in world coordinates), towns, industries and warehouses (in screen coordinates), pan/zoom, hover, select.
// "Path" coordinates = (x, -y) from the game world, because in the game the y axis grows to the north, and on screen downwards.

import { cargoImg, fmtInt, industryOfStation, rgb, stationImg, townSize, warehouseOfStation } from "./format.js";
import { RouteGraph, pointAtDistance, projectOnLeg, routeInRect } from "./route.js";
import { t } from "./i18n.js";
import { frameStart, perfInfo, timed } from "./perf.js";

const RAW_INDUSTRIES = new Set([
  "coal_mine", "iron_ore_mine", "quarry", "forest", "farm", "oil_well",
  "oil_platform", "fishing_grounds", "sand_excavator", "sand_pit", "clay_pit",
]);

export const industryKind = (ind) => {
  const key = String(ind.file ?? "").split("/").pop().replace(/\.con$/, "");
  return RAW_INDUSTRIES.has(key) ? "raw" : "processing";
};

/** Cargo -> the amount produced by the industry (rules without boosters). */
export function industryOutputs(ind) {
  const outputs = new Map();
  for (const rule of ind.rules ?? []) {
    if (rule.booster) continue;
    for (const [cargo, amount] of Object.entries(rule.output ?? {}))
      outputs.set(cargo, (outputs.get(cargo) ?? 0) + amount);
  }
  return outputs;
}

// An older export has no input flag: the input is the stock of a cargo the industry doesn't produce.
export const isInputStock = (s, produced) => s.input ?? !produced.has(s.cargo);

/** Cargo the industry needs (its input stocks, boosters included). */
export function industryInputs(ind) {
  const produced = new Set(industryOutputs(ind).keys());
  return new Set((ind.stocks ?? []).filter((s) => isInputStock(s, produced)).map((s) => s.cargo));
}

/** Cargo the town asks for (cargoNeeds of its zones, supply with demand). */
function townNeeds(town) {
  const needs = new Set((town.cargoNeeds ?? []).flatMap((group) => Object.keys(group ?? {})));
  for (const s of town.supply ?? []) if (s.demand > 0) needs.add(s.cargo);
  return needs;
}

const COLOR_VARS = ["--map-bg", "--map-street", "--map-street-tunnel", "--map-track", "--map-track-tunnel",
  "--industry-raw", "--industry-processing", "--warehouse", "--town", "--town-label", "--accent", "--font", "--panel",
  "--station-rail", "--station-road", "--station-tram", "--station-water", "--station-air", "--pin",
  "--link-supply", "--link-consume", "--depot"];

// Station icons: from this scale a circle with an icon, below it just a dot in the kind's color.
const STATION_ICON_SCALE = 0.05;
const STATION_ICON_PX = 24;
// Street stops: visible from STATION_ICON_SCALE, the number waiting from STOP_LABEL_SCALE.
const STOP_R = 4;
const STOP_LABEL_SCALE = 0.15;
const WAITING_ICON_PX = 16; // the passenger icon next to the number waiting
const OUTPUT_ICON_PX = 30; // production icons above the industry
const PIN_HEAD_Y = 22;      // the pin head is this many px above the chosen point
const DIM = 0.3;            // opacity of the rest of the map when one object is pointed at in the panel list
const FOOTPRINT_FILL = 0.18, FOOTPRINT_LINE = 0.6; // opacity of the station / warehouse / depot outlines
const STREET_MIN_PX_PER_M = 1 / 8; // zoomed out: a 12 m street 1.5 px, a 24 m avenue 3 px
const MIN_SCALE = 0.005, MAX_SCALE = 20;
// Vehicle movement between data batches: frames every ANIM_FRAME_MS (30 fps is enough, and the whole map
// is redrawn); a longer gap in the data (a background tab, the game paused) - no animation, a jump.
const ANIM_FRAME_MS = 33;
const UPDATE_MS_MIN = 2000, UPDATE_MS_MAX = 30000, SNAP_AFTER_MS = 60000;
const ON_ROUTE_M = 40; // a vehicle position this close to the line route = it drives along it (animation along the route)
// Connections of the selected industry: arrows every LINK_ARROW_GAP px along the curve, moving LINK_SPEED px/s
// towards the receiver; the curve bends sideways by LINK_BEND of its length, so A->B and B->A don't overlap.
// Cargo icons in the middle of the curves only up to LINK_ICONS_MAX connections (more would be a cloud of the same icons).
const LINK_ARROW_GAP = 70, LINK_SPEED = 45, LINK_BEND = 0.18, LINK_ICON_PX = 18, LINK_ICONS_MAX = 15;

// Colors from CSS (the theme) read once per frame, not for every object.
// Street stops: line stops (dynamic.lines[].stops) that don't belong to any station
// from dynamic.stations - one point per station group, with the lines that stop at it.
function stopsOf(dynamic) {
  const stationEntities = new Set((dynamic.stations ?? []).flatMap((s) => s.stations ?? []));
  const byGroup = new Map();
  for (const l of dynamic.lines ?? []) {
    for (const s of l.stops ?? []) {
      if (!s.pos || stationEntities.has(s.entity)) continue;
      let st = byGroup.get(s.group);
      if (!st) {
        st = { kind: "stop", id: s.group, name: s.name, px: s.pos[0], py: -s.pos[1], type: l.carrier ?? "ROAD", waiting: s.waiting, lines: [] };
        byGroup.set(s.group, st);
      }
      if (s.waiting != null) st.waiting = s.waiting;
      if (!st.lines.includes(l)) st.lines.push(l);
    }
  }
  return [...byGroup.values()];
}

// Areas of stations, warehouses and depots (network.footprints: [constructionId, type, polygons], a polygon =
// a ground lot [x1, y1, x2, y2, ...] in world meters), one Path2D per color, in path coordinates. Type = the station
// kind (RAIL, ROAD...), WAREHOUSE or DEPOT; the mod leaves out stations of industries and warehouses.
function footprintsOf(network) {
  const out = new Map(); // color var -> { path, layer }
  for (const [, type, polys] of network.footprints ?? []) {
    const color = type === "WAREHOUSE" ? "--warehouse" : type === "DEPOT" ? "--depot" : `--station-${String(type).toLowerCase()}`;
    let f = out.get(color);
    if (!f) out.set(color, f = { path: new Path2D(), layer: type === "WAREHOUSE" ? "warehouses" : "stations" });
    for (const pts of polys ?? []) {
      const n = pts.length >> 1;
      if (n < 3) continue;
      // one winding for all lots, so overlapping ones don't cancel out under the nonzero fill rule
      let area = 0;
      for (let i = 0, j = n - 1; i < n; j = i++) area += pts[2 * j] * pts[2 * i + 1] - pts[2 * i] * pts[2 * j + 1];
      const at = (k) => (area < 0 ? n - 1 - k : k);
      f.path.moveTo(pts[2 * at(0)], -pts[2 * at(0) + 1]);
      for (let k = 1; k < n; k++) f.path.lineTo(pts[2 * at(k)], -pts[2 * at(k) + 1]);
      f.path.closePath();
    }
  }
  return [...out.entries()].map(([color, f]) => ({ color, ...f }));
}

function readTheme() {
  const style = getComputedStyle(document.documentElement);
  return Object.fromEntries(COLOR_VARS.map((v) => [v, style.getPropertyValue(v).trim()]));
}

export class MapView {
  constructor(canvas, { onHover, onSelect, onPlace, onPlaceMode, onVisibleLines, onLayers, focusAnchorY } = {}) {
    this.canvas = canvas;
    this.ctx = canvas.getContext("2d");
    this.onHover = onHover;
    this.onSelect = onSelect;
    this.onPlace = onPlace;     // a click in pick mode / right-click -> { x, y, sx, sy, mode }
    this.onPlaceMode = onPlaceMode;
    this.onVisibleLines = onVisibleLines; // Set of line ids on screen (only with linesView on)
    this.onLayers = onLayers;   // a layer (stations, industries) turned on by focus() itself - sync the buttons
    // The height (a fraction of the map) where focus() puts the object; the middle by default. Higher on a phone,
    // because the bottom of the map is covered by the detail window.
    this.focusAnchorY = focusAnchorY ?? (() => 0.5);
    this.pin = null;            // { x, y, note } - the pin from a link or one being placed
    this.placeMode = null;      // "pin" | "build": the next click picks a spot
    this.marker = null;         // { x, y, label, radius, ok } - the building spot (js/build.js)

    this.view = { x: 0, y: 0, scale: 0.02 };
    this.paths = null;          // { streets (width -> Path2D), streetTunnel, track, trackTunnel }
    this.bbox = null;           // network extent in path coordinates
    this.towns = [];
    this.industries = [];
    this.showIndustries = true;
    this.warehouses = [];       // the player's warehouses (dynamic.warehouses)
    this.showWarehouses = true;
    this.stations = [];         // train stations, harbors, airports (no stops)
    this.stops = [];            // street stops from the line stops (stopsOf)
    this.footprints = [];       // [{ color, path, layer }] - areas of stations, warehouses, depots (footprintsOf, network)
    this.showStations = true;
    this.vehicleData = [];      // dynamic.vehicles
    this.vehicleAnim = new Map(); // vehicle id -> movement along the line route (retargetVehicles)
    this.vehiclesDirty = false; // new data or routes - work out the movement targets on the next frame
    this.vehicles = [];         // vehicles with their position in the last frame (drawing, hitTest)
    this.updateMs = 10000;      // the average interval between dynamic.json batches = travel time to the new position
    this.lastDynamicAt = 0;
    this.animUntil = 0;         // until when the vehicles move (performance.now)
    this.animTimer = 0;
    this.showVehicles = true;
    this.iconImages = new Map(); // url -> Image (game icons: station masks, cargo)
    this.iconCache = new Map();  // url|color|px -> canvas with the tinted icon
    this.lines = new Map();     // id -> line from dynamic.json
    this.graph = null;          // RouteGraph - for working out line routes
    this.routeCache = new Map(); // line id -> { key, route }
    this.gameRoutes = new Map(); // line id -> legs read from the vehicles (network.routes, see route.js routeLine)
    this.linesView = null;      // { match(line) } - the Lines tab: draw all lines on screen
    this.visibleKey = null;     // the last reported set of lines on screen (so it isn't reported every frame)
    this.visibleIds = new Set(); // lines drawn by drawVisibleLines in the last frame
    this.listHover = null;      // { kind, id } of the object hovered in the panel list
    this.terrain = null;        // { image, x, y, w, h } - the heightmap background, a rectangle in path coordinates
    this.showTerrain = true;
    this.overlay = null;        // a canvas on the terrain rectangle: places where a town won't get built (js/build.js)
    this.selected = null;       // { kind, id }
    this.pinned = [];           // [{ kind, id }] - objects with pinned windows: highlighted like the selected one
    this.focusedStop = null;    // { line, x, y } of the line stop chosen in the panel
    this.hovered = null;
    this.fitted = false;
    this.frame = 0;
    this.cargoHover = null;     // { key, kind, id, cargo, dir } - the cargo pointed at in the details (hoverCargo)
    this.cargoView = null;      // the view from before hoverCargo zoomed out - restored when the pointer leaves
    this.linksCache = null;     // { key, industries, towns, links } - industryLinks() for the cargo / selected industry

    this.resizeObserver = new ResizeObserver(() => this.resize());
    this.resizeObserver.observe(canvas);
    this.bindEvents();
    this.resize();
  }

  // ---------------------------------------------------------------- data
  setNetwork(network) {
    const f = Object.fromEntries(network.fields.map((name, i) => [name, i]));
    // streets: width in m (rounded) -> Path2D; 0 = an older export without widths
    const paths = { streets: new Map(), streetTunnel: new Path2D(), track: new Path2D(), trackTunnel: new Path2D() };
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;

    for (const e of network.edges) {
      const x0 = e[f.x0], y0 = -e[f.y0], x1 = e[f.x1], y1 = -e[f.y1];
      // A TF edge is a Hermite curve (positions + tangents) -> Bezier: c = p ± t/3
      const c0x = x0 + e[f.tx0] / 3, c0y = y0 - e[f.ty0] / 3;
      const c1x = x1 - e[f.tx1] / 3, c1y = y1 + e[f.ty1] / 3;
      const tunnel = e[f.type] === "TUNNEL";
      let p;
      if (e[f.road] === "TRACK") p = tunnel ? paths.trackTunnel : paths.track;
      else if (tunnel) p = paths.streetTunnel;
      else {
        const width = Math.round(e[f.width] ?? 0);
        p = paths.streets.get(width);
        if (!p) paths.streets.set(width, p = new Path2D());
      }
      p.moveTo(x0, y0);
      p.bezierCurveTo(c0x, c0y, c1x, c1y, x1, y1);

      if (x0 < minX) minX = x0; if (x0 > maxX) maxX = x0;
      if (y0 < minY) minY = y0; if (y0 > maxY) maxY = y0;
      if (x1 < minX) minX = x1; if (x1 > maxX) maxX = x1;
      if (y1 < minY) minY = y1; if (y1 > maxY) maxY = y1;
    }

    this.paths = paths;
    this.footprints = footprintsOf(network);
    this.gameRoutes = new Map((network.routes ?? []).map(([id, legs]) => [id, legs]));
    this.graph = new RouteGraph(network);
    this.routeCache.clear();
    this.vehiclesDirty = true; // new routes - vehicles jump onto them without animation
    if (network.edges.length) this.bbox = { minX, minY, maxX, maxY };
    if (!this.fitted) this.fit();
    this.requestDraw();
  }

  setDynamic(dynamic) {
    this.towns = (dynamic.towns ?? []).filter((t) => t.pos).map((t) => ({
      kind: "town", id: t.id, data: t, px: t.pos[0], py: -t.pos[1], needs: townNeeds(t),
      r: Math.max(4, Math.min(14, 2 + Math.sqrt(townSize(t)) * 0.4)),
    })).sort((a, b) => b.r - a.r);

    this.lines = new Map((dynamic.lines ?? []).map((l) => [l.id, l]));
    this.vehicleData = dynamic.vehicles ?? [];
    this.vehiclesDirty = true;
    const now = performance.now();
    const gap = now - this.lastDynamicAt;
    if (!this.lastDynamicAt || gap > SNAP_AFTER_MS) this.vehicleAnim.clear();
    else this.updateMs = Math.max(UPDATE_MS_MIN, Math.min(UPDATE_MS_MAX, (this.updateMs + gap) / 2));
    this.lastDynamicAt = now;

    this.industries = (dynamic.industries ?? []).filter((i) => i.pos).map((i) => ({
      kind: "industry", id: i.id, data: i, px: i.pos[0], py: -i.pos[1], type: industryKind(i),
      produces: new Set(industryOutputs(i).keys()), needs: industryInputs(i),
      outputs: [...industryOutputs(i).keys()].map(cargoImg).filter(Boolean),
    }));

    this.warehouses = (dynamic.warehouses ?? []).filter((w) => w.pos).map((w) => ({
      kind: "warehouse", id: w.id, data: w, px: w.pos[0], py: -w.pos[1],
    }));

    // A station built into an industry or warehouse: its icon is already on the map, so the station isn't drawn.
    const stationIndustry = industryOfStation(dynamic);
    const stationWarehouse = warehouseOfStation(dynamic);
    this.stations = (dynamic.stations ?? []).filter((s) => s.pos && !stationIndustry.has(s.id) && !stationWarehouse.has(s.id)).map((s) => ({
      kind: "station", id: s.id, data: s, px: s.pos[0], py: -s.pos[1], type: s.kind,
    }));
    this.stops = stopsOf(dynamic);

    if (!this.bbox && this.towns.length) {
      const xs = this.towns.map((t) => t.px), ys = this.towns.map((t) => t.py);
      this.bbox = { minX: Math.min(...xs), minY: Math.min(...ys), maxX: Math.max(...xs), maxY: Math.max(...ys) };
      if (!this.fitted) this.fit();
    }
    this.requestDraw();
  }

  // The terrain image from Tf3Bridge (terrain.png + terrain.json with the extent in game world coordinates; north at the top).
  setTerrain({ info, image }) {
    this.terrain = { image, x: info.minX, y: -info.maxY, w: info.maxX - info.minX, h: info.maxY - info.minY };
    if (!this.bbox) {
      this.bbox = { minX: info.minX, minY: -info.maxY, maxX: info.maxX, maxY: -info.minY };
      if (!this.fitted) this.fit();
    }
    this.requestDraw();
  }

  setOverlay(canvas) {
    if (this.overlay === canvas) return;
    this.overlay = canvas;
    this.requestDraw();
  }

  toggleTerrain() {
    this.showTerrain = !this.showTerrain;
    this.requestDraw();
    return this.showTerrain;
  }

  toggleStations() {
    this.showStations = !this.showStations;
    this.requestDraw();
    return this.showStations;
  }

  toggleIndustries() {
    this.showIndustries = !this.showIndustries;
    this.requestDraw();
    return this.showIndustries;
  }

  toggleWarehouses() {
    this.showWarehouses = !this.showWarehouses;
    this.requestDraw();
    return this.showWarehouses;
  }

  toggleVehicles() {
    this.showVehicles = !this.showVehicles;
    this.requestDraw();
    return this.showVehicles;
  }

  // The object hovered in the list (kind = null - none): highlighted on the map, the rest dimmed.
  // A line - only among the lines on screen (linesView); a town, industry, station - when it's on screen.
  hoverItem(kind, id) {
    if (kind === (this.listHover?.kind ?? null) && id === this.listHover?.id) return;
    this.listHover = kind ? { kind, id } : null;
    this.requestDraw();
  }

  // The Lines tab with the "eye" on: { match(line) -> bool } (panel filters), or null - off.
  setLinesView(view) {
    this.linesView = view;
    if (!view) this.visibleIds = new Set();
    if (!view && this.listHover?.kind === "line") this.listHover = null;
    this.visibleKey = null; // after the filters change, report the set of lines again
    this.requestDraw();
  }

  // ---------------------------------------------------------------- view
  resize() {
    const dpr = window.devicePixelRatio || 1;
    const { width, height } = this.canvas.getBoundingClientRect();
    this.w = width;
    this.h = height;
    this.dpr = dpr;
    const cw = Math.max(1, Math.round(width * dpr));
    const ch = Math.max(1, Math.round(height * dpr));
    if (this.canvas.width === cw && this.canvas.height === ch) return this.requestDraw();
    this.canvas.width = cw;
    this.canvas.height = ch;
    // Data may have arrived before the canvas got its size - fit the view once it has one.
    if (!this.fitted) this.fit();
    // A resize clears the canvas, and the ResizeObserver runs after the rAF frames -
    // draw right away, otherwise the browser shows an empty frame (flicker while dragging the panel).
    if (this.frame) cancelAnimationFrame(this.frame);
    this.frame = 0;
    this.draw();
  }

  fit(bbox = this.bbox, maxScale = Infinity) {
    if (!bbox || !(this.w > 0) || !(this.h > 0)) return;
    const { minX, minY, maxX, maxY } = bbox;
    this.view.x = (minX + maxX) / 2;
    this.view.y = (minY + maxY) / 2;
    this.view.scale = Math.min(maxScale, this.w / (maxX - minX || 1) * 0.85, this.h / (maxY - minY || 1) * 0.85);
    if (bbox === this.bbox) this.fitted = true;
    this.requestDraw();
  }

  focus(kind, id) {
    this.focusedStop = null;
    if (kind === "line") {
      this.selected = { kind, id };
      const route = this.lineRoute(id);
      if (route?.bbox) this.fit(route.bbox, 1);
      this.requestDraw();
      return;
    }
    const items = { town: this.towns, industry: this.industries, warehouse: this.warehouses, station: this.stations }[kind] ?? [];
    const item = items.find((x) => x.id === id);
    if (!item) return;
    if (kind === "station" && !this.showStations) {
      this.showStations = true;
      this.onLayers?.();
    }
    if (kind === "industry" && !this.showIndustries) {
      this.showIndustries = true;
      this.onLayers?.();
    }
    if (kind === "warehouse" && !this.showWarehouses) {
      this.showWarehouses = true;
      this.onLayers?.();
    }
    this.selected = { kind, id };
    if (kind === "industry" && this.fitLinks()) return;
    this.view.scale = Math.max(this.view.scale, 0.25);
    this.centerOn(item.px, item.py);
  }

  // The world point at the horizontal center of the map and at the focusAnchorY height vertically.
  centerOn(x, y) {
    this.view.x = x;
    this.view.y = y + (0.5 - this.focusAnchorY()) * this.h / this.view.scale;
    this.requestDraw();
  }

  // A stop of the selected or pinned line: we center on it and outline it until the selection changes.
  focusStop(lineId, index) {
    const pos = this.lines.get(lineId)?.stops?.[index]?.pos;
    if (!pos) return;
    this.focusedStop = { line: lineId, x: pos[0], y: -pos[1] };
    this.view.scale = Math.max(this.view.scale, 0.5);
    this.centerOn(this.focusedStop.x, this.focusedStop.y);
  }

  select(kind, id) {
    this.focusedStop = null;
    this.selected = kind ? { kind, id } : null;
    this.requestDraw();
  }

  setPinned(pinned) {
    this.pinned = pinned;
    if (this.focusedStop && !this.selectedLines().has(this.focusedStop.line)) this.focusedStop = null;
    this.requestDraw();
  }

  // Lines drawn as selected: pinned ones and the selected one (as long as they're in the data).
  selectedLines() {
    const ids = new Set();
    for (const s of [...this.pinned, this.selected]) if (s?.kind === "line" && this.lines.has(s.id)) ids.add(s.id);
    return ids;
  }

  toScreen(px, py) {
    return [(px - this.view.x) * this.view.scale + this.w / 2, (py - this.view.y) * this.view.scale + this.h / 2];
  }

  toWorld(sx, sy) {
    return [(sx - this.w / 2) / this.view.scale + this.view.x, (sy - this.h / 2) / this.view.scale + this.view.y];
  }

  // ---------------------------------------------------------------- drawing
  // Something on the map changed (view, data, layers, selection, hover): the static layer is drawn again.
  requestDraw() {
    this.staticDirty = true;
    this.requestFrame();
  }

  // A frame of the vehicle / connection animation: the map under them is taken from the static layer.
  requestFrame() {
    if (this.frame) return;
    this.frame = requestAnimationFrame(() => {
      this.frame = 0;
      this.draw();
    });
  }

  // The map is drawn in two parts: the static layer (terrain, footprints, network, lines - thousands of curves, the
  // costly part) to an offscreen canvas, only when something changed; and on top of it, every frame, the rest.
  draw() {
    frameStart();
    timed("frame", () => this.drawFrame());
  }

  drawFrame() {
    const { ctx, dpr } = this;
    const theme = timed("frame/theme", readTheme);
    const themeKey = Object.values(theme).join("|"); // a theme switch doesn't go through requestDraw
    this.theme = theme;
    const now = performance.now();
    // The connections of the cargo pointed at / selected industry replace the lines and vehicles (they move on, just not drawn).
    const links = timed("frame/links", () => this.industryLinks());
    const vehicles = this.showVehicles ? timed("frame/vehicle positions", () => this.positionVehicles(now)) : [];
    this.vehicles = links ? [] : vehicles;
    const focus = links ? null : timed("frame/focus", () => this.objectFocus());

    const layer = (this.layer ??= document.createElement("canvas"));
    if (layer.width !== this.canvas.width || layer.height !== this.canvas.height) {
      layer.width = this.canvas.width;
      layer.height = this.canvas.height;
      this.staticDirty = true;
    }
    if (this.staticDirty || themeKey !== this.themeKey) {
      this.staticDirty = false;
      this.themeKey = themeKey;
      this.ctx = layer.getContext("2d");
      try {
        timed("frame/static", () => this.drawStatic(focus));
      } finally {
        this.ctx = ctx;
      }
    }
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.globalAlpha = 1;
    timed("frame/static copy", () => ctx.drawImage(layer, 0, 0));

    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    if (links) timed("frame/links view", () => this.drawLinksView(links, now));
    else timed("frame/objects", () => this.drawObjects(focus));
    timed("frame/marker, pin", () => {
      this.drawMarker();
      this.drawPin();
    });

    if ((links || this.showVehicles && now < this.animUntil) && !this.animTimer) {
      this.animTimer = setTimeout(() => {
        this.animTimer = 0;
        this.requestFrame();
      }, ANIM_FRAME_MS);
    }
  }

  // The static layer (into this.ctx): background, terrain, footprints, the network and the lines (focus: objectFocus,
  // null - the connections view, without lines).
  drawStatic(focus) {
    const { ctx, dpr, w, h, view } = this;
    const css = (name) => this.theme[name];
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.fillStyle = css("--map-bg");
    ctx.fillRect(0, 0, w, h);

    if (this.terrain && (this.showTerrain || this.overlay)) timed("frame/static/terrain", () => {
      const s = view.scale, t = this.terrain;
      ctx.setTransform(s * dpr, 0, 0, s * dpr, (w / 2 - view.x * s) * dpr, (h / 2 - view.y * s) * dpr);
      ctx.imageSmoothingEnabled = true;
      ctx.imageSmoothingQuality = "high";
      if (this.showTerrain) ctx.drawImage(t.image, t.x, t.y, t.w, t.h);
      if (this.overlay) ctx.drawImage(this.overlay, t.x, t.y, t.w, t.h);
    });

    if (this.footprints.length) timed("frame/static/footprints", () => {
      // under the network, so the tracks and roads of a station run over its area
      const s = view.scale;
      ctx.setTransform(s * dpr, 0, 0, s * dpr, (w / 2 - view.x * s) * dpr, (h / 2 - view.y * s) * dpr);
      ctx.lineJoin = "round";
      ctx.lineWidth = 1 / s;
      const shown = { stations: this.showStations, warehouses: this.showWarehouses };
      for (const f of this.footprints) {
        if (!shown[f.layer]) continue;
        const color = css(f.color) || css("--accent");
        ctx.fillStyle = color;
        ctx.strokeStyle = color;
        ctx.globalAlpha = FOOTPRINT_FILL;
        ctx.fill(f.path);
        ctx.globalAlpha = FOOTPRINT_LINE;
        ctx.stroke(f.path);
      }
      ctx.globalAlpha = 1;
    });

    if (this.paths) timed("frame/static/network", () => {
      const s = view.scale;
      ctx.setTransform(s * dpr, 0, 0, s * dpr, (w / 2 - view.x * s) * dpr, (h / 2 - view.y * s) * dpr);
      ctx.lineCap = "round";
      ctx.lineJoin = "round";
      const px = 1 / s; // 1 screen pixel in world units

      const stroke = (path, color, width, dash) => {
        ctx.strokeStyle = color;
        ctx.lineWidth = width * px;
        ctx.setLineDash(dash ? dash.map((d) => d * px) : []);
        ctx.stroke(path);
      };
      stroke(this.paths.streetTunnel, css("--map-street-tunnel"), 1, [3, 3]);
      // real width when zoomed in; zoomed out at least STREET_MIN_PX_PER_M px per meter, so a main road stays wider
      for (const [width, path] of this.paths.streets) {
        const px = width ? Math.max(width * s, 1, width * STREET_MIN_PX_PER_M) : Math.max(1, Math.min(4, 6 * s));
        stroke(path, css("--map-street"), px);
      }
      stroke(this.paths.trackTunnel, css("--map-track-tunnel"), 1.5, [4, 3]);
      stroke(this.paths.track, css("--map-track"), Math.max(1.5, Math.min(5, 8 * s)));
      ctx.setLineDash([]);
    });

    if (!focus) return;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.globalAlpha = focus.alpha;
    timed("frame/static/lines", () => {
      if (this.linesView) this.drawVisibleLines(focus.hotLine, focus.selLines);
      else for (const id of focus.selLines) this.drawLine(id);
    });
    ctx.globalAlpha = 1;
  }

  // A point pointed at in the list, or a line pointed at in the panel or via a vehicle on the map (then together
  // with the selected object, e.g. a town): the rest semi-transparent (alpha), the pointed ones last, in full color.
  // The vehicles of the pointed or selected line (focusLines) are drawn last, the others dimmed.
  objectFocus() {
    const hotLine = this.hotLine();
    const spotLine = this.spotLine(hotLine);
    const spot = this.spotItem(spotLine === null ? this.listHover : this.selected);
    const selLines = this.selectedLines();
    const focusLines = hotLine !== null ? new Set([hotLine]) : selLines;
    return { hotLine, spotLine, spot, selLines, focusLines, alpha: spot || spotLine !== null ? DIM : 1 };
  }

  // Industries, warehouses, stations, vehicles and towns over the lines of the static layer: the normal map, without
  // the selected industry's connections.
  drawObjects({ spotLine, spot, focusLines, alpha }) {
    const { ctx } = this;
    ctx.globalAlpha = alpha;
    if (this.showIndustries) timed("frame/objects/industries", () => this.drawIndustries(spot));
    if (this.showWarehouses) timed("frame/objects/warehouses", () => this.drawWarehouses(spot));
    if (this.showStations) timed("frame/objects/stops", () => this.drawStops());
    if (this.showStations) timed("frame/objects/stations", () => this.drawStations(spot));
    // vehicles of other lines dimmed once (when the rest of the map is already dimmed - no extra DIM)
    if (this.showVehicles) timed("frame/objects/vehicles", () => this.drawVehicles((v) => !focusLines.has(v.line), !focusLines.size || ctx.globalAlpha < 1 ? 1 : DIM));
    timed("frame/objects/towns", () => this.drawTowns(spot));
    ctx.globalAlpha = 1;
    timed("frame/objects/pointed", () => {
      if (spotLine !== null) this.drawLine(spotLine);
      if (this.showVehicles && focusLines.size) this.drawVehicles((v) => focusLines.has(v.line));
      if (spot?.kind === "industry") this.drawIndustry(spot);
      else if (spot?.kind === "warehouse") this.drawWarehouse(spot);
      else if (spot?.kind === "station") this.drawStation(spot);
      else if (spot?.kind === "town") this.drawTown(spot, null);
    });
    perfInfo("vehicles", this.vehicles.length);
  }

  // ---------------------------------------------------------------- connections of an industry / a cargo
  // A cargo pointed at in the details of a town or industry (kind, id): dir "in" - what it needs, its possible
  // suppliers; "out" - what it produces, its possible consumers. Shown on the map instead of the normal view, zoomed out
  // to fit them; when the pointer leaves, the view from before comes back. kind = null - none.
  hoverCargo(kind, id, cargo, dir) {
    const key = kind && cargo ? `${kind}|${id}|${cargo}|${dir}` : null;
    if (key === (this.cargoHover?.key ?? null)) return;
    if (!this.cargoHover) this.cargoView = { ...this.view };
    this.cargoHover = key ? { key, kind, id, cargo, dir } : null;
    if (!this.cargoHover || !this.fitLinks()) {
      if (this.cargoView) this.view = this.cargoView;
      if (!this.cargoHover) this.cargoView = null;
    }
    this.requestDraw();
  }

  // Potential trade partners anywhere on the map, { center, dir, suppliers, consumers } with the cargo of each connection:
  //  * the cargo pointed at (hoverCargo): the industries that produce it -> the town / industry ("in"),
  //    or the industry -> the industries and towns that need it ("out");
  //  * otherwise the selected industry (dir null): suppliers - industries that produce what it needs,
  //    consumers - industries and towns that need what it produces.
  // null when neither. Kept until the selection or the data changes.
  industryLinks() {
    const sel = this.selected, hover = this.cargoHover;
    const key = hover ? `cargo|${hover.key}` : sel?.kind === "industry" ? `industry|${sel.id}` : null;
    if (!key) return null;
    const c = this.linksCache;
    if (c?.key === key && c.industries === this.industries && c.towns === this.towns) return c.links;
    let links = null;
    const center = hover && (hover.kind === "town" ? this.towns : this.industries).find((x) => x.id === hover.id);
    const ind = !hover && this.industries.find((i) => i.id === sel.id);
    if (center) {
      const { cargo, dir } = hover;
      const link = (item) => ({ item, cargos: [cargo] });
      const suppliers = dir === "in" ? this.industries.filter((i) => i !== center && i.produces.has(cargo)).map(link) : [];
      const consumers = dir === "out" ? [...this.industries, ...this.towns].filter((x) => x !== center && x.needs.has(cargo)).map(link) : [];
      links = { center, dir, suppliers, consumers };
    } else if (ind) {
      const common = (a, b) => [...a].filter((cargo) => b.has(cargo));
      const suppliers = [], consumers = [];
      for (const other of this.industries) {
        if (other === ind) continue;
        const gets = common(ind.needs, other.produces);
        if (gets.length) suppliers.push({ item: other, cargos: gets });
        const gives = common(ind.produces, other.needs);
        if (gives.length) consumers.push({ item: other, cargos: gives });
      }
      for (const town of this.towns) {
        const gives = common(ind.produces, town.needs);
        if (gives.length) consumers.push({ item: town, cargos: gives });
      }
      links = { center: ind, dir: null, suppliers, consumers };
    }
    this.linksCache = { key, industries: this.industries, towns: this.towns, links };
    return links;
  }

  // Zooms out to the industry / town with all its connections; false when it has none (or there are no connections).
  fitLinks() {
    const links = this.industryLinks();
    if (!links || !links.suppliers.length && !links.consumers.length) return false;
    const items = [links.center, ...links.suppliers.map((l) => l.item), ...links.consumers.map((l) => l.item)];
    const xs = items.map((i) => i.px), ys = items.map((i) => i.py);
    this.fit({ minX: Math.min(...xs), minY: Math.min(...ys), maxX: Math.max(...xs), maxY: Math.max(...ys) }, 0.25);
    return true;
  }

  // The rest of the map dimmed (without lines and vehicles), the connections on top: green arrows from the suppliers,
  // blue ones to the consumers, then the partners and the industry / town itself in full color, and a legend.
  drawLinksView(links, now) {
    const css = (name) => this.theme[name];
    const { ctx } = this;
    const { center } = links;
    const partners = new Set([...links.suppliers, ...links.consumers].map((l) => l.item));
    ctx.globalAlpha = DIM;
    if (this.showIndustries) for (const ind of this.industries) if (ind !== center && !partners.has(ind)) this.drawIndustry(ind);
    if (this.showWarehouses) this.drawWarehouses(null);
    if (this.showStations) this.drawStops();
    if (this.showStations) this.drawStations(null);
    const labels = [];
    for (const t of this.towns) if (t !== center && !partners.has(t)) this.drawTown(t, labels);
    ctx.globalAlpha = 1;

    const time = now / 1000;
    const icons = links.suppliers.length + links.consumers.length <= LINK_ICONS_MAX;
    for (const l of links.suppliers) this.drawLink(l.item, center, icons ? l.cargos : [], css("--link-supply"), time);
    for (const l of links.consumers) this.drawLink(center, l.item, icons ? l.cargos : [], css("--link-consume"), time);
    const partnerLabels = [];
    for (const item of partners) {
      if (item.kind === "town") this.drawTown(item, partnerLabels);
      else this.drawIndustry(item);
    }
    if (center.kind === "town") this.drawTown(center, null);
    else this.drawIndustry(center);
    const spot = this.spotItem(this.listHover);
    if (spot?.kind === "industry") this.drawIndustry(spot);
    else if (spot?.kind === "town") this.drawTown(spot, null);
    this.drawLinksLegend(links);
  }

  // One connection from -> to (map objects) as a curve: a faint wide glow, a thin line, arrows moving towards
  // the receiver, an arrowhead at the end and the cargo icons in the middle.
  drawLink(from, to, cargos, color, time) {
    const css = (name) => this.theme[name];
    const { ctx } = this;
    const [x0, y0] = this.toScreen(from.px, from.py);
    const [x1, y1] = this.toScreen(to.px, to.py);
    const dx = x1 - x0, dy = y1 - y0, len = Math.hypot(dx, dy);
    if (len < 24) return;
    // the control point to the right of the direction (on screen)
    const cx = (x0 + x1) / 2 - dy * LINK_BEND, cy = (y0 + y1) / 2 + dx * LINK_BEND;
    if (Math.max(x0, x1, cx) < 0 || Math.max(y0, y1, cy) < 0 || Math.min(x0, x1, cx) > this.w || Math.min(y0, y1, cy) > this.h) return;
    const curve = sampleQuad(x0, y0, cx, cy, x1, y1);
    const s0 = 12, s1 = curve.length - 14; // not over the end objects
    if (s1 - s0 < 10) return;

    ctx.save();
    ctx.lineCap = "round";
    ctx.lineJoin = "round";
    ctx.beginPath();
    tracePart(ctx, curve, s0, s1);
    ctx.strokeStyle = color;
    ctx.globalAlpha = 0.18;
    ctx.lineWidth = 7;
    ctx.stroke();
    ctx.globalAlpha = 0.75;
    ctx.lineWidth = 2;
    ctx.stroke();

    // moving arrows; they fade in after the start and out before the end
    ctx.fillStyle = color;
    const offset = (time * LINK_SPEED) % LINK_ARROW_GAP;
    for (let s = s0 + offset; s < s1 - 12; s += LINK_ARROW_GAP) {
      ctx.globalAlpha = 0.95 * Math.max(0, Math.min(1, (s - s0) / 25, (s1 - 12 - s) / 25));
      arrowHead(ctx, curve.at(s), 5);
    }
    ctx.globalAlpha = 1;
    arrowHead(ctx, curve.at(s1), 7);

    const imgs = cargos.map((cargo) => this.image(cargoImg(cargo))).filter(Boolean).slice(0, 3);
    if (imgs.length && len > 90) {
      const mid = curve.at(curve.length / 2);
      const s = LINK_ICON_PX, w = imgs.length * s + 6;
      ctx.fillStyle = css("--panel");
      ctx.strokeStyle = color;
      ctx.lineWidth = 1.5;
      ctx.beginPath();
      ctx.roundRect(mid.x - w / 2, mid.y - s / 2 - 3, w, s + 6, (s + 6) / 2);
      ctx.fill();
      ctx.stroke();
      imgs.forEach((img, i) => ctx.drawImage(img, mid.x - w / 2 + 3 + i * s, mid.y - s / 2, s, s));
    }
    ctx.restore();
  }

  // Top center (the map legend is at the bottom, the detail window on the left): what the colors mean and how many connections there are.
  drawLinksLegend(links) {
    const css = (name) => this.theme[name];
    const { ctx } = this;
    // one cargo pointed at: only its direction
    const rows = [
      links.dir !== "out" && [css("--link-supply"), t("links.suppliers", { n: fmtInt(links.suppliers.length) })],
      links.dir !== "in" && [css("--link-consume"), t("links.consumers", { n: fmtInt(links.consumers.length) })],
    ].filter(Boolean);
    ctx.save();
    ctx.font = `600 12px ${css("--font")}`;
    ctx.textBaseline = "middle";
    const w = Math.max(...rows.map(([, text]) => ctx.measureText(text).width)) + 52, h = rows.length * 20 + 12;
    const x = Math.round((this.w - w) / 2), y = 12;
    ctx.globalAlpha = 0.92;
    ctx.fillStyle = css("--panel");
    ctx.beginPath();
    ctx.roundRect(x, y, w, h, 8);
    ctx.fill();
    ctx.globalAlpha = 1;
    rows.forEach(([color, text], i) => {
      const ry = y + 16 + i * 20;
      ctx.strokeStyle = color;
      ctx.fillStyle = color;
      ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.moveTo(x + 10, ry);
      ctx.lineTo(x + 26, ry);
      ctx.stroke();
      arrowHead(ctx, { x: x + 32, y: ry, angle: 0 }, 5);
      ctx.fillStyle = css("--town-label");
      ctx.fillText(text, x + 42, ry);
    });
    ctx.restore();
  }

  // The pointed line: in the panel list or via the vehicle under the cursor on the map; otherwise null.
  hotLine() {
    const h = this.listHover?.kind === "line" ? this.listHover.id : this.hovered?.kind === "vehicle" ? this.hovered.line : null;
    return h != null && this.lines.has(h) ? h : null;
  }

  // The pointed line that drawVisibleLines no longer highlights (it does that on the Lines tab with the eye,
  // without a selected line, as long as the line is among the drawn ones); otherwise null.
  spotLine(hot) {
    if (hot === null) return null;
    return this.linesView && !this.selectedLines().size && this.visibleIds.has(hot) ? null : hot;
  }

  // Town / industry / station ({ kind, id }), as long as it's on screen; otherwise null.
  spotItem(h) {
    const items = { town: this.towns, industry: this.industries, warehouse: this.warehouses, station: this.stations }[h?.kind];
    const item = items?.find((x) => x.id === h.id);
    if (!item) return null;
    const [x, y] = this.toScreen(item.px, item.py);
    return x >= 0 && y >= 0 && x <= this.w && y <= this.h ? item : null;
  }

  // ---------------------------------------------------------------- pin (a link to a place)
  // pin = { x, y, note } in game coordinates (y to the north), or null.
  setPin(pin) {
    this.pin = pin;
    this.requestDraw();
  }

  // Centers the view on the pin; scale from the link, and without it - the zoom used when selecting a town.
  showPin(scale) {
    if (!this.pin) return;
    this.view.x = this.pin.x;
    this.view.y = -this.pin.y;
    this.view.scale = scale ?? Math.max(this.view.scale, 0.25);
    this.fitted = true; // data arriving later must not reset the view to the whole map
    this.requestDraw();
  }

  // mode: "pin" | "build" | null - what the next click on the map picks ("build": every click, until turned off).
  setPlaceMode(mode) {
    this.placeMode = mode;
    this.canvas.style.cursor = mode ? "crosshair" : "grab";
    this.onPlaceMode?.(mode);
  }

  // ---------------------------------------------------------------- building spot
  // marker = { x, y, label, radius, ok } in game coordinates; radius (m) = the minimum distance from other objects.
  setMarker(marker) {
    this.marker = marker;
    this.requestDraw();
  }

  // A diamond at the building spot, a dashed circle with the minimum distance and the name next to it.
  drawMarker() {
    const m = this.marker;
    if (!m) return;
    const css = (name) => this.theme[name];
    const { ctx } = this;
    const [x, y] = this.toScreen(m.x, -m.y);
    const r = (m.radius ?? 0) * this.view.scale;
    if (x < -r - 300 || y < -r - 60 || x > this.w + r + 300 || y > this.h + r + 60) return;
    const color = m.ok ? css("--accent") : css("--pin");

    if (r > 6) {
      ctx.save();
      ctx.setLineDash([6, 5]);
      ctx.strokeStyle = color;
      ctx.lineWidth = 1.5;
      ctx.globalAlpha = 0.8;
      ctx.beginPath();
      ctx.arc(x, y, r, 0, Math.PI * 2);
      ctx.stroke();
      ctx.restore();
    }

    ctx.fillStyle = color;
    ctx.strokeStyle = css("--map-bg");
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(x, y - 9);
    ctx.lineTo(x + 9, y);
    ctx.lineTo(x, y + 9);
    ctx.lineTo(x - 9, y);
    ctx.closePath();
    ctx.fill();
    ctx.stroke();

    const label = truncate((m.label ?? "").trim(), 40);
    if (!label) return;
    ctx.font = `600 12px ${css("--font")}`;
    ctx.textBaseline = "middle";
    const w = ctx.measureText(label).width + 14;
    const bx = x + 14, by = y - 11;
    ctx.fillStyle = css("--panel");
    ctx.strokeStyle = color;
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.roundRect(bx, by, w, 22, 6);
    ctx.fill();
    ctx.stroke();
    ctx.fillStyle = css("--town-label");
    ctx.fillText(label, bx + 7, y);
  }

  // Pin: a head in the accent color above the point, below it the (shortened) description in a bubble.
  drawPin() {
    if (!this.pin) return;
    const css = (name) => this.theme[name];
    const { ctx } = this;
    const [x, y] = this.toScreen(this.pin.x, -this.pin.y);
    if (x < -300 || y < -60 || x > this.w + 300 || y > this.h + 60) return;
    const hy = y - PIN_HEAD_Y;

    ctx.fillStyle = "rgba(0, 0, 0, .35)";
    ctx.beginPath();
    ctx.ellipse(x, y, 5, 2, 0, 0, Math.PI * 2);
    ctx.fill();

    ctx.strokeStyle = css("--map-bg");
    ctx.fillStyle = css("--pin");
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(x, y);
    ctx.lineTo(x - 7, hy + 5);
    ctx.arc(x, hy, 9, Math.PI * 0.8, Math.PI * 0.2);
    ctx.closePath();
    ctx.fill();
    ctx.stroke();
    ctx.fillStyle = css("--map-bg");
    ctx.beginPath();
    ctx.arc(x, hy, 3.5, 0, Math.PI * 2);
    ctx.fill();

    const note = (this.pin.note ?? "").replace(/\s+/g, " ").trim();
    if (!note) return;
    ctx.font = `600 12px ${css("--font")}`;
    ctx.textBaseline = "middle";
    const lines = wrapText(ctx, note, 220);
    const lh = 16;
    const w = Math.max(...lines.map((l) => ctx.measureText(l).width)) + 14;
    const h = lines.length * lh + 6;
    const bx = x + 14, by = hy - 11;
    ctx.fillStyle = css("--panel");
    ctx.strokeStyle = css("--pin");
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.roundRect(bx, by, w, h, 6);
    ctx.fill();
    ctx.stroke();
    ctx.fillStyle = css("--town-label");
    lines.forEach((l, i) => ctx.fillText(l, bx + 7, by + 3 + lh / 2 + i * lh));
  }

  // The line route is computed lazily and kept until the stops / network change.
  lineRoute(id) {
    const line = this.lines.get(id);
    if (!line || !this.graph) return null;
    const key = JSON.stringify([line.carrier, (line.stops ?? []).map((s) => s.pos)]);
    const cached = this.routeCache.get(id);
    if (cached?.key === key) return cached.route;
    const route = this.graph.routeLine(line, this.gameRoutes.get(id));
    this.routeCache.set(id, { key, route });
    return route;
  }

  // All lines crossing the screen (and matching the panel filters); with selected / pinned ones - only those.
  // We report the set of lines on screen to the panel (the line list) when it changes.
  // hotLine: the pointed line (in the list or via a vehicle) - on top, wider, the rest dimmed.
  drawVisibleLines(hotLine, selLines) {
    const [minX, minY] = this.toWorld(0, 0);
    const [maxX, maxY] = this.toWorld(this.w, this.h);
    const rect = { minX, minY, maxX, maxY };
    const visible = timed("frame/static/lines/routes, on screen", () => [...this.lines.values()]
      .filter((l) => this.linesView.match(l) && routeInRect(this.lineRoute(l.id), rect))
      .sort((a, b) => (a.vehicleCount ?? 0) - (b.vehicleCount ?? 0))); // the busiest on top
    perfInfo("lines on screen", `${visible.length} of ${this.lines.size}`);

    // The pointed line: the others semi-transparent, it on top and 50% wider.
    this.visibleIds = new Set(visible.map((l) => l.id));
    const hot = hotLine !== null && this.visibleIds.has(hotLine) ? hotLine : null;
    if (selLines.size) for (const id of selLines) this.drawLine(id);
    else timed("frame/static/lines/stroke", () => {
      const alpha = this.ctx.globalAlpha; // already dimmed when a town / industry / station is pointed at
      this.ctx.globalAlpha = hot === null ? alpha : DIM;
      for (const l of visible) if (l.id !== hot) this.drawLine(l.id, true);
      this.ctx.globalAlpha = alpha;
      if (hot !== null) this.drawLine(hot, true, 1.5);
    });

    const key = visible.map((l) => l.id).sort((a, b) => a - b).join(",");
    if (key !== this.visibleKey) {
      this.visibleKey = key;
      this.onVisibleLines?.(new Set(this.visibleIds));
    }
  }

  // A line on top: an outline in the background color, the route in the line color, stops as circles.
  // compact: thinner, for drawing many lines at once; stops only when zoomed in.
  // widen: route width multiplier (highlighting the line pointed at in the list).
  drawLine(id, compact = false, widen = 1) {
    const route = this.lineRoute(id);
    if (!route) return;
    const css = (name) => this.theme[name];
    const { ctx, dpr, w, h, view } = this;
    const s = view.scale;
    const color = rgb(this.lines.get(id).color, css("--accent"));

    ctx.save();
    ctx.setTransform(s * dpr, 0, 0, s * dpr, (w / 2 - view.x * s) * dpr, (h / 2 - view.y * s) * dpr);
    ctx.lineCap = "round";
    ctx.lineJoin = "round";
    ctx.strokeStyle = css("--map-bg");
    ctx.lineWidth = ((compact ? 2.5 : 4) * widen + (compact ? 2 : 3)) / s; // the outline around the route
    ctx.stroke(route.path);
    ctx.strokeStyle = color;
    ctx.lineWidth = (compact ? 2.5 : 4) * widen / s;
    ctx.stroke(route.path);
    ctx.restore();

    if (compact && s < STATION_ICON_SCALE) return;
    ctx.lineWidth = compact ? 1.5 : 2.5;
    for (const stop of route.stops) {
      const [x, y] = this.toScreen(stop.x, stop.y);
      if (x < -10 || y < -10 || x > w + 10 || y > h + 10) continue;
      ctx.fillStyle = css("--map-bg");
      ctx.strokeStyle = color;
      ctx.beginPath();
      ctx.arc(x, y, compact ? 3 : 5, 0, Math.PI * 2);
      ctx.fill();
      ctx.stroke();
    }
    if (this.focusedStop?.line === id && !compact) {
      const [x, y] = this.toScreen(this.focusedStop.x, this.focusedStop.y);
      this.ring(x, y, 10);
    }
  }

  // skip: an object drawn separately (pointed at in the list) - skipped here.
  drawIndustries(skip) {
    for (const ind of this.industries) if (ind !== skip) this.drawIndustry(ind);
  }

  drawIndustry(ind) {
    const css = (name) => this.theme[name];
    const { ctx } = this;
    const size = this.view.scale > 0.08 ? 20 : 10;
    const [x, y] = this.toScreen(ind.px, ind.py);
    if (x < -30 || y < -30 || x > this.w + 30 || y > this.h + 30) return;
    ctx.fillStyle = css(ind.type === "raw" ? "--industry-raw" : "--industry-processing");
    ctx.strokeStyle = css("--map-bg");
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.rect(x - size / 2, y - size / 2, size, size);
    ctx.fill();
    ctx.stroke();
    if (this.isMarked(ind)) this.ring(x, y, size);
    // like station icons: only when zoomed in
    if (this.view.scale >= STATION_ICON_SCALE) this.drawOutputs(ind.outputs, x, y - size / 2 - 3);
  }

  drawWarehouses(skip) {
    for (const w of this.warehouses) if (w !== skip) this.drawWarehouse(w);
  }

  // Warehouse: a little house (a square with a roof) in the warehouse color, a fill bar below it; when zoomed in
  // icons of the stored cargo above it and the "amount / capacity" level below the bar.
  drawWarehouse(wh) {
    const css = (name) => this.theme[name];
    const { ctx } = this;
    const big = this.view.scale > 0.08;
    const size = big ? 18 : 10;
    const [x, y] = this.toScreen(wh.px, wh.py);
    if (x < -40 || y < -40 || x > this.w + 40 || y > this.h + 40) return;
    const half = size / 2, roof = size * 0.45;
    ctx.fillStyle = css("--warehouse");
    ctx.strokeStyle = css("--map-bg");
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.moveTo(x - half, y + half);
    ctx.lineTo(x - half, y - half + roof * 0.5);
    ctx.lineTo(x, y - half - roof * 0.5);
    ctx.lineTo(x + half, y - half + roof * 0.5);
    ctx.lineTo(x + half, y + half);
    ctx.closePath();
    ctx.fill();
    ctx.stroke();
    if (this.isMarked(wh)) this.ring(x, y, size);

    const d = wh.data;
    const fill = d.capacity > 0 ? Math.min(1, (d.amount ?? 0) / d.capacity) : 0;
    const bw = big ? 28 : 14, bh = big ? 4 : 3, by = y + half + 3;
    ctx.fillStyle = css("--map-bg");
    ctx.fillRect(x - bw / 2 - 1, by - 1, bw + 2, bh + 2);
    ctx.fillStyle = css("--map-street");
    ctx.fillRect(x - bw / 2, by, bw, bh);
    ctx.fillStyle = css("--warehouse");
    ctx.fillRect(x - bw / 2, by, bw * fill, bh);

    if (this.view.scale < STATION_ICON_SCALE) return;
    const cargos = Object.entries(d.cargo ?? {}).sort((a, b) => b[1] - a[1]).map(([c]) => cargoImg(c)).filter(Boolean);
    this.drawOutputs(cargos.slice(0, 4), x, y - half - roof * 0.5 - 3);
    if (!big || !d.capacity) return;
    const label = `${fmtInt(d.amount ?? 0)} / ${fmtInt(d.capacity)}`;
    ctx.font = `600 11px ${css("--font")}`;
    ctx.textAlign = "center";
    ctx.textBaseline = "top";
    ctx.lineWidth = 3;
    ctx.strokeStyle = css("--map-bg");
    ctx.strokeText(label, x, by + bh + 3);
    ctx.fillStyle = css("--town-label");
    ctx.fillText(label, x, by + bh + 3);
    ctx.textAlign = "start";
  }

  // Icons of the produced cargo in a row, centered above the industry square (bottomY = the bottom of the row).
  drawOutputs(srcs, x, bottomY) {
    const s = OUTPUT_ICON_PX, gap = 1;
    const imgs = srcs.map((src) => this.image(src)).filter(Boolean);
    let left = x - (imgs.length * s + (imgs.length - 1) * gap) / 2;
    for (const img of imgs) {
      this.ctx.drawImage(img, left, bottomY - s, s, s);
      left += s + gap;
    }
  }

  // Street stops (zoomed in only): a dot in the carrier color; when zoomed in further
  // the number of waiting passengers below it (like a warehouse level).
  drawStops() {
    if (this.view.scale < STATION_ICON_SCALE) return;
    const css = (name) => this.theme[name];
    const { ctx } = this;
    const big = this.view.scale > STOP_LABEL_SCALE;
    for (const st of this.stops) {
      const [x, y] = this.toScreen(st.px, st.py);
      if (x < -20 || y < -20 || x > this.w + 20 || y > this.h + 20) continue;
      // a ring like a small station circle - filled dots are vehicles
      ctx.fillStyle = css("--panel");
      ctx.strokeStyle = css(`--station-${String(st.type).toLowerCase()}`) || css("--station-road");
      ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.arc(x, y, STOP_R, 0, Math.PI * 2);
      ctx.fill();
      ctx.stroke();
      if (this.isMarked(st)) this.ring(x, y, STOP_R * 2);
      if (big && st.waiting != null) this.drawWaiting(st.waiting, x, y + STOP_R + 2);
    }
  }

  // Waiting passengers below the point (topY = the top of the text): the passenger icon from the game (as with
  // vehicle cargo) and the number, centered on x together.
  drawWaiting(n, x, topY) {
    const css = (name) => this.theme[name];
    const { ctx } = this;
    const label = fmtInt(n), s = WAITING_ICON_PX, gap = 2;
    ctx.font = `600 11px ${css("--font")}`;
    const icon = this.image(cargoImg("passengers"));
    const w = (icon ? s + gap : 0) + ctx.measureText(label).width;
    let left = x - w / 2;
    if (icon) {
      ctx.drawImage(icon, left, topY - 1, s, s);
      left += s + gap;
    }
    ctx.textAlign = "left";
    ctx.textBaseline = "middle";
    ctx.lineWidth = 3;
    ctx.strokeStyle = css("--map-bg");
    ctx.strokeText(label, left, topY + s / 2);
    ctx.fillStyle = css("--town-label");
    ctx.fillText(label, left, topY + s / 2);
    ctx.textAlign = "start";
  }

  // Zoomed in: a dark circle with a border in the kind's color and an icon; zoomed out: just a dot.
  drawStations(skip) {
    for (const st of this.stations) if (st !== skip) this.drawStation(st);
  }

  drawStation(st) {
    const css = (name) => this.theme[name];
    const { ctx } = this;
    const icons = this.view.scale >= STATION_ICON_SCALE;
    const r = icons ? 16 : 8;
    const [x, y] = this.toScreen(st.px, st.py);
    if (x < -20 || y < -20 || x > this.w + 20 || y > this.h + 20) return;
    const color = css(`--station-${String(st.type).toLowerCase()}`) || css("--accent");
    ctx.beginPath();
    ctx.arc(x, y, r, 0, Math.PI * 2);
    if (icons) {
      ctx.fillStyle = css("--panel");
      ctx.strokeStyle = color;
      ctx.lineWidth = 2;
      ctx.fill();
      ctx.stroke();
      const icon = this.tintedIcon(stationImg(st.data), color, STATION_ICON_PX);
      if (icon) {
        ctx.drawImage(icon, x - STATION_ICON_PX / 2, y - STATION_ICON_PX / 2, STATION_ICON_PX, STATION_ICON_PX);
      } else { // the image is still loading or doesn't exist
        ctx.fillStyle = color;
        ctx.beginPath();
        ctx.arc(x, y, 4, 0, Math.PI * 2);
        ctx.fill();
      }
    } else {
      ctx.fillStyle = color;
      ctx.strokeStyle = css("--map-bg");
      ctx.lineWidth = 1.5;
      ctx.fill();
      ctx.stroke();
    }
    if (this.isMarked(st)) this.ring(x, y, r * 2);
    if (this.view.scale > STOP_LABEL_SCALE && st.data.waiting != null) this.drawWaiting(st.data.waiting, x, y + r + 3);
  }

  // ---------------------------------------------------------------- vehicles
  // A vehicle's movement between data batches (an entry in vehicleAnim), from t0 over dur:
  //   * mode "route" - along the line route loop: distance from -> from + delta,
  //   * mode "line"  - in a straight line (x0, y0) -> (x1, y1); route + d: that point on the route, if it lies on it.
  // With every batch the vehicle travels from where it is on screen to the new position over updateMs,
  // i.e. until the next batch. This is interpolation: the picture is one batch behind, but the movement is smooth.
  //
  // The position from the mod (v.pos) is real; along the route when it lies near the segment leading to v.stop, otherwise
  // in a straight line (ships, planes, detours). Without pos (an old export or positions turned off in the mod)
  // we guess from the stop number (retargetGuessed).
  retargetVehicles(now) {
    this.vehiclesDirty = false;
    const prev = this.vehicleAnim;
    const next = new Map();
    const guessed = [];
    let moving = false;

    for (const v of this.vehicleData) {
      if (!this.lines.has(v.line) || v.state === "IN_DEPOT") continue;
      if (!v.pos) {
        if (v.state === "EN_ROUTE" || v.state === "AT_TERMINAL") guessed.push(v);
        continue;
      }
      const p = { x: v.pos[0], y: -v.pos[1] };
      const a = prev.get(v.id);
      const route = this.lineRoute(v.line);
      const proj = route ? projectOnLeg(route, v.stop, p) : null;
      const onRoute = proj && proj.dist <= ON_ROUTE_M;
      const cur = a ? this.animPoint(a, now) : p;

      // Along the route, if the previous position was on it too and the movement is forward, no longer than
      // 3x the straight line (otherwise it's e.g. a U-turn or an A* route different from the game's - then a straight line).
      const from = onRoute ? this.distanceOn(a, route, now) : null;
      if (from != null) {
        let delta = loopDelta(proj.d - from, route.length);
        if (delta < 0 && delta > -ON_ROUTE_M) delta = 0; // position jitter
        if (delta >= 0 && delta <= 3 * Math.hypot(p.x - cur.x, p.y - cur.y) + 100) {
          next.set(v.id, { v, line: v.line, mode: "route", route, from, delta, t0: now, dur: this.updateMs });
          if (delta) moving = true;
          continue;
        }
      }
      next.set(v.id, {
        v, line: v.line, mode: "line", x0: cur.x, y0: cur.y, x1: p.x, y1: p.y, t0: now, dur: this.updateMs,
        route: onRoute ? route : null, d: onRoute ? proj.d : null,
      });
      if (cur.x !== p.x || cur.y !== p.y) moving = true;
    }

    if (this.retargetGuessed(guessed, prev, next, now)) moving = true;
    this.vehicleAnim = next;
    this.animUntil = moving ? now + this.updateMs : 0;
  }

  // Without a position from the mod: AT_TERMINAL - at the stop; EN_ROUTE - on the route from the previous
  // stop to stop, several vehicles on one segment spread evenly. On a long segment the vehicle
  // stands still until it reaches the stop - hence the positions in the mod. -> whether anything moves.
  retargetGuessed(vehicles, prev, next, now) {
    const groups = new Map(); // line|stop|state -> vehicles
    for (const v of vehicles) {
      const key = `${v.line}|${v.stop}|${v.state}`;
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key).push(v);
    }

    let moving = false;
    for (const vs of groups.values()) {
      const { line: lineId, stop, state } = vs[0];
      const route = this.lineRoute(lineId);
      const leg = route?.legs.get(stop);
      if (!leg) { // no route (e.g. the network hasn't arrived yet): at the stop, without movement
        const pos = this.lines.get(lineId).stops?.[stop]?.pos;
        if (!pos) continue;
        for (const v of vs) {
          next.set(v.id, { v, line: lineId, mode: "line", x0: pos[0], y0: -pos[1], x1: pos[0], y1: -pos[1], t0: now, dur: 1 });
        }
        continue;
      }
      const L = route.length;
      const was = (v) => this.distanceOn(prev.get(v.id), route, now);
      // On a segment the vehicles in the order they drive (new ones first), so they don't pass each other.
      if (state === "EN_ROUTE" && vs.length > 1) {
        const order = (v) => (was(v) == null ? -Infinity : loopDelta(was(v) - leg.start, L));
        vs.sort((a, b) => order(a) - order(b) || a.id - b.id);
      }
      vs.forEach((v, k) => {
        const target = state === "EN_ROUTE" ? leg.start + (leg.end - leg.start) * (k + 1) / (vs.length + 1) : leg.end;
        const from = was(v);
        // Always forward along the loop. A target behind the vehicle comes from the even spread (e.g. a second vehicle
        // left the segment, so this one "drops" from 2/3 to 1/2) - then the vehicle keeps going, to half
        // the distance it has left to the stop.
        let delta = from == null ? 0 : loopDelta(target - from, L);
        if (delta < 0) {
          const done = loopDelta(from - leg.start, L), legLen = leg.end - leg.start;
          delta = done >= 0 && done <= legLen ? (legLen - done) / 2 : 0;
        }
        if (delta) moving = true;
        next.set(v.id, { v, line: lineId, mode: "route", route, from: from ?? target, delta, t0: now, dur: this.updateMs });
      });
    }
    return moving;
  }

  animProgress(a, now) {
    return Math.min(1, (now - a.t0) / a.dur);
  }

  animDistance(a, now) {
    return a.from + a.delta * this.animProgress(a, now);
  }

  // A vehicle's position on screen at the moment now (path coordinates).
  animPoint(a, now) {
    if (a.mode === "route") return pointAtDistance(a.route, this.animDistance(a, now));
    const f = this.animProgress(a, now);
    return { x: a.x0 + (a.x1 - a.x0) * f, y: a.y0 + (a.y1 - a.y0) * f };
  }

  // The distance along route at which the vehicle (entry a) is, or null when it's not on this route.
  distanceOn(a, route, now) {
    if (!a || a.route !== route) return null;
    return a.mode === "route" ? this.animDistance(a, now) : a.d;
  }

  // Vehicles with their position at the moment now (path coordinates).
  positionVehicles(now) {
    if (this.vehiclesDirty) this.retargetVehicles(now);
    const out = [];
    for (const a of this.vehicleAnim.values()) {
      const p = this.animPoint(a, now);
      out.push({ kind: "vehicle", id: a.v.id, data: a.v, line: a.line, px: p.x, py: p.y });
    }
    return out;
  }

  // filter: which vehicles to draw; alpha: extra dimming (vehicles of lines other than the pointed one).
  drawVehicles(filter, alpha = 1) {
    const css = (name) => this.theme[name];
    const { ctx } = this;
    const base = ctx.globalAlpha;
    const r = this.view.scale >= STATION_ICON_SCALE ? 5.5 : 3.5;
    ctx.globalAlpha = base * alpha;
    ctx.lineWidth = 1.5;
    ctx.strokeStyle = css("--map-bg");
    for (const v of this.vehicles) {
      if (!filter(v)) continue;
      const [x, y] = this.toScreen(v.px, v.py);
      if (x < -10 || y < -10 || x > this.w + 10 || y > this.h + 10) continue;
      ctx.fillStyle = rgb(this.lines.get(v.line).color, css("--accent"));
      ctx.beginPath();
      ctx.arc(x, y, r, 0, Math.PI * 2);
      ctx.fill();
      ctx.stroke();
      if (this.isMarked(v)) this.ring(x, y, r * 2);
    }
    ctx.globalAlpha = base;
  }

  // An image from the cache; loaded on first use (we redraw once it loads). null until
  // it loads - or for good when the file doesn't exist.
  image(src) {
    if (!src) return null;
    let img = this.iconImages.get(src);
    if (!img) {
      img = new Image();
      img.onload = () => this.requestDraw();
      img.src = src;
      this.iconImages.set(src, img);
    }
    return img.complete && img.naturalWidth ? img : null;
  }

  // A game icon is a white mask: we tint it once per color and size (in device pixels)
  // on a separate canvas and keep it in the cache. null until the image loads.
  tintedIcon(src, color, size) {
    const img = this.image(src);
    if (!img) return null;
    const px = Math.round(size * this.dpr);
    const key = `${src}|${color}|${px}`;
    let canvas = this.iconCache.get(key);
    if (!canvas) {
      canvas = document.createElement("canvas");
      canvas.width = canvas.height = px;
      const c = canvas.getContext("2d");
      c.imageSmoothingQuality = "high";
      c.drawImage(img, 0, 0, px, px);
      c.globalCompositeOperation = "source-in";
      c.fillStyle = color;
      c.fillRect(0, 0, px, px);
      this.iconCache.set(key, canvas);
    }
    return canvas;
  }

  drawTowns(skip) {
    const labels = []; // labels: from the largest towns, without overlapping
    for (const t of this.towns) if (t !== skip) this.drawTown(t, labels);
  }

  // labels: boxes of the labels already drawn (null - always a label, e.g. a town pointed at in the list).
  drawTown(t, labels) {
    const css = (name) => this.theme[name];
    const { ctx } = this;
    const [x, y] = this.toScreen(t.px, t.py);
    if (x < -50 || y < -50 || x > this.w + 50 || y > this.h + 50) return;
    const r = this.townRadius(t);
    ctx.fillStyle = css("--town");
    ctx.strokeStyle = css("--map-bg");
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.arc(x, y, r, 0, Math.PI * 2);
    ctx.fill();
    ctx.stroke();
    const marked = !labels || this.isMarked(t);
    if (marked) this.ring(x, y, r * 2);

    ctx.font = `600 12px ${css("--font")}`;
    ctx.textBaseline = "middle";
    const text = t.data.name ?? "";
    // The owner (viewer, splitAllOwners) in a second line below the name: smaller, yellow.
    const owner = t.data.owner ?? "";
    const nameW = ctx.measureText(text).width;
    ctx.font = `600 10px ${css("--font")}`;
    const ownerW = owner ? ctx.measureText(owner).width : 0;
    const box = { x: x + r + 4, y: y - 8, w: Math.max(nameW, ownerW) + 6, h: owner ? 28 : 16 };
    if (labels && !marked && labels.some((b) => overlaps(b, box))) return;
    labels?.push(box);
    ctx.lineWidth = 3;
    ctx.strokeStyle = css("--map-bg");
    if (owner) {
      ctx.strokeText(owner, box.x, y + 12);
      ctx.fillStyle = css("--accent");
      ctx.fillText(owner, box.x, y + 12);
    }
    ctx.font = `600 12px ${css("--font")}`;
    ctx.strokeText(text, box.x, y);
    ctx.fillStyle = marked ? css("--accent") : css("--town-label");
    ctx.fillText(text, box.x, y);
  }

  // The radius of a town ball on screen: full from the zoom used when a town is selected (0.25),
  // smoothly smaller when zoomed out, down to half.
  townRadius(t) {
    const k = Math.min(1, 0.5 + 2 * this.view.scale);
    return Math.max(3, t.r * k);
  }

  isMarked(item) {
    const m = (s) => s && s.kind === item.kind && s.id === item.id;
    return m(this.selected) || m(this.hovered) || m(this.listHover) || this.pinned.some(m);
  }

  ring(x, y, size) {
    const css = (name) => this.theme[name];
    const { ctx } = this;
    ctx.strokeStyle = css("--accent");
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.arc(x, y, size / 2 + 5, 0, Math.PI * 2);
    ctx.stroke();
  }

  // ---------------------------------------------------------------- interaction
  hitTest(sx, sy) {
    let best = null, bestD = Infinity;
    const consider = (item, radius) => {
      const [x, y] = this.toScreen(item.px, item.py);
      const d = Math.hypot(x - sx, y - sy);
      if (d <= radius && d < bestD) { best = item; bestD = d; }
    };
    for (const t of this.towns) consider(t, this.townRadius(t) + 4);
    if (this.showIndustries) for (const i of this.industries) consider(i, 8);
    if (this.showWarehouses) for (const w of this.warehouses) consider(w, 9);
    if (this.showStations) {
      const r = this.view.scale >= STATION_ICON_SCALE ? 11 : 6;
      if (this.view.scale >= STATION_ICON_SCALE) for (const s of this.stops) consider(s, STOP_R + 3);
      for (const s of this.stations) consider(s, r);
    }
    for (const v of this.vehicles) consider(v, 6); // positions from the last frame - where they're visible
    // the pin on top: we catch its head (PIN_HEAD_Y px above the chosen point)
    if (this.pin) {
      const [x, y] = this.toScreen(this.pin.x, -this.pin.y);
      if (Math.hypot(x - sx, y - PIN_HEAD_Y - sy) <= 12) return { kind: "pin", id: "pin", data: this.pin };
    }
    return best;
  }

  // The click spot -> game coordinates (x, y; y to the north) for onPlace.
  place(e, mode = this.placeMode ?? "pin") {
    const rect = this.canvas.getBoundingClientRect();
    const sx = e.clientX - rect.left, sy = e.clientY - rect.top;
    const [px, py] = this.toWorld(sx, sy);
    // Pin: one click and done. Building: the mode lasts, further clicks move the spot (js/build.js).
    if (mode !== "build") this.setPlaceMode(null);
    this.onPlace?.({ x: Math.round(px), y: Math.round(-py), sx, sy, mode });
  }

  // Zoom to scale with the world point (wx, wy) under the screen point (sx, sy).
  zoomAt(wx, wy, sx, sy, scale) {
    this.view.scale = Math.max(MIN_SCALE, Math.min(MAX_SCALE, scale));
    this.view.x = wx - (sx - this.w / 2) / this.view.scale;
    this.view.y = wy - (sy - this.h / 2) / this.view.scale;
    this.requestDraw();
  }

  bindEvents() {
    const c = this.canvas;
    const local = (e) => {
      const rect = c.getBoundingClientRect();
      return [e.clientX - rect.left, e.clientY - rect.top];
    };
    let drag = null;
    const pointers = new Map(); // pointerId -> [sx, sy] (touch: pinching with two fingers)
    let pinch = null;           // { dist, wx, wy, scale } from the moment the second finger went down

    const startPinch = () => {
      const [[ax, ay], [bx, by]] = [...pointers.values()];
      const [wx, wy] = this.toWorld((ax + bx) / 2, (ay + by) / 2);
      pinch = { dist: Math.hypot(bx - ax, by - ay) || 1, wx, wy, scale: this.view.scale };
      drag = null;
    };

    c.addEventListener("wheel", (e) => {
      e.preventDefault();
      const [sx, sy] = local(e);
      const [wx, wy] = this.toWorld(sx, sy);
      this.zoomAt(wx, wy, sx, sy, this.view.scale * Math.exp(-e.deltaY * 0.0015)); // the point under the cursor stays
    }, { passive: false });

    // The right button places a pin (instead of the browser menu).
    c.addEventListener("contextmenu", (e) => {
      e.preventDefault();
      this.place(e, "pin");
    });

    c.addEventListener("pointerdown", (e) => {
      if (e.button !== 0) return;
      pointers.set(e.pointerId, local(e));
      if (pointers.size === 2) startPinch();
      else if (pointers.size === 1) drag = { x: e.clientX, y: e.clientY, vx: this.view.x, vy: this.view.y, moved: false };
      try { c.setPointerCapture(e.pointerId); } catch { /* the pointer is no longer active */ }
    });

    c.addEventListener("pointermove", (e) => {
      const [sx, sy] = local(e);
      if (pointers.has(e.pointerId)) pointers.set(e.pointerId, [sx, sy]);
      if (pinch && pointers.size >= 2) {
        // the world point under the center of the fingers follows the center of the fingers: zoom and pan at once
        const [[ax, ay], [bx, by]] = [...pointers.values()];
        const dist = Math.hypot(bx - ax, by - ay);
        this.zoomAt(pinch.wx, pinch.wy, (ax + bx) / 2, (ay + by) / 2, pinch.scale * dist / pinch.dist);
        return;
      }
      if (drag) {
        const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
        if (Math.abs(dx) + Math.abs(dy) > 3) drag.moved = true;
        this.view.x = drag.vx - dx / this.view.scale;
        this.view.y = drag.vy - dy / this.view.scale;
        this.requestDraw();
        return;
      }
      if (pinch) return; // one finger left after pinching - no hover until it's lifted
      const hit = this.hitTest(sx, sy);
      const changed = (hit?.id ?? null) !== (this.hovered?.id ?? null);
      this.hovered = hit ? { kind: hit.kind, id: hit.id, line: hit.line } : null; // line: vehicles only
      c.style.cursor = this.placeMode ? "crosshair" : hit ? "pointer" : "grab";
      this.onHover?.(hit, sx, sy);
      if (changed) this.requestDraw();
    });

    const release = (e) => {
      pointers.delete(e.pointerId);
      if (pinch && !pointers.size) pinch = null;
    };
    c.addEventListener("pointercancel", (e) => {
      release(e);
      drag = null;
    });

    c.addEventListener("pointerup", (e) => {
      const wasClick = drag && !drag.moved && !pinch;
      release(e);
      drag = null;
      if (!wasClick) return;
      if (this.placeMode) {
        this.place(e);
        return;
      }
      const hit = this.hitTest(...local(e));
      if (hit?.kind === "pin") return; // the pin only has a tooltip
      // vehicle: we select its line, stop: the first line that stops at it
      const line = hit?.kind === "vehicle" ? hit.line : hit?.kind === "stop" ? hit.lines[0]?.id : undefined;
      const sel = line !== undefined ? { kind: "line", id: line, data: this.lines.get(line) } : hit?.kind === "stop" ? null : hit;
      this.select(sel?.kind, sel?.id);
      if (sel?.kind === "industry") this.fitLinks();
      this.onSelect?.(sel);
    });

    c.addEventListener("pointerleave", () => {
      this.hovered = null;
      this.onHover?.(null);
      this.requestDraw();
    });
  }
}

// A distance difference on a loop of length L reduced to (-L/4, 3L/4]: forward movement or a little backward.
function loopDelta(d, L) {
  const m = ((d % L) + L) % L;
  return m > L * 0.75 ? m - L : m;
}

// A quadratic curve sampled into points with the distance along it: { pts, length, at(s) -> { x, y, angle } }.
function sampleQuad(x0, y0, cx, cy, x1, y1, n = 32) {
  const pts = [{ x: x0, y: y0, s: 0 }];
  for (let i = 1; i <= n; i++) {
    const u = i / n, v = 1 - u;
    const x = v * v * x0 + 2 * u * v * cx + u * u * x1, y = v * v * y0 + 2 * u * v * cy + u * u * y1;
    const prev = pts[i - 1];
    pts.push({ x, y, s: prev.s + Math.hypot(x - prev.x, y - prev.y) });
  }
  const at = (s) => {
    let i = 1;
    while (i < n && pts[i].s < s) i++;
    const a = pts[i - 1], b = pts[i];
    const f = b.s > a.s ? Math.max(0, Math.min(1, (s - a.s) / (b.s - a.s))) : 0;
    return { x: a.x + (b.x - a.x) * f, y: a.y + (b.y - a.y) * f, angle: Math.atan2(b.y - a.y, b.x - a.x) };
  };
  return { pts, length: pts[n].s, at };
}

// The curve from distance s0 to s1 as a path (beginPath is the caller's).
function tracePart(ctx, curve, s0, s1) {
  const a = curve.at(s0);
  ctx.moveTo(a.x, a.y);
  for (const p of curve.pts) if (p.s > s0 && p.s < s1) ctx.lineTo(p.x, p.y);
  const b = curve.at(s1);
  ctx.lineTo(b.x, b.y);
}

// A filled arrowhead with its tip at p, pointing along p.angle.
function arrowHead(ctx, p, size) {
  ctx.save();
  ctx.translate(p.x, p.y);
  ctx.rotate(p.angle);
  ctx.beginPath();
  ctx.moveTo(0, 0);
  ctx.lineTo(-size * 1.8, -size);
  ctx.lineTo(-size * 1.3, 0);
  ctx.lineTo(-size * 1.8, size);
  ctx.closePath();
  ctx.fill();
  ctx.restore();
}

const truncate = (s, n) => (s.length > n ? `${s.slice(0, n - 1)}…` : s);

/** Text broken into lines up to `maxW` px wide (a word that's too long is broken by characters). */
function wrapText(ctx, text, maxW) {
  const lines = [];
  let line = "";
  for (const word of text.split(" ")) {
    const next = line ? `${line} ${word}` : word;
    if (ctx.measureText(next).width <= maxW) { line = next; continue; }
    if (line) lines.push(line);
    line = "";
    for (const ch of word) {
      if (line && ctx.measureText(line + ch).width > maxW) { lines.push(line); line = ""; }
      line += ch;
    }
  }
  if (line) lines.push(line);
  return lines;
}

const overlaps = (a, b) => a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;
