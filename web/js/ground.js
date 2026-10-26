// Terrain for building from Tf3Bridge: where a town will get built and which region each point is in.
//
// Both images have a pixel per heightmap sample, like terrain.png (north at the top, half a sample beyond the outermost ones):
//   terrain-site.png (Processing/TownSites.cs):    R = share of water, G = share of steep slopes in the town area
//                                                  (±250 m), B = 255 under water
//   terrain-zone.png (Processing/TerrainZones.cs): R = mountains, G = water within about 1 km, B = flat lowland
// Rules for industries (build-rules.json, Rewards/IndustryZones.cs): kind -> "mountains" | "nearWater" |
// "lowland" | "inland". Thresholds and logic as in C#; here only a hint, Tf3Bridge decides.

export const TOWN_MAX_WATER = 0.3, TOWN_MAX_STEEP = 0.5;
export const ON_WATER = new Set(["oil_platform", "fishing_grounds", "sand_excavator"]); // as in create.lua
const BLOCKED_RGBA = [220, 60, 50, 90]; // map overlay: building not allowed here

function pixels(bitmap) {
  const canvas = document.createElement("canvas");
  canvas.width = bitmap.width;
  canvas.height = bitmap.height;
  const g = canvas.getContext("2d", { willReadFrequently: true });
  g.drawImage(bitmap, 0, 0);
  return g.getImageData(0, 0, bitmap.width, bitmap.height).data;
}

export class Ground {
  /** sites, zones: ImageBitmap or null (an older Tf3Bridge); info: terrain.json. */
  constructor(info, sites, zones) {
    this.info = info;
    const size = sites ?? zones;
    this.w = size?.width ?? 0;
    this.h = size?.height ?? 0;
    this.sites = sites ? pixels(sites) : null;
    this.zones = zones && zones.width === this.w && zones.height === this.h ? pixels(zones) : null;
    this.masks = new Map();    // key ("town" or industry kind + zone) -> Uint8Array: 1 = allowed
    this.overlays = new Map(); // the same key -> canvas with the red areas
    this.zoneExists = new Map();
  }

  /** Index of the sample under (x, y) in game meters; -1 outside the map. */
  index(x, y) {
    const { minX, maxY, step } = this.info;
    const i = Math.floor((x - minX) / step), row = Math.floor((maxY - y) / step);
    return i < 0 || row < 0 || i >= this.w || row >= this.h ? -1 : row * this.w + i;
  }

  onWater(k) {
    return this.sites ? this.sites[k * 4 + 2] > 127 : false;
  }

  /** { water, steep, onWater } for a town at (x, y); null outside the map or without terrain-site.png. */
  site(x, y) {
    const k = this.index(x, y);
    if (k < 0 || !this.sites) return null;
    const d = this.sites, o = k * 4;
    return { water: d[o] / 255, steep: d[o + 1] / 255, onWater: d[o + 2] > 127 };
  }

  inZone(k, zone) {
    const o = k * 4, d = this.zones;
    switch (zone) {
      case "mountains": return d[o] > 127;
      case "nearWater": return d[o + 1] > 127;
      case "lowland": return d[o + 2] > 127;
      case "inland": return d[o + 1] <= 127;
      default: return true;
    }
  }

  /** Whether the region exists anywhere on land; when not (e.g. a map without mountains), it restricts nothing (like TerrainZones.Exists). */
  hasZone(zone) {
    if (!this.zones || !zone) return false;
    if (!this.zoneExists.has(zone)) {
      let found = false;
      for (let k = 0, n = this.w * this.h; k < n && !found; k++) found = !this.onWater(k) && this.inZone(k, zone);
      this.zoneExists.set(zone, found);
    }
    return this.zoneExists.get(zone);
  }

  /** The zone that really restricts this industry kind (null = none). */
  activeZone(zone) {
    return zone && zone !== "any" && this.hasZone(zone) ? zone : null;
  }

  // Whether sample k may be built on: a town (type null) or an industry of a given kind in zone zone.
  allowedAt(k, type, zone) {
    if (!type) {
      const o = k * 4, d = this.sites;
      return !(d[o + 2] > 127 || d[o] / 255 > TOWN_MAX_WATER || d[o + 1] / 255 > TOWN_MAX_STEEP);
    }
    if (this.sites && ON_WATER.has(type) !== this.onWater(k)) return false;
    return !zone || this.inZone(k, zone);
  }

  key(type, zone) {
    return type ? `${type}:${zone ?? ""}` : "town";
  }

  mask(type, zone) {
    const key = this.key(type, zone);
    if (!this.masks.has(key)) {
      const n = this.w * this.h, m = new Uint8Array(n);
      for (let k = 0; k < n; k++) m[k] = this.allowedAt(k, type, zone) ? 1 : 0;
      this.masks.set(key, m);
    }
    return this.masks.get(key);
  }

  /** Whether there's anything to restrict: a town needs terrain-site.png, an industry water or a region. */
  restricts(type, zone) {
    return type ? !!this.sites || !!zone : !!this.sites;
  }

  /** Canvas with red areas where building isn't allowed (null = nothing restricts). */
  overlay(type, zone) {
    if (!this.restricts(type, zone)) return null;
    const key = this.key(type, zone);
    if (!this.overlays.has(key)) {
      const m = this.mask(type, zone);
      const canvas = document.createElement("canvas");
      canvas.width = this.w;
      canvas.height = this.h;
      const g = canvas.getContext("2d");
      const img = g.createImageData(this.w, this.h);
      for (let k = 0; k < m.length; k++) if (!m[k]) img.data.set(BLOCKED_RGBA, k * 4);
      g.putImageData(img, 0, 0);
      this.overlays.set(key, canvas);
    }
    return this.overlays.get(key);
  }

  /** The nearest allowed spot: { dx, dy } in meters (y to the north); null when there's none. */
  nearestAllowed(x, y, type, zone) {
    const m = this.mask(type, zone);
    const { minX, maxY, step } = this.info;
    const ci = (x - minX) / step - 0.5, cr = (maxY - y) / step - 0.5;
    let best = -1, bestD = Infinity;
    for (let row = 0; row < this.h; row++) {
      const dr = (row - cr) ** 2;
      if (dr >= bestD) continue;
      const base = row * this.w;
      for (let i = 0; i < this.w; i++) {
        if (!m[base + i]) continue;
        const d = dr + (i - ci) ** 2;
        if (d < bestD) { bestD = d; best = base + i; }
      }
    }
    if (best < 0) return null;
    const i = best % this.w, row = Math.floor(best / this.w);
    return { dx: (i - ci) * step, dy: (cr - row) * step };
  }
}
