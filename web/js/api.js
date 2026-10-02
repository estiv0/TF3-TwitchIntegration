// Pobieranie danych opublikowanych przez Tf3Bridge (latest/meta.json, dynamic.json, network.json).
// Polling: co pollMs pobieramy mały meta.json; dynamic/network tylko gdy zmienił się ich seq.

import { config } from "../config.js";

async function fetchJson(name) {
  const res = await fetch(config.dataBaseUrl + name, { cache: "no-store" });
  if (!res.ok) throw new Error(`${name}: HTTP ${res.status}`);
  return res.json();
}

export class DataFeed {
  constructor({ onMeta, onDynamic, onNetwork, onError }) {
    this.handlers = { onMeta, onDynamic, onNetwork, onError };
    this.dynamicKey = null;
    this.networkKey = null;
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

    const dynamicKey = `${meta.session}:${meta.dynamicSeq}`;
    if (meta.dynamicSeq && dynamicKey !== this.dynamicKey) {
      const dynamic = await fetchJson("dynamic.json");
      this.dynamicKey = dynamicKey;
      this.handlers.onDynamic?.(dynamic);
    }
  }
}
