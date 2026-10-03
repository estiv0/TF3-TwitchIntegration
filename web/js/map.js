// Mapa: sieć (Path2D w układzie świata), miasta i zakłady (w układzie ekranu), pan/zoom, hover, wybór.
// Układ "ścieżki" = (x, -y) ze świata gry, bo w grze oś y rośnie na północ, a na ekranie w dół.

import { cargoImg, industryOfStation, rgb, stationImg, townSize } from "./format.js";
import { RouteGraph, pointAtDistance, projectOnLeg, routeInRect } from "./route.js";

const RAW_INDUSTRIES = new Set([
  "coal_mine", "iron_ore_mine", "quarry", "forest", "farm", "oil_well",
  "oil_platform", "fishing_grounds", "sand_excavator",
]);

export const industryKind = (ind) => {
  const key = String(ind.file ?? "").split("/").pop().replace(/\.con$/, "");
  return RAW_INDUSTRIES.has(key) ? "raw" : "processing";
};

/** Ładunek -> ilość produkowana przez zakład (reguły bez boosterów). */
export function industryOutputs(ind) {
  const outputs = new Map();
  for (const rule of ind.rules ?? []) {
    if (rule.booster) continue;
    for (const [cargo, amount] of Object.entries(rule.output ?? {}))
      outputs.set(cargo, (outputs.get(cargo) ?? 0) + amount);
  }
  return outputs;
}

const COLOR_VARS = ["--map-bg", "--map-street", "--map-street-tunnel", "--map-track", "--map-track-tunnel",
  "--industry-raw", "--industry-processing", "--town", "--town-label", "--accent", "--font", "--panel",
  "--station-rail", "--station-road", "--station-tram", "--station-water", "--station-air", "--pin"];

// Ikony stacji: od tej skali kółko z ikoną, poniżej sama kropka w kolorze rodzaju.
const STATION_ICON_SCALE = 0.05;
const STATION_ICON_PX = 24;
const OUTPUT_ICON_PX = 30; // ikonki produkcji nad zakładem
const PIN_HEAD_Y = 22;      // główka pinezki tyle px nad wskazanym punktem
const DIM = 0.3;            // krycie reszty mapy, gdy na liście w panelu wskazano jeden obiekt
const MIN_SCALE = 0.005, MAX_SCALE = 20;
// Ruch pojazdów między paczkami danych: klatki co ANIM_FRAME_MS (30 fps wystarczy, a cała mapa
// rysuje się od nowa); dłuższa przerwa w danych (karta w tle, gra wstrzymana) - bez animacji, skok.
const ANIM_FRAME_MS = 33;
const UPDATE_MS_MIN = 2000, UPDATE_MS_MAX = 30000, SNAP_AFTER_MS = 60000;
const ON_ROUTE_M = 40; // pozycja pojazdu tak blisko trasy linii = jedzie po niej (animacja wzdłuż trasy)

// Kolory z CSS (motyw) odczytywane raz na klatkę, nie dla każdego obiektu.
function readTheme() {
  const style = getComputedStyle(document.documentElement);
  return Object.fromEntries(COLOR_VARS.map((v) => [v, style.getPropertyValue(v).trim()]));
}

