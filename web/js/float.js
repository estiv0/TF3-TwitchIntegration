// Floating detail windows over the map (js/sidebar.js draws them, here only the positions): each one separately,
// dragged by the header (h2 / subtitle), a clicked one comes to the front. A window doesn't leave the map,
// and its height reaches at most the bottom edge of the map (beyond that it scrolls inside).
// The selected object's window opens at the spot remembered in this browser's localStorage;
// a pinned one stays where it was pinned and has its own position (until unpinned).
// On narrow screens (CSS @media) the windows lie one below the other at the bottom of the map and can't be moved.

const KEY = "tf3-detail-pos";
const MARGIN = 12, MIN_VISIBLE_H = 160;
// Like @media (max-width: 900px) in CSS; innerWidth instead of matchMedia, because its "change" can
// arrive only after the ResizeObserver and the window would stay in the wrong place.
const isNarrow = () => window.innerWidth <= 900;

export class FloatingWindows {
  // key: the localStorage entry with the position (separate for each layer, e.g. the stream window in js/stream.js)
  constructor(layer, wrap, { key = KEY } = {}) {
    this.layer = layer;
    this.wrap = wrap;
    this.key = key;
    this.home = { x: MARGIN, y: MARGIN }; // the spot of the selected object's window
    this.saved = false;                   // a position from localStorage (rather than the default)
    try {
      const saved = JSON.parse(localStorage.getItem(key));
      if (Number.isFinite(saved?.x) && Number.isFinite(saved?.y)) {
        this.home = saved;
        this.saved = true;
      }
    } catch { /* no localStorage or a bad entry */ }
    this.pinnedPos = new Map(); // window key ("kind:id") -> { x, y } of a pinned one
    this.order = new Map();     // key -> stacking order (higher on top)
    this.top = 0;
    this.drag = null;           // { key, dx, dy } - the grab point relative to the window's top left corner

    layer.addEventListener("pointerdown", (e) => this.pointerDown(e));
    // Dragging through the window object, not the floating window: sidebar may redraw its element meanwhile.
    window.addEventListener("pointermove", (e) => this.pointerMove(e));
    window.addEventListener("pointerup", () => this.pointerUp());
    window.addEventListener("pointercancel", () => this.pointerUp());
    // Double-click on the header of the selected object's window: back to the default spot.
    layer.addEventListener("dblclick", (e) => {
      const card = e.target.closest("[data-card]");
      if (isNarrow() || !card || this.isPinned(card) || !e.target.closest("h2, .detail-sub")) return;
      this.home = { x: MARGIN, y: MARGIN };
      try { localStorage.removeItem(this.key); } catch { /* no localStorage */ }
      this.layout();
    });

    new ResizeObserver(() => this.layout()).observe(wrap);
    window.addEventListener("resize", () => this.layout());
  }

  card(key) {
    return [...this.layer.querySelectorAll("[data-card]")].find((c) => c.dataset.card === key) ?? null;
  }

  isPinned(card) {
    return card.dataset.pinned === "true";
  }

  pos(card) {
    return (this.isPinned(card) && this.pinnedPos.get(card.dataset.card)) || this.home;
  }

  // Positions all windows (after every redraw and map resize).
  layout() {
    const cards = [...this.layer.querySelectorAll("[data-card]")];
    for (const card of cards) this.place(card);
    // pinned on a narrow screen (no position) - after widening it stays where it showed up
    if (!isNarrow()) for (const card of cards) if (this.isPinned(card) && !this.pinnedPos.has(card.dataset.card)) this.pin(card.dataset.card);
  }

  place(card) {
    if (isNarrow()) {
      card.style.left = card.style.top = card.style.maxHeight = card.style.zIndex = "";
      return;
    }
    const w = this.wrap.clientWidth, h = this.wrap.clientHeight;
    const pos = this.pos(card);
    const x = Math.max(MARGIN, Math.min(pos.x, w - card.offsetWidth - MARGIN));
    const y = Math.max(MARGIN, Math.min(pos.y, h - Math.min(MIN_VISIBLE_H, card.offsetHeight) - MARGIN));
    card.style.left = `${x}px`;
    card.style.top = `${y}px`;
    card.style.maxHeight = `${Math.max(MIN_VISIBLE_H, h - y - MARGIN)}px`;
    card.style.zIndex = String(this.order.get(card.dataset.card) ?? 0);
  }

  // A window to the front (a newly selected object, a click on the window).
  front(key) {
    if (this.order.get(key) === this.top && this.top) return;
    this.order.set(key, ++this.top);
    const card = this.card(key);
    if (card) this.place(card);
  }

  // A pinned window stays where it is (it keeps its own position, independent of the selected object's window).
  pin(key) {
    const card = this.card(key);
    if (card && !isNarrow()) this.pinnedPos.set(key, { x: card.offsetLeft, y: card.offsetTop });
  }

  unpin(key) {
    this.pinnedPos.delete(key);
  }

  pointerDown(e) {
    const card = e.target.closest("[data-card]");
    if (!card || isNarrow()) return;
    this.front(card.dataset.card);
    if (e.button !== 0 || !e.target.closest("h2, .detail-sub") || e.target.closest("button, a")) return;
    e.preventDefault();
    this.drag = { key: card.dataset.card, dx: e.clientX - card.offsetLeft, dy: e.clientY - card.offsetTop };
    card.classList.add("dragging");
  }

  pointerMove(e) {
    if (!this.drag) return;
    const card = this.card(this.drag.key);
    if (!card) return;
    const pos = { x: e.clientX - this.drag.dx, y: e.clientY - this.drag.dy };
    if (this.isPinned(card)) this.pinnedPos.set(this.drag.key, pos);
    else this.home = pos;
    this.place(card);
  }

  pointerUp() {
    if (!this.drag) return;
    const card = this.card(this.drag.key);
    this.drag = null;
    if (!card) return;
    card.classList.remove("dragging");
    // we save the position after fitting it to the map, so it doesn't "run away" after the window shrinks
    const pos = { x: card.offsetLeft, y: card.offsetTop };
    if (this.isPinned(card)) {
      this.pinnedPos.set(card.dataset.card, pos);
      return;
    }
    this.home = pos;
    try { localStorage.setItem(this.key, JSON.stringify(pos)); } catch { /* no localStorage */ }
  }
}
