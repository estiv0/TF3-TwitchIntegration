// Stream na Twitchu: przycisk w górnym pasku i pływający mini-player nad mapą.
// Przycisk to zwykły link do kanału (config.twitchUrl); na szerokim ekranie kliknięcie otwiera/zamyka
// okienko z playerem zamiast przechodzić na Twitcha (Ctrl/środkowy przycisk dalej otwiera nową kartę).
// Na wąskim ekranie (telefon) player zasłoniłby mapę - tam przycisk po prostu otwiera Twitcha.
// Player (iframe) ładuje się dopiero po otwarciu okienka i znika po zamknięciu: bez transferu i bez
// podwójnego dźwięku u widzów, którzy oglądają już na Twitchu. Startuje wyciszony.
// Twitch wymaga parent=<domena strony> (u nas location.hostname: gin.estfosoft.com albo localhost)
// i playera co najmniej 400x300 px (CSS .stream-frame).

import { config } from "../config.js";
import { FloatingWindows } from "./float.js";
import { t } from "./i18n.js";

const OPEN_KEY = "tf3-stream-open";
const MARGIN = 12, TOOLS_W = 46; // map-tools (34 px) w prawym górnym rogu mapy + odstęp
const isNarrow = () => window.innerWidth <= 900;

export class StreamPlayer {
  constructor({ button, layer, wrap }) {
    this.button = button;
    this.layer = layer;
    this.wrap = wrap;
    this.card = layer.querySelector("[data-card]");
    this.frame = layer.querySelector("[data-stream-frame]");
    this.channel = config.twitchUrl.match(/twitch\.tv\/([\w]+)/i)?.[1] ?? null;

    if (!config.twitchUrl) {
      button.hidden = true;
      return;
    }
    button.href = config.twitchUrl;
    for (const a of layer.querySelectorAll("[data-stream-link]")) a.href = config.twitchUrl;
    if (!this.channel) return; // nie da się osadzić - zostaje sam link
    layer.querySelector("[data-stream-name]").textContent = this.channel;

    this.windows = new FloatingWindows(layer, wrap, { key: "tf3-stream-pos" });
    button.addEventListener("click", (e) => {
      if (isNarrow() || e.ctrlKey || e.metaKey || e.shiftKey || e.button !== 0) return;
      e.preventDefault();
      this.toggle(this.layer.hidden);
    });
    layer.querySelector("[data-stream-close]").addEventListener("click", () => this.toggle(false));
    // zwężenie okna: okienko znika (na telefonie go nie ma), razem z dźwiękiem
    window.addEventListener("resize", () => { if (isNarrow() && !this.layer.hidden) this.toggle(false, false); });

    let wasOpen = false;
    try { wasOpen = localStorage.getItem(OPEN_KEY) === "1"; } catch { /* brak localStorage */ }
    if (wasOpen && !isNarrow()) this.toggle(true, false);
    else this.refresh();
  }

  // remember=false: bez zapisu (otwarcie po przeładowaniu, zamknięcie przez zwężenie okna)
  toggle(open, remember = true) {
    this.layer.hidden = !open;
    if (open) {
      if (!this.frame.firstChild) {
        const src = `https://player.twitch.tv/?channel=${encodeURIComponent(this.channel)}`
          + `&parent=${encodeURIComponent(location.hostname)}&muted=true&autoplay=true`;
        this.frame.innerHTML = `<iframe src="${src}" allow="autoplay; fullscreen" allowfullscreen title="Twitch"></iframe>`;
      }
      // pierwsze otwarcie: prawy górny róg mapy, obok przycisków mapy
      if (!this.windows.saved) {
        this.windows.home = {
          x: this.wrap.clientWidth - this.card.offsetWidth - MARGIN - TOOLS_W,
          y: MARGIN,
        };
        this.windows.saved = true; // dalej trzyma się miejsca, w które widz je przesunie
      }
      this.windows.front("stream");
      this.windows.layout();
    } else {
      this.frame.replaceChildren();
    }
    if (remember) {
      try { localStorage.setItem(OPEN_KEY, open ? "1" : "0"); } catch { /* brak localStorage */ }
    }
    this.refresh();
  }

  // live: dane z gry są świeże (main.js: renderStatus) - gra chodzi, więc stream w praktyce też
  setLive(live) {
    this.button.dataset.live = String(!!live);
    this.refresh();
  }

  refresh() {
    const open = !this.layer.hidden;
    this.button.setAttribute("aria-expanded", String(open));
    this.button.title = t(open ? "stream.hide" : this.channel && !isNarrow() ? "stream.show" : "stream.open");
  }
}
