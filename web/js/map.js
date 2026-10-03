// Mapa: sieć (Path2D w układzie świata), miasta i zakłady (w układzie ekranu), pan/zoom, hover, wybór.
// Układ "ścieżki" = (x, -y) ze świata gry, bo w grze oś y rośnie na północ, a na ekranie w dół.

import { cargoImg, industryOfStation, rgb, stationImg, townSize } from "./format.js";
import { RouteGraph, routeInRect } from "./route.js";

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

// Kolory z CSS (motyw) odczytywane raz na klatkę, nie dla każdego obiektu.
function readTheme() {
  const style = getComputedStyle(document.documentElement);
  return Object.fromEntries(COLOR_VARS.map((v) => [v, style.getPropertyValue(v).trim()]));
}

export class MapView {
  constructor(canvas, { onHover, onSelect, onPlace, onPinMode, onVisibleLines } = {}) {
    this.canvas = canvas;
    this.ctx = canvas.getContext("2d");
    this.onHover = onHover;
    this.onSelect = onSelect;
    this.onPlace = onPlace;     // kliknięcie w trybie pinezki / prawy przycisk -> { x, y, sx, sy }
    this.onPinMode = onPinMode;
    this.onVisibleLines = onVisibleLines; // Set id linii na ekranie (tylko przy włączonym linesView)
    this.pin = null;            // { x, y, note } - pinezka z linku albo właśnie stawiana
    this.pinMode = false;

    this.view = { x: 0, y: 0, scale: 0.02 };
    this.paths = null;          // { street, streetTunnel, track, trackTunnel }
    this.bbox = null;           // zakres sieci w układzie ścieżki
    this.towns = [];
    this.industries = [];
    this.stations = [];         // dworce, porty, lotniska (bez przystanków)
    this.showStations = true;
    this.iconImages = new Map(); // url -> Image (ikony z gry: maski stacji, ładunki)
    this.iconCache = new Map();  // url|kolor|px -> canvas z zabarwioną ikoną
    this.lines = new Map();     // id -> linia z dynamic.json
    this.graph = null;          // RouteGraph - do wyznaczania tras linii
    this.routeCache = new Map(); // id linii -> { key, route }
    this.linesView = null;      // { match(linia) } - zakładka Linie: rysuj wszystkie linie na ekranie
    this.visibleKey = null;     // ostatnio zgłoszony zestaw linii na ekranie (żeby nie zgłaszać co klatkę)
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
    this.canvas.width = Math.max(1, Math.round(width * dpr));
    this.canvas.height = Math.max(1, Math.round(height * dpr));
    // Dane mogły przyjść, zanim canvas dostał rozmiar - dopasuj widok, gdy już go ma.
    if (!this.fitted) this.fit();
    this.requestDraw();
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
    if (kind === "station" && !this.showStations) this.showStations = true;
    this.selected = { kind, id };
    this.view.x = item.px;
    this.view.y = item.py;
    this.view.scale = Math.max(this.view.scale, 0.25);
    this.requestDraw();
  }

  // Przystanek zaznaczonej linii: centrujemy na nim i obrysowujemy go do zmiany wyboru.
  focusStop(lineId, index) {
    const pos = this.lines.get(lineId)?.stops?.[index]?.pos;
    if (!pos) return;
    this.selected = { kind: "line", id: lineId };
    this.focusedStop = { x: pos[0], y: -pos[1] };
    this.view.x = this.focusedStop.x;
    this.view.y = this.focusedStop.y;
    this.view.scale = Math.max(this.view.scale, 0.5);
    this.requestDraw();
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
    // Punkt wskazany na liście: reszta półprzezroczysta, on na końcu, w pełnym kolorze.
    const spot = this.spotItem();
    ctx.globalAlpha = spot ? DIM : 1;
    if (this.linesView) this.drawVisibleLines();
    else if (this.selected?.kind === "line") this.drawLine(this.selected.id);
    this.drawIndustries(spot);
    if (this.showStations) this.drawStations(spot);
    this.drawTowns(spot);
    ctx.globalAlpha = 1;
    if (spot?.kind === "industry") this.drawIndustry(spot);
    else if (spot?.kind === "station") this.drawStation(spot);
    else if (spot?.kind === "town") this.drawTown(spot, null);
    this.drawPin();
  }