export class MapView {
  constructor(canvas, { onHover, onSelect, onPlace, onPinMode, onVisibleLines, onLayers, focusAnchorY } = {}) {
    this.canvas = canvas;
    this.ctx = canvas.getContext("2d");
    this.onHover = onHover;
    this.onSelect = onSelect;
    this.onPlace = onPlace;     // kliknięcie w trybie pinezki / prawy przycisk -> { x, y, sx, sy }
    this.onPinMode = onPinMode;
    this.onVisibleLines = onVisibleLines; // Set id linii na ekranie (tylko przy włączonym linesView)
    this.onLayers = onLayers;   // warstwa (stacje, zakłady) włączona sama przez focus() - zsynchronizuj przyciski
    // Wysokość (ułamek mapy), na której focus() stawia obiekt; domyślnie środek. Na telefonie wyżej,
    // bo dół mapy zasłania okienko szczegółów.
    this.focusAnchorY = focusAnchorY ?? (() => 0.5);
    this.pin = null;            // { x, y, note } - pinezka z linku albo właśnie stawiana
    this.pinMode = false;

    this.view = { x: 0, y: 0, scale: 0.02 };
    this.paths = null;          // { street, streetTunnel, track, trackTunnel }
    this.bbox = null;           // zakres sieci w układzie ścieżki
    this.towns = [];
    this.industries = [];
    this.showIndustries = true;
    this.stations = [];         // dworce, porty, lotniska (bez przystanków)
    this.showStations = true;
    this.vehicleData = [];      // dynamic.vehicles
    this.vehicleAnim = new Map(); // id pojazdu -> ruch po trasie linii (retargetVehicles)
    this.vehiclesDirty = false; // nowe dane albo trasy - wyznacz cele ruchu przy następnej klatce
    this.vehicles = [];         // pojazdy z pozycją w ostatniej klatce (rysowanie, hitTest)
    this.updateMs = 10000;      // średni odstęp między paczkami dynamic.json = czas jazdy do nowej pozycji
    this.lastDynamicAt = 0;
    this.animUntil = 0;         // do kiedy pojazdy jadą (performance.now)
    this.animTimer = 0;
    this.showVehicles = true;
    this.iconImages = new Map(); // url -> Image (ikony z gry: maski stacji, ładunki)
    this.iconCache = new Map();  // url|kolor|px -> canvas z zabarwioną ikoną
    this.lines = new Map();     // id -> linia z dynamic.json
    this.graph = null;          // RouteGraph - do wyznaczania tras linii
    this.routeCache = new Map(); // id linii -> { key, route }
    this.linesView = null;      // { match(linia) } - zakładka Linie: rysuj wszystkie linie na ekranie
    this.visibleKey = null;     // ostatnio zgłoszony zestaw linii na ekranie (żeby nie zgłaszać co klatkę)
    this.visibleIds = new Set(); // linie narysowane przez drawVisibleLines w ostatniej klatce
    this.listHover = null;      // { kind, id } obiektu wskazanego myszą na liście w panelu
    this.terrain = null;        // { image, x, y, w, h } - tło z heightmapy, prostokąt w układzie ścieżki
    this.showTerrain = true;
    this.selected = null;       // { kind, id }
    this.focusedStop = null;    // { x, y } przystanku zaznaczonej linii wybranego w panelu
    this.hovered = null;
    this.fitted = false;
    this.frame = 0;

    this.resizeObserver = new ResizeObserver(() => this.resize());
    this.resizeObserver.observe(canvas);
    this.bindEvents();
    this.resize();
  }

  // ---------------------------------------------------------------- dane
  setNetwork(network) {
    const f = Object.fromEntries(network.fields.map((name, i) => [name, i]));
    const paths = { street: new Path2D(), streetTunnel: new Path2D(), track: new Path2D(), trackTunnel: new Path2D() };
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;

    for (const e of network.edges) {
      const x0 = e[f.x0], y0 = -e[f.y0], x1 = e[f.x1], y1 = -e[f.y1];
      // Krawędź TF to krzywa Hermite'a (pozycje + tangensy) -> Bezier: c = p ± t/3
      const c0x = x0 + e[f.tx0] / 3, c0y = y0 - e[f.ty0] / 3;
      const c1x = x1 - e[f.tx1] / 3, c1y = y1 + e[f.ty1] / 3;
      const key = (e[f.road] === "TRACK" ? "track" : "street") + (e[f.type] === "TUNNEL" ? "Tunnel" : "");
      const p = paths[key];
      p.moveTo(x0, y0);
      p.bezierCurveTo(c0x, c0y, c1x, c1y, x1, y1);

      if (x0 < minX) minX = x0; if (x0 > maxX) maxX = x0;
      if (y0 < minY) minY = y0; if (y0 > maxY) maxY = y0;
      if (x1 < minX) minX = x1; if (x1 > maxX) maxX = x1;
      if (y1 < minY) minY = y1; if (y1 > maxY) maxY = y1;
    }

    this.paths = paths;
    this.graph = new RouteGraph(network);
    this.routeCache.clear();
    this.vehiclesDirty = true; // nowe trasy - pojazdy przeskakują na nie bez animacji
    if (network.edges.length) this.bbox = { minX, minY, maxX, maxY };
    if (!this.fitted) this.fit();
    this.requestDraw();
  }

