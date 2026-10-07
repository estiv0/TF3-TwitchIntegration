// Pływające okienka szczegółów nad mapą (js/sidebar.js je rysuje, tu tylko pozycje): każde osobno,
// przeciągane za nagłówek (h2 / podtytuł), kliknięte wychodzi na wierzch. Okienko nie wychodzi poza mapę,
// a jego wysokość sięga najwyżej do dolnej krawędzi mapy (dalej przewija się w środku).
// Okienko zaznaczonego obiektu otwiera się w miejscu zapamiętanym w localStorage tej przeglądarki;
// przypięte zostaje w miejscu przypięcia i ma własną pozycję (do odpięcia).
// Na wąskich ekranach (CSS @media) okienka leżą jedno pod drugim na dole mapy i nie da się ich przesuwać.

const KEY = "tf3-detail-pos";
const MARGIN = 12, MIN_VISIBLE_H = 160;
// Jak @media (max-width: 900px) w CSS; innerWidth zamiast matchMedia, bo jego "change" potrafi
// przyjść dopiero po ResizeObserverze i okienko zostałoby w złym miejscu.
const isNarrow = () => window.innerWidth <= 900;

export class FloatingWindows {
  // key: wpis localStorage z pozycją (osobny dla każdej warstwy, np. okienko streamu w js/stream.js)
  constructor(layer, wrap, { key = KEY } = {}) {
    this.layer = layer;
    this.wrap = wrap;
    this.key = key;
    this.home = { x: MARGIN, y: MARGIN }; // miejsce okienka zaznaczonego obiektu
    this.saved = false;                   // pozycja z localStorage (a nie domyślna)
    try {
      const saved = JSON.parse(localStorage.getItem(key));
      if (Number.isFinite(saved?.x) && Number.isFinite(saved?.y)) {
        this.home = saved;
        this.saved = true;
      }
    } catch { /* brak localStorage albo zły wpis */ }
    this.pinnedPos = new Map(); // klucz okienka ("rodzaj:id") -> { x, y } przypiętego
    this.order = new Map();     // klucz -> kolejność na wierzchu (większe wyżej)
    this.top = 0;
    this.drag = null;           // { key, dx, dy } - punkt złapania względem lewego górnego rogu okienka

    layer.addEventListener("pointerdown", (e) => this.pointerDown(e));
    // Przeciąganie przez okno, nie przez okienko: sidebar może w tym czasie przerysować jego element.
    window.addEventListener("pointermove", (e) => this.pointerMove(e));
    window.addEventListener("pointerup", () => this.pointerUp());
    window.addEventListener("pointercancel", () => this.pointerUp());
    // Dwuklik w nagłówek okienka zaznaczonego obiektu: z powrotem w domyślne miejsce.
    layer.addEventListener("dblclick", (e) => {
      const card = e.target.closest("[data-card]");
      if (isNarrow() || !card || this.isPinned(card) || !e.target.closest("h2, .detail-sub")) return;
      this.home = { x: MARGIN, y: MARGIN };
      try { localStorage.removeItem(this.key); } catch { /* brak localStorage */ }
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

  // Ustawia wszystkie okienka (po każdym przerysowaniu i zmianie rozmiaru mapy).
  layout() {
    const cards = [...this.layer.querySelectorAll("[data-card]")];
    for (const card of cards) this.place(card);
    // przypięte na wąskim ekranie (bez pozycji) - po poszerzeniu zostaje tam, gdzie się pokazało
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

  // Okienko na wierzch (nowo zaznaczony obiekt, kliknięcie w okienko).
  front(key) {
    if (this.order.get(key) === this.top && this.top) return;
    this.order.set(key, ++this.top);
    const card = this.card(key);
    if (card) this.place(card);
  }

  // Przypięte okienko zostaje tam, gdzie jest (dalej ma własną pozycję, niezależną od okienka zaznaczonego obiektu).
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
    // zapisujemy pozycję po dopasowaniu do mapy, żeby po zmniejszeniu okna nie "uciekała"
    const pos = { x: card.offsetLeft, y: card.offsetTop };
    if (this.isPinned(card)) {
      this.pinnedPos.set(card.dataset.card, pos);
      return;
    }
    this.home = pos;
    try { localStorage.setItem(this.key, JSON.stringify(pos)); } catch { /* brak localStorage */ }
  }
}
