// Pinezki z linkiem: miejsce na mapie + opis zapisane w adresie strony, bez serwera.
// Format: #pin=<x>,<y>,<skala>&note=<opis> - x, y we współrzędnych gry (m, y na północ),
// skala = przybliżenie widoku w chwili tworzenia linku.

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

/** Pełny adres strony z pinezką w hashu. */
export function pinUrl({ x, y, note }, scale) {
  // Ręcznie zamiast URLSearchParams.toString(), żeby link był czytelny (przecinki, %20 zamiast "+").
  let hash = `pin=${Math.round(x)},${Math.round(y)},${Number(scale.toPrecision(3))}`;
  if (note) hash += `&note=${encodeURIComponent(note.slice(0, NOTE_MAX))}`;
  const url = new URL(location.href);
  url.hash = hash;
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
    this.saved = null; // pinezka z linku - wraca po anulowaniu nowej
    this.draft = null;

    button.addEventListener("click", () => map.setPinMode(!map.pinMode));

    this.note.addEventListener("input", () => {
      if (!this.draft) return;
      this.draft.note = this.note.value;
      this.linkRow.hidden = true; // opis się zmienił - stary link już nie pasuje
      map.setPin(this.draft);
    });

    form.addEventListener("submit", (e) => {
      e.preventDefault();
      this.createLink();
    });
    form.querySelector("[data-pin-cancel]").addEventListener("click", () => this.cancel());
    this.copyBtn.addEventListener("click", () => this.copy());

    document.addEventListener("keydown", (e) => {
      if (e.key !== "Escape") return;
      if (map.pinMode) map.setPinMode(false);
      else if (!form.hidden) this.cancel();
    });

    window.addEventListener("hashchange", () => this.loadFromHash());
    this.loadFromHash();
  }

  // Wywoływane przez MapView (onPinMode).
  modeChanged(on) {
    this.button.setAttribute("aria-pressed", String(on));
  }

  // Wywoływane przez MapView (onPlace): nowa pinezka w klikniętym miejscu + formularz.
  place({ x, y, sx, sy }) {
    this.draft = { x, y, note: "" };
    this.map.setPin(this.draft);
    this.note.value = "";
    this.linkRow.hidden = true;
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
    // Adres w pasku też wskazuje pinezkę (bez hashchange, więc widok się nie przestawia).
    history.replaceState(null, "", url);
    this.saved = { ...this.draft };
    this.urlInput.value = url;
    this.linkRow.hidden = false;
    this.position(...this.formAnchor());
    this.copy();
  }

  async copy() {
    this.urlInput.select();
    try {
      await navigator.clipboard.writeText(this.urlInput.value);
      this.copyBtn.textContent = "Skopiowano ✓";
    } catch {
      this.copyBtn.textContent = "Ctrl+C"; // schowek niedostępny (np. http) - link jest zaznaczony
    }
    clearTimeout(this.copyTimer);
    this.copyTimer = setTimeout(() => { this.copyBtn.textContent = "Kopiuj"; }, 2000);
  }

  // Formularz został przy pinezce: jej pozycja na ekranie (do ponownego dopasowania po zmianie wysokości).
  formAnchor() {
    const [sx, sy] = this.map.toScreen(this.draft.x, -this.draft.y);
    return [sx, sy];
  }

  cancel() {
    this.form.hidden = true;
    this.draft = null;
    this.map.setPin(this.saved);
  }

  loadFromHash() {
    const pin = readPinHash();
    if (!pin) return;
    this.saved = { x: pin.x, y: pin.y, note: pin.note };
    this.form.hidden = true;
    this.draft = null;
    this.map.setPin(this.saved);
    this.map.showPin(pin.scale);
  }
}