  setDynamic(dynamic) {
    this.towns = (dynamic.towns ?? []).filter((t) => t.pos).map((t) => ({
      kind: "town", id: t.id, data: t, px: t.pos[0], py: -t.pos[1],
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
      outputs: [...industryOutputs(i).keys()].map(cargoImg).filter(Boolean),
    }));

    // Stacja wbudowana w zakład: na mapie jest już ikonka zakładu, więc stacji nie rysujemy.
    const stationIndustry = industryOfStation(dynamic);
    this.stations = (dynamic.stations ?? []).filter((s) => s.pos && !stationIndustry.has(s.id)).map((s) => ({
      kind: "station", id: s.id, data: s, px: s.pos[0], py: -s.pos[1], type: s.kind,
    }));

    if (!this.bbox && this.towns.length) {
      const xs = this.towns.map((t) => t.px), ys = this.towns.map((t) => t.py);
      this.bbox = { minX: Math.min(...xs), minY: Math.min(...ys), maxX: Math.max(...xs), maxY: Math.max(...ys) };
      if (!this.fitted) this.fit();
    }
    this.requestDraw();
  }

  // Obrazek terenu z Tf3Bridge (terrain.png + terrain.json z zakresem w świecie gry; północ u góry).
  setTerrain({ info, image }) {
    this.terrain = { image, x: info.minX, y: -info.maxY, w: info.maxX - info.minX, h: info.maxY - info.minY };
    if (!this.bbox) {
      this.bbox = { minX: info.minX, minY: -info.maxY, maxX: info.maxX, maxY: -info.minY };
      if (!this.fitted) this.fit();
    }
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

  toggleVehicles() {
    this.showVehicles = !this.showVehicles;
    this.requestDraw();
    return this.showVehicles;
  }

  // Obiekt wskazany myszą na liście (kind = null - żaden): podświetlony na mapie, reszta przygaszona.
  // Linia - tylko wśród linii z ekranu (linesView); miasto, zakład, stacja - gdy jest na ekranie.
  hoverItem(kind, id) {
    if (kind === (this.listHover?.kind ?? null) && id === this.listHover?.id) return;
    this.listHover = kind ? { kind, id } : null;
    this.requestDraw();
  }

  // Zakładka Linie z włączonym "okiem": { match(linia) -> bool } (filtry panelu) albo null - wyłączone.
  setLinesView(view) {
    this.linesView = view;
    if (!view) this.visibleIds = new Set();
    if (!view && this.listHover?.kind === "line") this.listHover = null;
    this.visibleKey = null; // po zmianie filtrów zgłoś zestaw linii na nowo
    this.requestDraw();
  }

  // ---------------------------------------------------------------- widok
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
    // Dane mogły przyjść, zanim canvas dostał rozmiar - dopasuj widok, gdy już go ma.
    if (!this.fitted) this.fit();
    // Zmiana rozmiaru czyści canvas, a ResizeObserver działa już po klatkach rAF -
    // rysuj od razu, inaczej przeglądarka pokaże pustą klatkę (migotanie przy przeciąganiu panelu).
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
    const items = { town: this.towns, industry: this.industries, station: this.stations }[kind] ?? [];
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
    this.selected = { kind, id };
    this.view.scale = Math.max(this.view.scale, 0.25);
    this.centerOn(item.px, item.py);
  }

  // Punkt świata na środku mapy w poziomie i na wysokości focusAnchorY w pionie.
  centerOn(x, y) {
    this.view.x = x;
    this.view.y = y + (0.5 - this.focusAnchorY()) * this.h / this.view.scale;
    this.requestDraw();
  }

  // Przystanek zaznaczonej linii: centrujemy na nim i obrysowujemy go do zmiany wyboru.
  focusStop(lineId, index) {
    const pos = this.lines.get(lineId)?.stops?.[index]?.pos;
    if (!pos) return;
    this.selected = { kind: "line", id: lineId };
    this.focusedStop = { x: pos[0], y: -pos[1] };
    this.view.scale = Math.max(this.view.scale, 0.5);
    this.centerOn(this.focusedStop.x, this.focusedStop.y);
  }

  select(kind, id) {
    this.focusedStop = null;
    this.selected = kind ? { kind, id } : null;
    this.requestDraw();
  }

  toScreen(px, py) {
    return [(px - this.view.x) * this.view.scale + this.w / 2, (py - this.view.y) * this.view.scale + this.h / 2];
  }

  toWorld(sx, sy) {
    return [(sx - this.w / 2) / this.view.scale + this.view.x, (sy - this.h / 2) / this.view.scale + this.view.y];
  }

