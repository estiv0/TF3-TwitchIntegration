// Pobieranie danych opublikowanych przez Tf3Bridge (latest/meta.json, dynamic.json, network.json, terrain.*).
// Polling: co pollMs pobieramy mały meta.json; dynamic/network/terrain tylko gdy zmienił się ich seq.

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

export class DataFeed {
  constructor({ onMeta, onDynamic, onNetwork, onTerrain, onError }) {
    this.handlers = { onMeta, onDynamic, onNetwork, onTerrain, onError };
    this.dynamicKey = null;
    this.networkKey = null;
    this.terrainKey = null;
    this.timer = null;
  }

  start() {
    const tick = async () => {
      try {
        await this.poll();
      } catch (err) {
        this.handlers.onError?.(err);
      }
      this.timer = setTimeout(tick, config.pollMs);
    };
    tick();
  }

  async poll() {
    const meta = await fetchJson("meta.json");
    this.handlers.onMeta?.(meta);

    const networkKey = `${meta.networkSession}:${meta.networkSeq}`;
    if (meta.networkSeq != null && networkKey !== this.networkKey) {
      const network = await fetchJson("network.json");
      this.networkKey = networkKey;
      this.handlers.onNetwork?.(network);
    }

    // Teren zmienia się rzadko: terrain.json (zakres w świecie) + terrain.png z kluczem w URL,
    // żeby obrazek mógł być keszowany, a nowa wersja i tak się pobrała.
    const terrainKey = `${meta.terrainSession}:${meta.terrainSeq}`;
    if (meta.terrainSeq != null && terrainKey !== this.terrainKey) {
      const info = await fetchJson("terrain.json");
      const image = await loadImage(`${config.dataBaseUrl}terrain.png?v=${encodeURIComponent(terrainKey)}`);
      this.terrainKey = terrainKey;
      this.handlers.onTerrain?.({ info, image });
    }

    const dynamicKey = `${meta.session}:${meta.dynamicSeq}`;
    if (meta.dynamicSeq && dynamicKey !== this.dynamicKey) {
      const dynamic = await fetchJson("dynamic.json");
      this.dynamicKey = dynamicKey;
      this.handlers.onDynamic?.(dynamic);
    }
  }
}
