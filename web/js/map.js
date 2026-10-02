// Mapa: sieć (Path2D w układzie świata), miasta i zakłady (w układzie ekranu), pan/zoom, hover, wybór.
// Układ "ścieżki" = (x, -y) ze świata gry, bo w grze oś y rośnie na północ, a na ekranie w dół.

import { townSize } from "./format.js";

const RAW_INDUSTRIES = new Set([
  "coal_mine", "iron_ore_mine", "quarry", "forest", "farm", "oil_well",
  "oil_platform", "fishing_grounds", "sand_excavator",
]);

export const industryKind = (ind) => {
  const key = String(ind.file ?? "").split("/").pop().replace(/\.con$/, "");
  return RAW_INDUSTRIES.has(key) ? "raw" : "processing";
};

const COLOR_VARS = ["--map-bg", "--map-street", "--map-street-tunnel", "--map-track", "--map-track-tunnel",
  "--industry-raw", "--industry-processing", "--town", "--town-label", "--accent", "--font"];

// Kolory z CSS (motyw) odczytywane raz na klatkę, nie dla każdego obiektu.
function readTheme() {
  const style = getComputedStyle(document.documentElement);
  return Object.fromEntries(COLOR_VARS.map((v) => [v, style.getPropertyValue(v).trim()]));
}

export class MapView {
  constructor(canvas, { onHover, onSelect } = {}) {
    this.canvas = canvas;
    this.ctx = canvas.getContext("2d");
    this.onHover = onHover;
    this.onSelect = onSelect;

    this.view = { x: 0, y: 0, scale: 0.02 };
    this.paths = null;          // { street, streetTunnel, track, trackTunnel }
    this.bbox = null;           // zakres sieci w układzie ścieżki
    this.towns = [];
    this.industries = [];
    this.selected = null;       // { kind, id }
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
    if (network.edges.length) this.bbox = { minX, minY, maxX, maxY };
    if (!this.fitted) this.fit();
    this.requestDraw();
  }

  setDynamic(dynamic) {
    this.towns = (dynamic.towns ?? []).filter((t) => t.pos).map((t) => ({
      kind: "town", id: t.id, data: t, px: t.pos[0], py: -t.pos[1],
      r: Math.max(4, Math.min(14, 2 + Math.sqrt(townSize(t)) * 0.4)),
    })).sort((a, b) => b.r - a.r);

    this.industries = (dynamic.industries ?? []).filter((i) => i.pos).map((i) => ({
      kind: "industry", id: i.id, data: i, px: i.pos[0], py: -i.pos[1], type: industryKind(i),
    }));

    if (!this.bbox && this.towns.length) {
      const xs = this.towns.map((t) => t.px), ys = this.towns.map((t) => t.py);
      this.bbox = { minX: Math.min(...xs), minY: Math.min(...ys), maxX: Math.max(...xs), maxY: Math.max(...ys) };
      this.fit();
    }
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

  fit() {
    if (!this.bbox || !(this.w > 0) || !(this.h > 0)) return;
    const { minX, minY, maxX, maxY } = this.bbox;
    this.view.x = (minX + maxX) / 2;
    this.view.y = (minY + maxY) / 2;
    this.view.scale = Math.min(this.w / (maxX - minX || 1), this.h / (maxY - minY || 1)) * 0.92;
    this.fitted = true;
    this.requestDraw();
  }

  focus(kind, id) {
    const item = (kind === "town" ? this.towns : this.industries).find((x) => x.id === id);
    if (!item) return;
    this.selected = { kind, id };
    this.view.x = item.px;
    this.view.y = item.py;
    this.view.scale = Math.max(this.view.scale, 0.25);
    this.requestDraw();
  }

  select(kind, id) {
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
    this.drawIndustries();
    this.drawTowns();
  }

  drawIndustries() {
    const css = (name) => this.theme[name];
    const { ctx } = this;
    const size = this.view.scale > 0.08 ? 7 : 5;
    const colors = { raw: css("--industry-raw"), processing: css("--industry-processing") };
    for (const ind of this.industries) {
      const [x, y] = this.toScreen(ind.px, ind.py);
      if (x < -10 || y < -10 || x > this.w + 10 || y > this.h + 10) continue;
      ctx.fillStyle = colors[ind.type];
      ctx.strokeStyle = css("--map-bg");
      ctx.lineWidth = 1.5;
      ctx.beginPath();
      ctx.rect(x - size / 2, y - size / 2, size, size);
      ctx.fill();
      ctx.stroke();
      if (this.isMarked(ind)) this.ring(x, y, size);
    }
  }

  drawTowns() {
    const css = (name) => this.theme[name];
    const { ctx } = this;
    const labels = [];
    ctx.font = `600 12px ${css("--font")}`;
    ctx.textBaseline = "middle";

    for (const t of this.towns) {
      const [x, y] = this.toScreen(t.px, t.py);
      if (x < -50 || y < -50 || x > this.w + 50 || y > this.h + 50) continue;
      ctx.fillStyle = css("--town");
      ctx.strokeStyle = css("--map-bg");
      ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.arc(x, y, t.r, 0, Math.PI * 2);
      ctx.fill();
      ctx.stroke();
      if (this.isMarked(t)) this.ring(x, y, t.r * 2);

      // etykiety: od największych miast, bez nakładania się
      const text = t.data.name ?? "";
      const tw = ctx.measureText(text).width;
      const box = { x: x + t.r + 4, y: y - 8, w: tw + 6, h: 16 };
      const marked = this.isMarked(t);
      if (marked || !labels.some((b) => overlaps(b, box))) {
        labels.push(box);
        ctx.lineWidth = 3;
        ctx.strokeStyle = css("--map-bg");
        ctx.strokeText(text, box.x, y);
        ctx.fillStyle = marked ? css("--accent") : css("--town-label");
        ctx.fillText(text, box.x, y);
      }
    }
  }

  isMarked(item) {
    const m = (s) => s && s.kind === item.kind && s.id === item.id;
    return m(this.selected) || m(this.hovered);
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
    return best;
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

    c.addEventListener("pointerdown", (e) => {
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
      c.style.cursor = hit ? "pointer" : "grab";
      this.onHover?.(hit, e.clientX - rect.left, e.clientY - rect.top);
      if (changed) this.requestDraw();
    });

    c.addEventListener("pointerup", (e) => {
      const wasClick = drag && !drag.moved;
      drag = null;
      if (!wasClick) return;
      const rect = c.getBoundingClientRect();
      const hit = this.hitTest(e.clientX - rect.left, e.clientY - rect.top);
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

const overlaps = (a, b) => a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;