  // ---------------------------------------------------------------- rysowanie
  requestDraw() {
    if (this.frame) return;
    this.frame = requestAnimationFrame(() => {
      this.frame = 0;
      this.draw();
    });
  }

  draw() {
    const { ctx, dpr, w, h, view } = this;
    const css = (name) => this.theme[name];
    this.theme = readTheme();
    const now = performance.now();
    this.vehicles = this.showVehicles ? this.positionVehicles(now) : [];
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.fillStyle = css("--map-bg");
    ctx.fillRect(0, 0, w, h);

    if (this.terrain && this.showTerrain) {
      const s = view.scale, t = this.terrain;
      ctx.setTransform(s * dpr, 0, 0, s * dpr, (w / 2 - view.x * s) * dpr, (h / 2 - view.y * s) * dpr);
      ctx.imageSmoothingEnabled = true;
      ctx.imageSmoothingQuality = "high";
      ctx.drawImage(t.image, t.x, t.y, t.w, t.h);
    }

    if (this.paths) {
      const s = view.scale;
      ctx.setTransform(s * dpr, 0, 0, s * dpr, (w / 2 - view.x * s) * dpr, (h / 2 - view.y * s) * dpr);
      ctx.lineCap = "round";
      ctx.lineJoin = "round";
      const px = 1 / s; // 1 piksel ekranu w jednostkach świata

      const stroke = (path, color, width, dash) => {
        ctx.strokeStyle = color;
        ctx.lineWidth = width * px;
        ctx.setLineDash(dash ? dash.map((d) => d * px) : []);
        ctx.stroke(path);
      };
      stroke(this.paths.streetTunnel, css("--map-street-tunnel"), 1, [3, 3]);
      stroke(this.paths.street, css("--map-street"), Math.max(1, Math.min(4, 6 * s)));
      stroke(this.paths.trackTunnel, css("--map-track-tunnel"), 1.5, [4, 3]);
      stroke(this.paths.track, css("--map-track"), Math.max(1.5, Math.min(5, 8 * s)));
      ctx.setLineDash([]);
    }

    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    // Punkt wskazany na liście albo linia wskazana w panelu lub przez pojazd na mapie (wtedy razem
    // z zaznaczonym obiektem, np. miastem): reszta półprzezroczysta, wskazane na końcu, w pełnym kolorze.
    // Pojazdy linii wskazanej albo zaznaczonej rysujemy na końcu, pozostałe przygaszone.
    const hotLine = this.hotLine();
    const spotLine = this.spotLine(hotLine);
    const spot = this.spotItem(spotLine === null ? this.listHover : this.selected);
    const focusLine = hotLine ?? (this.selected?.kind === "line" ? this.selected.id : null);
    ctx.globalAlpha = spot || spotLine !== null ? DIM : 1;
    if (this.linesView) this.drawVisibleLines(hotLine);
    else if (this.selected?.kind === "line") this.drawLine(this.selected.id);
    if (this.showIndustries) this.drawIndustries(spot);
    if (this.showStations) this.drawStations(spot);
    // pojazdy innych linii przygaszone raz (gdy reszta mapy już jest przygaszona - bez dodatkowego DIM)
    if (this.showVehicles) this.drawVehicles((v) => v.line !== focusLine, focusLine === null || ctx.globalAlpha < 1 ? 1 : DIM);
    this.drawTowns(spot);
    ctx.globalAlpha = 1;
    if (spotLine !== null) this.drawLine(spotLine);
    if (this.showVehicles && focusLine !== null) this.drawVehicles((v) => v.line === focusLine);
    if (spot?.kind === "industry") this.drawIndustry(spot);
    else if (spot?.kind === "station") this.drawStation(spot);
    else if (spot?.kind === "town") this.drawTown(spot, null);
    this.drawPin();

    if (this.showVehicles && now < this.animUntil && !this.animTimer) {
      this.animTimer = setTimeout(() => {
        this.animTimer = 0;
        this.requestDraw();
      }, ANIM_FRAME_MS);
    }
  }

  // Linia wskazana: na liście w panelu albo przez pojazd pod kursorem na mapie; inaczej null.
  hotLine() {
    const h = this.listHover?.kind === "line" ? this.listHover.id : this.hovered?.kind === "vehicle" ? this.hovered.line : null;
    return h != null && this.lines.has(h) ? h : null;
  }

