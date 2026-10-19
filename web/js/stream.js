// The Twitch stream: a button in the top bar and a floating mini player over the map.
// The button is a plain link to the channel (config.twitchUrl); on a wide screen a click opens/closes
// the player window instead of going to Twitch (Ctrl/middle button still opens a new tab).
// On a narrow screen (phone) the player would cover the map - there the button just opens Twitch.
// The player (iframe) only loads once the window opens and disappears when it closes: no traffic and no
// double audio for viewers already watching on Twitch. It starts muted.
// Twitch requires parent=<site domain> (here location.hostname: e.g. tf3.example.com or localhost)
// and a player of at least 400x300 px (CSS .stream-frame).

import { config } from "../config.js";
import { FloatingWindows } from "./float.js";
import { t } from "./i18n.js";

const OPEN_KEY = "tf3-stream-open";
const MARGIN = 12, TOOLS_W = 46; // map-tools (34 px) in the top right corner of the map + a gap
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
    if (!this.channel) return; // can't be embedded - only the link remains
    layer.querySelector("[data-stream-name]").textContent = this.channel;

    this.windows = new FloatingWindows(layer, wrap, { key: "tf3-stream-pos" });
    button.addEventListener("click", (e) => {
      if (isNarrow() || e.ctrlKey || e.metaKey || e.shiftKey || e.button !== 0) return;
      e.preventDefault();
      this.toggle(this.layer.hidden);
    });
    layer.querySelector("[data-stream-close]").addEventListener("click", () => this.toggle(false));
    // the window narrows: the player window disappears (there's none on a phone), together with the audio
    window.addEventListener("resize", () => { if (isNarrow() && !this.layer.hidden) this.toggle(false, false); });

    let wasOpen = false;
    try { wasOpen = localStorage.getItem(OPEN_KEY) === "1"; } catch { /* no localStorage */ }
    if (wasOpen && !isNarrow()) this.toggle(true, false);
    else this.refresh();
  }

  // remember=false: not stored (opening after a reload, closing because the window narrowed)
  toggle(open, remember = true) {
    this.layer.hidden = !open;
    if (open) {
      if (!this.frame.firstChild) {
        const src = `https://player.twitch.tv/?channel=${encodeURIComponent(this.channel)}`
          + `&parent=${encodeURIComponent(location.hostname)}&muted=true&autoplay=true`;
        this.frame.innerHTML = `<iframe src="${src}" allow="autoplay; fullscreen" title="Twitch"></iframe>`;
      }
      // the first opening: the top right corner of the map, next to the map buttons
      if (!this.windows.saved) {
        this.windows.home = {
          x: this.wrap.clientWidth - this.card.offsetWidth - MARGIN - TOOLS_W,
          y: MARGIN,
        };
        this.windows.saved = true; // from then on it sticks to where the viewer moves it
      }
      this.windows.front("stream");
      this.windows.layout();
    } else {
      this.frame.replaceChildren();
    }
    if (remember) {
      try { localStorage.setItem(OPEN_KEY, open ? "1" : "0"); } catch { /* no localStorage */ }
    }
    this.refresh();
  }

  // live: the game data is fresh (main.js: renderStatus) - the game is running, so in practice the stream too
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
