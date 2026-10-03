// Pobieranie danych opublikowanych przez Tf3Bridge (latest/meta.json, dynamic.json, network.json, terrain.*).
// Polling: co pollMs pobieramy mały meta.json; dynamic/network/terrain tylko gdy zmienił się ich seq.
// W ukrytej karcie polling stoi.

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
  // onPoll(err) po każdym sprawdzeniu: null - wszystko pobrane, inaczej błąd (pierwszy z nieudanych plików).
  constructor({ onMeta, onDynamic, onNetwork, onTerrain, onPoll }) {
    this.handlers = { onMeta, onDynamic, onNetwork, onTerrain, onPoll };
    this.dynamicKey = null;
    this.networkKey = null;
    this.terrainKey = null;
    this.timer = null;
    this.busy = false;
  }

  // W ukrytej karcie nie odpytujemy (ruch z Blob Storage kosztuje); po powrocie od razu sprawdzamy.
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

  // Bez meta.json nie ma czego sprawdzać; pozostałe pliki pobieramy niezależnie, żeby np. brakujący
  // terrain.png nie zatrzymał odświeżania dynamic.json. Nieudany plik spróbujemy przy następnym sprawdzeniu.
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

    // Teren zmienia się rzadko: terrain.json (zakres w świecie) + terrain.png z kluczem w URL,
    // żeby obrazek mógł być keszowany, a nowa wersja i tak się pobrała.
    const terrainKey = `${meta.terrainSession}:${meta.terrainSeq}`;
    if (meta.terrainSeq != null && terrainKey !== this.terrainKey) {
      await step(async () => {
        const info = await fetchJson("terrain.json");
        const image = await loadImage(`${config.dataBaseUrl}terrain.png?v=${encodeURIComponent(terrainKey)}`);
        this.terrainKey = terrainKey;
        this.handlers.onTerrain?.({ info, image });
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