  // Wskazana linia, której nie podświetla już drawVisibleLines (ta robi to na zakładce Linie z okiem,
  // bez zaznaczonej linii, o ile linia jest wśród narysowanych); inaczej null.
  spotLine(hot) {
    if (hot === null) return null;
    return this.linesView && this.selected?.kind !== "line" && this.visibleIds.has(hot) ? null : hot;
  }

  // Miasto / zakład / stacja ({ kind, id }), o ile jest na ekranie; inaczej null.
  spotItem(h) {
    const items = { town: this.towns, industry: this.industries, station: this.stations }[h?.kind];
    const item = items?.find((x) => x.id === h.id);
    if (!item) return null;
    const [x, y] = this.toScreen(item.px, item.py);
    return x >= 0 && y >= 0 && x <= this.w && y <= this.h ? item : null;
  }

  // ---------------------------------------------------------------- pinezka (link do miejsca)
  // pin = { x, y, note } we współrzędnych gry (y na północ) albo null.
  setPin(pin) {
    this.pin = pin;
    this.requestDraw();
  }

  // Centruje widok na pinezce; scale z linku, a bez niego - przybliżenie jak przy wyborze miasta.
  showPin(scale) {
    if (!this.pin) return;
    this.view.x = this.pin.x;
    this.view.y = -this.pin.y;
    this.view.scale = scale ?? Math.max(this.view.scale, 0.25);
    this.fitted = true; // dane przychodzące później nie mogą przestawić widoku na całą mapę
    this.requestDraw();
  }

  setPinMode(on) {
    this.pinMode = on;
    this.canvas.style.cursor = on ? "crosshair" : "grab";
    this.onPinMode?.(on);
  }

  // Szpilka: główka w kolorze akcentu nad punktem, pod nią opis (skrócony) w dymku.
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

