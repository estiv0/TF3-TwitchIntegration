// Side panel width: a handle on its left edge (dragging, arrow keys, double-click = default).
// The width is the CSS variable --sidebar-w on .layout, remembered in this browser's localStorage.
// The map adapts on its own (ResizeObserver in MapView).

const KEY = "tf3-sidebar-width";
// MIN: what all the panel tabs need (EN and PL); a narrower saved panel widens to MIN.
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
      try { localStorage.setItem(KEY, String(width)); } catch { /* no localStorage */ }
    }
  };

  let saved = NaN;
  try { saved = Number(localStorage.getItem(KEY)); } catch { /* no localStorage */ }
  apply(Number.isFinite(saved) && saved > 0 ? saved : DEFAULT, false);
  handle.setAttribute("aria-valuemin", String(MIN));

  let drag = null; // { x, w } from the moment it was grabbed
  handle.addEventListener("pointerdown", (e) => {
    if (e.button !== 0) return;
    e.preventDefault();
    drag = { x: e.clientX, w: width };
    handle.classList.add("dragging");
    document.body.classList.add("resizing");
    try { handle.setPointerCapture(e.pointerId); } catch { /* the pointer is no longer active */ }
  });
  handle.addEventListener("pointermove", (e) => {
    if (drag) apply(drag.w + (drag.x - e.clientX), false); // the panel is on the right: to the left = wider
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
  // A smaller window: the panel must not cover the whole map.
  window.addEventListener("resize", () => {
    if (width > max()) apply(width, false);
  });
}