  // Miasto / zakład / stacja wskazane na liście, o ile jest na ekranie; inaczej null.
  spotItem() {
    const h = this.listHover;
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
  drawVisibleLines() {
    const [minX, minY] = this.toWorld(0, 0);
    const [maxX, maxY] = this.toWorld(this.w, this.h);
    const rect = { minX, minY, maxX, maxY };
    const visible = [...this.lines.values()]
      .filter((l) => this.linesView.match(l) && routeInRect(this.lineRoute(l.id), rect))
      .sort((a, b) => (a.vehicleCount ?? 0) - (b.vehicleCount ?? 0)); // najbardziej obłożone na wierzchu

    // Linia wskazana na liście: pozostałe półprzezroczyste, ona na wierzchu i 50% szersza.
    const h = this.listHover;
    const hot = h?.kind === "line" && visible.some((l) => l.id === h.id) ? h.id : null;
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
      this.onVisibleLines?.(new Set(visible.map((l) => l.id)));
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
    for (const i of this.industries) consider(i, 8);
    if (this.showStations) {
      const r = this.view.scale >= STATION_ICON_SCALE ? 11 : 6;
      for (const s of this.stations) consider(s, r);
    }
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

  bindEvents() {
    const c = this.canvas;
    let drag = null;

    c.addEventListener("wheel", (e) => {
      e.preventDefault();
      const rect = c.getBoundingClientRect();
      const sx = e.clientX - rect.left, sy = e.clientY - rect.top;
      const [wx, wy] = this.toWorld(sx, sy);
      const factor = Math.exp(-e.deltaY * 0.0015);
      this.view.scale = Math.max(0.005, Math.min(20, this.view.scale * factor));
      // punkt pod kursorem zostaje w miejscu
      this.view.x = wx - (sx - this.w / 2) / this.view.scale;
      this.view.y = wy - (sy - this.h / 2) / this.view.scale;
      this.requestDraw();
    }, { passive: false });

    // Prawy przycisk stawia pinezkę (zamiast menu przeglądarki).
    c.addEventListener("contextmenu", (e) => {
      e.preventDefault();
      this.place(e);
    });

    c.addEventListener("pointerdown", (e) => {
      if (e.button !== 0) return;
      drag = { x: e.clientX, y: e.clientY, vx: this.view.x, vy: this.view.y, moved: false };
      c.setPointerCapture(e.pointerId);
    });

    c.addEventListener("pointermove", (e) => {
      const rect = c.getBoundingClientRect();
      if (drag) {
        const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
        if (Math.abs(dx) + Math.abs(dy) > 3) drag.moved = true;
        this.view.x = drag.vx - dx / this.view.scale;
        this.view.y = drag.vy - dy / this.view.scale;
        this.requestDraw();
        return;
      }
      const hit = this.hitTest(e.clientX - rect.left, e.clientY - rect.top);
      const changed = (hit?.id ?? null) !== (this.hovered?.id ?? null);
      this.hovered = hit ? { kind: hit.kind, id: hit.id } : null;
      c.style.cursor = this.pinMode ? "crosshair" : hit ? "pointer" : "grab";
      this.onHover?.(hit, e.clientX - rect.left, e.clientY - rect.top);
      if (changed) this.requestDraw();
    });

    c.addEventListener("pointerup", (e) => {
      const wasClick = drag && !drag.moved;
      drag = null;
      if (!wasClick) return;
      if (this.pinMode) {
        this.place(e);
        return;
      }
      const rect = c.getBoundingClientRect();
      const hit = this.hitTest(e.clientX - rect.left, e.clientY - rect.top);
      if (hit?.kind === "pin") return; // pinezka ma tylko tooltip
      this.select(hit?.kind, hit?.id);
      this.onSelect?.(hit);
    });

    c.addEventListener("pointerleave", () => {
      this.hovered = null;
      this.onHover?.(null);
      this.requestDraw();
    });
  }
}

const truncate = (s, n) => (s.length > n ? `${s.slice(0, n - 1)}…` : s);

const overlaps = (a, b) => a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;