    const note = truncate((this.pin.note ?? "").replace(/\s+/g, " ").trim(), 48); // pełny opis w tooltipie
    if (!note) return;
    ctx.font = `600 12px ${css("--font")}`;
    ctx.textBaseline = "middle";
    const w = ctx.measureText(note).width + 14;
    const bx = x + 14, by = hy - 11;
    ctx.fillStyle = css("--panel");
    ctx.strokeStyle = css("--pin");
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.roundRect(bx, by, w, 22, 6);
    ctx.fill();
    ctx.stroke();
    ctx.fillStyle = css("--town-label");
    ctx.fillText(note, bx + 7, hy);
  }

  // Trasa linii liczona leniwie i trzymana do zmiany przystanków / sieci.
  lineRoute(id) {
    const line = this.lines.get(id);
    if (!line || !this.graph) return null;
    const key = JSON.stringify([line.carrier, (line.stops ?? []).map((s) => s.pos)]);
    const cached = this.routeCache.get(id);
    if (cached?.key === key) return cached.route;
    const route = this.graph.routeLine(line);
    this.routeCache.set(id, { key, route });
    return route;
  }

  // Wszystkie linie przecinające ekran (i pasujące do filtrów panelu); zaznaczona linia - tylko ona.
  // Zestaw linii na ekranie zgłaszamy panelowi (lista linii), gdy się zmieni.
  // hotLine: linia wskazana (na liście albo przez pojazd) - na wierzchu, szersza, reszta przygaszona.
  drawVisibleLines(hotLine) {
    const [minX, minY] = this.toWorld(0, 0);
    const [maxX, maxY] = this.toWorld(this.w, this.h);
    const rect = { minX, minY, maxX, maxY };
    const visible = [...this.lines.values()]
      .filter((l) => this.linesView.match(l) && routeInRect(this.lineRoute(l.id), rect))
      .sort((a, b) => (a.vehicleCount ?? 0) - (b.vehicleCount ?? 0)); // najbardziej obłożone na wierzchu

    // Linia wskazana: pozostałe półprzezroczyste, ona na wierzchu i 50% szersza.
    this.visibleIds = new Set(visible.map((l) => l.id));
    const hot = hotLine !== null && this.visibleIds.has(hotLine) ? hotLine : null;
    if (this.selected?.kind === "line") this.drawLine(this.selected.id);
    else {
      const alpha = this.ctx.globalAlpha; // już przygaszone, gdy wskazano miasto / zakład / stację
      this.ctx.globalAlpha = hot === null ? alpha : DIM;
      for (const l of visible) if (l.id !== hot) this.drawLine(l.id, true);
      this.ctx.globalAlpha = alpha;
      if (hot !== null) this.drawLine(hot, true, 1.5);
    }

    const key = visible.map((l) => l.id).sort((a, b) => a - b).join(",");
    if (key !== this.visibleKey) {
      this.visibleKey = key;
      this.onVisibleLines?.(new Set(this.visibleIds));
    }
  }

  // Linia na wierzchu: obrys w kolorze tła, trasa w kolorze linii, przystanki jako kółka.
  // compact: cieńsza, do rysowania wielu linii naraz; przystanki dopiero po przybliżeniu.
  // widen: mnożnik grubości trasy (podświetlenie linii wskazanej na liście).
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
    ctx.lineWidth = ((compact ? 2.5 : 4) * widen + (compact ? 2 : 3)) / s; // obrys wokół trasy
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
    if (this.focusedStop && !compact) {
      const [x, y] = this.toScreen(this.focusedStop.x, this.focusedStop.y);
      this.ring(x, y, 10);
    }
  }

  // skip: obiekt rysowany osobno (wskazany na liście) - pomijany tutaj.
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
    // jak ikony stacji: tylko po przybliżeniu
    if (this.view.scale >= STATION_ICON_SCALE) this.drawOutputs(ind.outputs, x, y - size / 2 - 3);
  }

  // Ikonki produkowanych ładunków w rzędzie, wyśrodkowane nad kwadratem zakładu (bottomY = dół rzędu).
  drawOutputs(srcs, x, bottomY) {
    const s = OUTPUT_ICON_PX, gap = 1;
    const imgs = srcs.map((src) => this.image(src)).filter(Boolean);
    let left = x - (imgs.length * s + (imgs.length - 1) * gap) / 2;
    for (const img of imgs) {
      this.ctx.drawImage(img, left, bottomY - s, s, s);
      left += s + gap;
    }
  }

  // Przybliżone: ciemne kółko z obwódką w kolorze rodzaju i ikoną; oddalone: sama kropka.
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
      } else { // obrazek jeszcze się ładuje albo go nie ma
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
  }

  // ---------------------------------------------------------------- pojazdy
  // Ruch pojazdu między paczkami danych (wpis w vehicleAnim), od t0 przez dur:
  //   * mode "route" - wzdłuż pętli trasy linii: odległość from -> from + delta,
  //   * mode "line"  - po prostej (x0, y0) -> (x1, y1); route + d: ten punkt na trasie, jeśli na niej leży.
  // Z każdą paczką pojazd jedzie z miejsca, w którym jest na ekranie, do nowej pozycji przez updateMs,
  // czyli do następnej paczki. To interpolacja: obraz jest o jedną paczkę opóźniony, ale ruch jest płynny.
  //
  // Pozycja z moda (v.pos) - prawdziwa; po trasie, gdy leży przy odcinku dojazdu do v.stop, inaczej
  // po prostej (statki, samoloty, objazdy). Bez pos (stary eksport albo pozycje wyłączone w modzie)
  // zgadujemy z numeru przystanku (retargetGuessed).
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

      // Po trasie, jeśli poprzednia pozycja też na niej była i ruch jest do przodu, nie dłuższy niż
      // 3x prosta (inaczej to np. zawrócenie albo trasa z A* inna niż w grze - wtedy po prostej).
      const from = onRoute ? this.distanceOn(a, route, now) : null;
      if (from != null) {
        let delta = loopDelta(proj.d - from, route.length);
        if (delta < 0 && delta > -ON_ROUTE_M) delta = 0; // drgania pozycji
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

  // Bez pozycji z moda: AT_TERMINAL - na przystanku stop; EN_ROUTE - na trasie od poprzedniego
  // przystanku do stop, kilka pojazdów na jednym odcinku rozłożonych równo. Na długim odcinku pojazd
  // stoi, dopóki nie dojedzie do przystanku - stąd pozycje w modzie. -> czy coś jedzie.
  retargetGuessed(vehicles, prev, next, now) {
    const groups = new Map(); // linia|przystanek|stan -> pojazdy
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
      if (!leg) { // bez trasy (np. sieć jeszcze nie przyszła): na przystanku, bez ruchu
        const pos = this.lines.get(lineId).stops?.[stop]?.pos;
        if (!pos) continue;
        for (const v of vs) {
          next.set(v.id, { v, line: lineId, mode: "line", x0: pos[0], y0: -pos[1], x1: pos[0], y1: -pos[1], t0: now, dur: 1 });
        }
        continue;
      }
      const L = route.length;
      const was = (v) => this.distanceOn(prev.get(v.id), route, now);
      // Na odcinku pojazdy w kolejności, w jakiej jadą (nowe na początku), żeby się nie mijały.
      if (state === "EN_ROUTE" && vs.length > 1) {
        const order = (v) => (was(v) == null ? -Infinity : loopDelta(was(v) - leg.start, L));
        vs.sort((a, b) => order(a) - order(b) || a.id - b.id);
      }
      vs.forEach((v, k) => {
        const target = state === "EN_ROUTE" ? leg.start + (leg.end - leg.start) * (k + 1) / (vs.length + 1) : leg.end;
        const from = was(v);
        // Zawsze do przodu po pętli. Cel za pojazdem bierze się z równego rozkładu (np. drugi pojazd
        // zjechał z odcinka, więc ten "spada" z 2/3 na 1/2) - wtedy pojazd jedzie dalej, do połowy
        // drogi, która mu została do przystanku.
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

  // Pozycja pojazdu na ekranie w chwili now (układ ścieżki).
  animPoint(a, now) {
    if (a.mode === "route") return pointAtDistance(a.route, this.animDistance(a, now));
    const f = this.animProgress(a, now);
    return { x: a.x0 + (a.x1 - a.x0) * f, y: a.y0 + (a.y1 - a.y0) * f };
  }

  // Odległość wzdłuż trasy route, w której jest pojazd (wpis a), albo null, gdy nie jest na tej trasie.
  distanceOn(a, route, now) {
    if (!a || a.route !== route) return null;
    return a.mode === "route" ? this.animDistance(a, now) : a.d;
  }

  // Pojazdy z pozycją w chwili now (układ ścieżki).
  positionVehicles(now) {
    if (this.vehiclesDirty) this.retargetVehicles(now);
    const out = [];
    for (const a of this.vehicleAnim.values()) {
      const p = this.animPoint(a, now);
      out.push({ kind: "vehicle", id: a.v.id, data: a.v, line: a.line, px: p.x, py: p.y });
    }
    return out;
  }

  // filter: które pojazdy rysować; alpha: dodatkowe przygaszenie (pojazdy innych linii niż wskazana).
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

  // Obrazek z cache; ładowany przy pierwszym użyciu (po wczytaniu przerysowujemy). null, dopóki
  // się nie wczyta - albo na stałe, gdy pliku nie ma.
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

  // Ikonka z gry to biała maska: barwimy ją raz na kolor i rozmiar (w pikselach urządzenia)
  // na osobnym canvasie i trzymamy w cache. null, dopóki obrazek się nie wczyta.
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
    const labels = []; // etykiety: od największych miast, bez nakładania się
    for (const t of this.towns) if (t !== skip) this.drawTown(t, labels);
  }

  // labels: ramki już narysowanych etykiet (null - etykieta zawsze, np. miasto wskazane na liście).
  drawTown(t, labels) {
    const css = (name) => this.theme[name];
    const { ctx } = this;
    const [x, y] = this.toScreen(t.px, t.py);
    if (x < -50 || y < -50 || x > this.w + 50 || y > this.h + 50) return;
    ctx.fillStyle = css("--town");
    ctx.strokeStyle = css("--map-bg");
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.arc(x, y, t.r, 0, Math.PI * 2);
    ctx.fill();
    ctx.stroke();
    const marked = !labels || this.isMarked(t);
    if (marked) this.ring(x, y, t.r * 2);

    ctx.font = `600 12px ${css("--font")}`;
    ctx.textBaseline = "middle";
    const text = t.data.name ?? "";
    const box = { x: x + t.r + 4, y: y - 8, w: ctx.measureText(text).width + 6, h: 16 };
    if (labels && !marked && labels.some((b) => overlaps(b, box))) return;
    labels?.push(box);
    ctx.lineWidth = 3;
    ctx.strokeStyle = css("--map-bg");
    ctx.strokeText(text, box.x, y);
    ctx.fillStyle = marked ? css("--accent") : css("--town-label");
    ctx.fillText(text, box.x, y);
  }

  isMarked(item) {
    const m = (s) => s && s.kind === item.kind && s.id === item.id;
    return m(this.selected) || m(this.hovered) || m(this.listHover);
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

  // ---------------------------------------------------------------- interakcja
  hitTest(sx, sy) {
    let best = null, bestD = Infinity;
    const consider = (item, radius) => {
      const [x, y] = this.toScreen(item.px, item.py);
      const d = Math.hypot(x - sx, y - sy);
      if (d <= radius && d < bestD) { best = item; bestD = d; }
    };
    for (const t of this.towns) consider(t, t.r + 4);
    if (this.showIndustries) for (const i of this.industries) consider(i, 8);
    if (this.showStations) {
      const r = this.view.scale >= STATION_ICON_SCALE ? 11 : 6;
      for (const s of this.stations) consider(s, r);
    }
    for (const v of this.vehicles) consider(v, 6); // pozycje z ostatniej klatki - tam, gdzie je widać
    // pinezka na wierzchu: łapiemy jej główkę (PIN_HEAD_Y px nad wskazanym punktem)
    if (this.pin) {
      const [x, y] = this.toScreen(this.pin.x, -this.pin.y);
      if (Math.hypot(x - sx, y - PIN_HEAD_Y - sy) <= 12) return { kind: "pin", id: "pin", data: this.pin };
    }
    return best;
  }

  // Miejsce kliknięcia -> współrzędne gry (x, y; y na północ) dla onPlace.
  place(e) {
    const rect = this.canvas.getBoundingClientRect();
    const sx = e.clientX - rect.left, sy = e.clientY - rect.top;
    const [px, py] = this.toWorld(sx, sy);
    this.setPinMode(false);
    this.onPlace?.({ x: Math.round(px), y: Math.round(-py), sx, sy });
  }

  // Przybliżenie do scale z punktem świata (wx, wy) pod punktem ekranu (sx, sy).
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
    const pointers = new Map(); // pointerId -> [sx, sy] (dotyk: szczypanie dwoma palcami)
    let pinch = null;           // { dist, wx, wy, scale } z chwili położenia drugiego palca

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
      this.zoomAt(wx, wy, sx, sy, this.view.scale * Math.exp(-e.deltaY * 0.0015)); // punkt pod kursorem zostaje
    }, { passive: false });

    // Prawy przycisk stawia pinezkę (zamiast menu przeglądarki).
    c.addEventListener("contextmenu", (e) => {
      e.preventDefault();
      this.place(e);
    });

    c.addEventListener("pointerdown", (e) => {
      if (e.button !== 0) return;
      pointers.set(e.pointerId, local(e));
      if (pointers.size === 2) startPinch();
      else if (pointers.size === 1) drag = { x: e.clientX, y: e.clientY, vx: this.view.x, vy: this.view.y, moved: false };
      try { c.setPointerCapture(e.pointerId); } catch { /* wskaźnik już nieaktywny */ }
    });

    c.addEventListener("pointermove", (e) => {
      const [sx, sy] = local(e);
      if (pointers.has(e.pointerId)) pointers.set(e.pointerId, [sx, sy]);
      if (pinch && pointers.size >= 2) {
        // punkt świata spod środka palców idzie za środkiem palców: zoom i przesuwanie naraz
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
      if (pinch) return; // po szczypaniu został jeden palec - bez hovera, aż go podniesie
      const hit = this.hitTest(sx, sy);
      const changed = (hit?.id ?? null) !== (this.hovered?.id ?? null);
      this.hovered = hit ? { kind: hit.kind, id: hit.id, line: hit.line } : null; // line: tylko pojazd
      c.style.cursor = this.pinMode ? "crosshair" : hit ? "pointer" : "grab";
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
      if (this.pinMode) {
        this.place(e);
        return;
      }
      const hit = this.hitTest(...local(e));
      if (hit?.kind === "pin") return; // pinezka ma tylko tooltip
      // pojazd: zaznaczamy jego linię
      const sel = hit?.kind === "vehicle" ? { kind: "line", id: hit.line, data: this.lines.get(hit.line) } : hit;
      this.select(sel?.kind, sel?.id);
      this.onSelect?.(sel);
    });

    c.addEventListener("pointerleave", () => {
      this.hovered = null;
      this.onHover?.(null);
      this.requestDraw();
    });
  }
}

// Różnica odległości na pętli długości L sprowadzona do (-L/4, 3L/4]: ruch do przodu albo mały wstecz.
function loopDelta(d, L) {
  const m = ((d % L) + L) % L;
  return m > L * 0.75 ? m - L : m;
}

const truncate = (s, n) => (s.length > n ? `${s.slice(0, n - 1)}…` : s);

const overlaps = (a, b) => a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;
