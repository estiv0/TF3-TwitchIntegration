// Pinezki z linkiem: miejsce na mapie + opis zapisane w adresie strony, bez serwera.
// Format: #pin=<x>,<y>,<skala>&note=<opis> - x, y we współrzędnych gry (m, y na północ),
// skala = przybliżenie widoku w chwili tworzenia linku.

import { hashWith } from "./hash.js";
import { t } from "./i18n.js";

const NOTE_MAX = 200;

/** Pinezka z hasha adresu albo null. */
export function readPinHash(hash = location.hash) {
  const params = new URLSearchParams(hash.replace(/^#/, ""));
  if (!params.has("pin")) return null;
  const [x, y, scale] = params.get("pin").split(",").map(Number);
  if (!Number.isFinite(x) || !Number.isFinite(y)) return null;
  return {
    x, y,
    scale: Number.isFinite(scale) && scale > 0 ? scale : undefined,
    note: (params.get("note") ?? "").slice(0, NOTE_MAX),
  };
}

/** Części hasha pinezki ("pin=...", "note=..."). */
function pinParts({ x, y, note }, scale) {
  // Ręcznie zamiast URLSearchParams.toString(), żeby link był czytelny (przecinki, %20 zamiast "+").
  const pin = [`pin=${Math.round(x)},${Math.round(y)},${Number(scale.toPrecision(3))}`];
  if (note) pin.push(`note=${encodeURIComponent(note.slice(0, NOTE_MAX))}`);
  return pin;
}

/** Link do udostępnienia: adres strony z samą pinezką w hashu (bez zaznaczonego obiektu, np. &line=...). */
export function pinUrl(pin, scale) {
  const url = new URL(location.href);
  url.hash = pinParts(pin, scale).join("&");
  return url.href;
}

/**
 * Stawianie pinezki: przycisk włącza tryb (następne kliknięcie w mapę), prawy przycisk myszy
 * stawia od razu. Formularz przy pinezce zbiera opis i tworzy link (kopiowany do schowka).
 */
export class PinController {
  constructor(map, { form, button, wrap }) {
    this.map = map;
    this.form = form;
    this.button = button;
    this.wrap = wrap;
    this.note = form.querySelector("textarea");
    this.linkRow = form.querySelector("[data-pin-link]");
    this.urlInput = form.querySelector("#pin-url");
    this.copyBtn = form.querySelector("[data-pin-copy]");
    this.cancelBtn = form.querySelector("[data-pin-cancel]");
    this.saved = null; // pinezka z linku - wraca po anulowaniu nowej
    this.draft = null;

    button.addEventListener("click", () => map.setPlaceMode(map.placeMode === "pin" ? null : "pin"));

    this.note.addEventListener("input", () => {
      if (!this.draft) return;
      this.draft.note = this.note.value;
      this.showLink(false); // opis się zmienił - stary link już nie pasuje
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
      e.preventDefault(); // obsłużone - main.js nie odznacza już zaznaczenia
    });

    window.addEventListener("hashchange", () => this.loadFromHash());
    this.loadFromHash();
  }

  // Wywoływane przez MapView (onPlaceMode).
  modeChanged(on) {
    this.button.setAttribute("aria-pressed", String(on));
  }

  // Wywoływane przez MapView (onPlace): nowa pinezka w klikniętym miejscu + formularz.
  place({ x, y, sx, sy }) {
    this.draft = { x, y, note: "" };
    this.map.setPin(this.draft);
    this.note.value = "";
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
    // Adres w pasku też wskazuje pinezkę, ale zachowuje zaznaczenie z ekranu
    // (bez hashchange, więc widok się nie przestawia).
    history.replaceState(null, "", `#${hashWith(["pin", "note"], pinParts(this.draft, this.map.view.scale))}`);
    this.saved = { ...this.draft };
    this.urlInput.value = url;
    this.showLink(true);
    this.position(...this.formAnchor());
    this.copyAndClose();
  }

  // Link gotowy: pole z linkiem i "Zamknij" zamiast "Anuluj" (pinezka jest już zapisana w linku).
  showLink(on) {
    clearTimeout(this.closeTimer);
    this.linkRow.hidden = !on;
    this.cancelBtn.textContent = t(on ? "pin.close" : "pin.cancel");
    this.copyBtn.textContent = t("pin.copy");
  }

  // Po skopiowaniu chwilę pokazujemy "Skopiowano" i zamykamy okienko. Bez schowka (np. http)
  // okienko zostaje z zaznaczonym linkiem do ręcznego Ctrl+C.
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

  // Formularz został przy pinezce: jej pozycja na ekranie (do ponownego dopasowania po zmianie wysokości).
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
