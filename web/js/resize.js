// Szerokość panelu bocznego: uchwyt na jego lewej krawędzi (przeciąganie, strzałki, dwuklik = domyślna).
// Szerokość to zmienna CSS --sidebar-w na .layout, zapamiętana w localStorage tej przeglądarki.
// Mapa dopasowuje się sama (ResizeObserver w MapView).

const KEY = "tf3-sidebar-width";
// MIN: tyle potrzebują wszystkie zakładki panelu (PL i EN); węższy zapisany panel rozszerza się do MIN.
const DEFAULT = 520, MIN = 520, MAP_MIN = 320, STEP = 20;

export function initSidebarResize(layout, handle) {
  const max = () => Math.max(MIN, Math.min(900, window.innerWidth - MAP_MIN));
  const clamp = (w) => Math.round(Math.max(MIN, Math.min(max(), w)));
  let width = DEFAULT;

  const apply = (w, save = true) => {
    width = clamp(w);
    layout.style.setProperty("--sidebar-w", `${width}px`);
    handle.setAttribute("aria-valuenow", String(width));
    if (save) {
      try { localStorage.setItem(KEY, String(width)); } catch { /* brak localStorage */ }
    }
  };

  let saved = NaN;
  try { saved = Number(localStorage.getItem(KEY)); } catch { /* brak localStorage */ }
  apply(Number.isFinite(saved) && saved > 0 ? saved : DEFAULT, false);
  handle.setAttribute("aria-valuemin", String(MIN));

  let drag = null; // { x, w } z chwili złapania
  handle.addEventListener("pointerdown", (e) => {
    if (e.button !== 0) return;
    e.preventDefault();
    drag = { x: e.clientX, w: width };
    handle.classList.add("dragging");
    document.body.classList.add("resizing");
    try { handle.setPointerCapture(e.pointerId); } catch { /* wskaźnik już nieaktywny */ }
  });
  handle.addEventListener("pointermove", (e) => {
    if (drag) apply(drag.w + (drag.x - e.clientX), false); // panel jest po prawej: w lewo = szerzej
  });
  const end = () => {
    if (!drag) return;
    drag = null;
    handle.classList.remove("dragging");
    document.body.classList.remove("resizing");
    apply(width);
  };
  handle.addEventListener("pointerup", end);
  handle.addEventListener("pointercancel", end);
  handle.addEventListener("dblclick", () => apply(DEFAULT));
  handle.addEventListener("keydown", (e) => {
    const d = { ArrowLeft: STEP, ArrowRight: -STEP }[e.key];
    if (d) {
      e.preventDefault();
      apply(width + d);
    } else if (e.key === "Home") {
      e.preventDefault();
      apply(DEFAULT);
    }
  });
  // Mniejsze okno: panel nie może zasłonić całej mapy.
  window.addEventListener("resize", () => {
    if (width > max()) apply(width, false);
  });
}
