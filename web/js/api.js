// Downloading the data published by Tf3Bridge (latest/meta.json, dynamic.json, network.json, terrain.*,
// terrain-site.png, terrain-zone.png, build-rules.json).
// Polling: every pollMs we download the small meta.json; dynamic/network/terrain only when their seq changed.
// Polling stops in a hidden tab.

import { config } from "../config.js";

async function fetchJson(name) {
  const res = await fetch(config.dataBaseUrl + name, { cache: "no-store" });
  if (!res.ok) throw new Error(`${name}: HTTP ${res.status}`);
  return res.json();
}

function loadImage(url) {
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.onload = () => resolve(img);
    img.onerror = () => reject(new Error(`${url}: image load failed`));
    img.src = url;
  });
}

// An image with data (pixels read in JS): fetch instead of <img>, so an image from another domain (Blob Storage,
// CORS) doesn't "taint" the canvas, and without color conversion, so the channel values stay exactly as in the file.
async function loadBitmap(url) {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`${url}: HTTP ${res.status}`);
  return createImageBitmap(await res.blob(), { colorSpaceConversion: "none", premultiplyAlpha: "none" });
}

export class DataFeed {
  // onPoll(err) after every check: null - everything downloaded, otherwise an error (the first of the failed files).
  constructor({ onMeta, onDynamic, onNetwork, onTerrain, onRules, onPoll }) {
    this.handlers = { onMeta, onDynamic, onNetwork, onTerrain, onRules, onPoll };
    this.rulesKey = null;
    this.dynamicKey = null;
    this.networkKey = null;
    this.terrainKey = null;
    this.timer = null;
    this.busy = false;
  }

  // We don't poll in a hidden tab (Blob Storage traffic costs money); after coming back we check right away.
  start() {
    const tick = async () => {
      this.timer = null;
      this.busy = true;
      let error = null;
      try {
        await this.poll();
      } catch (err) {
        error = err;
      }
      this.busy = false;
      this.handlers.onPoll?.(error);
      if (!document.hidden) this.timer = setTimeout(tick, config.pollMs);
    };
    document.addEventListener("visibilitychange", () => {
      if (document.hidden) {
        clearTimeout(this.timer);
        this.timer = null;
      } else if (!this.timer && !this.busy) {
        tick();
      }
    });
    tick();
  }

  // Without meta.json there's nothing to check; the other files are downloaded independently, so e.g. a missing
  // terrain.png doesn't stop dynamic.json from refreshing. A failed file is retried on the next check.
  async poll() {
    const meta = await fetchJson("meta.json");
    this.handlers.onMeta?.(meta);
    const errors = [];
    const step = async (fn) => {
      try {
        await fn();
      } catch (err) {
        errors.push(err);
      }
    };

    const networkKey = `${meta.networkSession}:${meta.networkSeq}`;
    if (meta.networkSeq != null && networkKey !== this.networkKey) {
      await step(async () => {
        const network = await fetchJson("network.json");
        this.networkKey = networkKey;
        this.handlers.onNetwork?.(network);
      });
    }

    // Terrain changes rarely: terrain.json (world extent) + terrain.png with a key in the URL,
    // so the image can be cached and a new version still gets downloaded.
    const terrainKey = `${meta.terrainSession}:${meta.terrainSeq}`;
    if (meta.terrainSeq != null && terrainKey !== this.terrainKey) {
      await step(async () => {
        const info = await fetchJson("terrain.json");
        const v = `?v=${encodeURIComponent(terrainKey)}`;
        const image = await loadImage(`${config.dataBaseUrl}terrain.png${v}`);
        // Terrain for building (water and slopes for towns, regions for industries); an older Tf3Bridge doesn't publish them.
        const [sites, zones] = await Promise.all(["terrain-site.png", "terrain-zone.png"]
          .map((name) => loadBitmap(`${config.dataBaseUrl}${name}${v}`).catch(() => null)));
        this.terrainKey = terrainKey;
        this.handlers.onTerrain?.({ info, image, sites, zones });
      });
    }

    // Where industries may be placed (the streamer's settings in Tf3Bridge); changes rarely.
    if (meta.rulesVersion && meta.rulesVersion !== this.rulesKey) {
      await step(async () => {
        const rules = await fetchJson("build-rules.json");
        this.rulesKey = meta.rulesVersion;
        this.handlers.onRules?.(rules);
      });
    }

    const dynamicKey = `${meta.session}:${meta.dynamicSeq}`;
    if (meta.dynamicSeq && dynamicKey !== this.dynamicKey) {
      await step(async () => {
        const dynamic = await fetchJson("dynamic.json");
        this.dynamicKey = dynamicKey;
        this.handlers.onDynamic?.(dynamic);
      });
    }

    if (errors.length) throw errors[0];
  }
}
