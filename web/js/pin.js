// Pins with a link: a spot on the map + a description stored in the page address, no server.
// Format: #pin=<x>_<y>_<scale>&note=<description> - x, y in game coordinates (m, y to the north),
// scale = the view zoom when the link was created. Underscores instead of commas, because Twitch chat
// cuts a link at a comma; old links with commas still work.

import { hashWith } from "./hash.js";
import { t } from "./i18n.js";

// Short, because a link with Polish characters (%C5%9B...) grows fast, and a chat message has a limit.
export const NOTE_MAX = 120;

/** The pin from the address hash, or null. */
export function readPinHash(hash = location.hash) {
  const params = new URLSearchParams(hash.replace(/^#/, ""));
  if (!params.has("pin")) return null;
  const [x, y, scale] = params.get("pin").split(/[_,]/).map(Number);
  if (!Number.isFinite(x) || !Number.isFinite(y)) return null;
  return {
    x, y,
    scale: Number.isFinite(scale) && scale > 0 ? scale : undefined,
    note: (params.get("note") ?? "").slice(0, NOTE_MAX),
  };
}

/** The pin's hash parts ("pin=...", "note=..."). */
function pinParts({ x, y, note }, scale) {
  // By hand instead of URLSearchParams.toString(), so the link is readable (%20 instead of "+").
  const pin = [`pin=${Math.round(x)}_${Math.round(y)}_${Number(scale.toPrecision(3))}`];
  if (note) pin.push(`note=${encodeURIComponent(note.slice(0, NOTE_MAX))}`);
  return pin;
}

/** A link to share: the page address with only the pin in the hash (no selected object, e.g. &line=...). */
export function pinUrl(pin, scale) {
  const url = new URL(location.href);
  url.hash = pinParts(pin, scale).join("&");
  return url.href;
}

/**
 * Placing a pin: the button turns on the mode (the next click on the map), the right mouse button
 * places it right away. The form next to the pin collects a description and creates a link (copied to the clipboard).
 */
export class PinController {
  constructor(map, { form, button, wrap }) {
    this.map = map;
    this.form = form;
    this.button = button;
    this.wrap = wrap;
    this.note = form.querySelector("textarea");
    this.note.maxLength = NOTE_MAX;
    this.count = form.querySelector("[data-pin-count]");
    this.linkRow = form.querySelector("[data-pin-link]");
    this.urlInput = form.querySelector("#pin-url");
    this.copyBtn = form.querySelector("[data-pin-copy]");
    this.cancelBtn = form.querySelector("[data-pin-cancel]");
    this.saved = null; // the pin from the link - comes back after a new one is cancelled
    this.draft = null;

    button.addEventListener("click", () => map.setPlaceMode(map.placeMode === "pin" ? null : "pin"));

    this.note.addEventListener("input", () => {
      if (!this.draft) return;
      this.draft.note = this.note.value;
      this.showCount();
      this.showLink(false); // the description changed - the old link no longer fits
      map.setPin(this.draft);
    });

    form.addEventListener("submit", (e) => {
      e.preventDefault();
      this.createLink();
    });
    this.cancelBtn.addEventListener("click", () => this.cancel());
    this.copyBtn.addEventListener("click", () => this.copyAndClose());

    document.addEventListener("keydown", (e) => {
      if (e.key !== "Escape") return;
      if (map.placeMode === "pin") map.setPlaceMode(null);
      else if (!form.hidden) this.cancel();
      else return;
      e.preventDefault(); // handled - main.js no longer clears the selection
    });

    window.addEventListener("hashchange", () => this.loadFromHash());
    this.loadFromHash();
  }

  // Called by MapView (onPlaceMode).
  modeChanged(on) {
    this.button.setAttribute("aria-pressed", String(on));
  }

  // Called by MapView (onPlace): a new pin at the clicked spot + the form.
  place({ x, y, sx, sy }) {
    this.draft = { x, y, note: "" };
    this.map.setPin(this.draft);
    this.note.value = "";
    this.showCount();
    this.showLink(false);
    this.form.hidden = false;
    this.position(sx, sy);
    this.note.focus();
  }

  position(sx, sy) {
    const w = this.wrap.clientWidth, h = this.wrap.clientHeight;
    const fw = this.form.offsetWidth, fh = this.form.offsetHeight;
    this.form.style.left = `${Math.max(8, Math.min(sx + 16, w - fw - 8))}px`;
    this.form.style.top = `${Math.max(8, Math.min(sy - 24, h - fh - 8))}px`;
  }

  createLink() {
    if (!this.draft) return;
    const url = pinUrl(this.draft, this.map.view.scale);
    // The address bar also points at the pin, but keeps the on-screen selection
    // (without hashchange, so the view doesn't move).
    history.replaceState(null, "", `#${hashWith(["pin", "note"], pinParts(this.draft, this.map.view.scale))}`);
    this.saved = { ...this.draft };
    this.urlInput.value = url;
    this.showLink(true);
    this.position(...this.formAnchor());
    this.copyAndClose();
  }

  showCount() {
    this.count.textContent = `${this.note.value.length}/${NOTE_MAX}`;
  }

  // The link is ready: the field with the link and "Close" instead of "Cancel" (the pin is already stored in the link).
  showLink(on) {
    clearTimeout(this.closeTimer);
    this.linkRow.hidden = !on;
    this.cancelBtn.textContent = t(on ? "pin.close" : "pin.cancel");
    this.copyBtn.textContent = t("pin.copy");
  }

  // After copying we briefly show "Copied" and close the window. Without a clipboard (e.g. http)
  // the window stays with the link selected for a manual Ctrl+C.
  async copyAndClose() {
    clearTimeout(this.closeTimer);
    this.urlInput.select();
    try {
      await navigator.clipboard.writeText(this.urlInput.value);
    } catch {
      this.copyBtn.textContent = "Ctrl+C";
      return;
    }
    this.copyBtn.textContent = t("pin.copied");
    this.closeTimer = setTimeout(() => this.cancel(), 900);
  }

  // The form stayed next to the pin: its position on screen (for refitting after a height change).
  formAnchor() {
    const [sx, sy] = this.map.toScreen(this.draft.x, -this.draft.y);
    return [sx, sy];
  }

  cancel() {
    clearTimeout(this.closeTimer);
    this.form.hidden = true;
    this.draft = null;
    this.map.setPin(this.saved);
  }

  loadFromHash() {
    const pin = readPinHash();
    if (!pin) return;
    this.saved = { x: pin.x, y: pin.y, note: pin.note };
    clearTimeout(this.closeTimer);
    this.form.hidden = true;
    this.draft = null;
    this.map.setPin(this.saved);
    this.map.showPin(pin.scale);
  }
}
