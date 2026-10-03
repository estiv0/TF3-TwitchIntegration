// Pływające okienko szczegółów nad mapą: przeciągane za nagłówek (h2 / podtytuł), pozycja
// zapamiętana w localStorage tej przeglądarki. Okienko nie wychodzi poza mapę, a jego wysokość
// sięga najwyżej do dolnej krawędzi mapy (dalej przewija się w środku).
// Na wąskich ekranach (CSS @media) okienko leży na dole mapy i nie da się go przesuwać.

const KEY = "tf3-detail-pos";
const MARGIN = 12, MIN_VISIBLE_H = 160;
// Jak @media (max-width: 900px) w CSS; innerWidth zamiast matchMedia, bo jego "change" potrafi
// przyjść dopiero po ResizeObserverze i okienko zostałoby w złym miejscu.
const isNarrow = () => window.innerWidth <= 900;

export function initFloatingPanel(panel, wrap) {
  let pos = { x: MARGIN, y: MARGIN };
  try {
    const saved = JSON.parse(localStorage.getItem(KEY));
    if (Number.isFinite(saved?.x) && Number.isFinite(saved?.y)) pos = saved;
  } catch { /* brak localStorage albo zły wpis */ }

  const place = () => {
    if (isNarrow()) {
      panel.style.left = panel.style.top = panel.style.maxHeight = "";
      return;
    }
    const w = wrap.clientWidth, h = wrap.clientHeight;
    const x = Math.max(MARGIN, Math.min(pos.x, w - panel.offsetWidth - MARGIN));
    const y = Math.max(MARGIN, Math.min(pos.y, h - Math.min(MIN_VISIBLE_H, panel.offsetHeight) - MARGIN));
    panel.style.left = `${x}px`;
    panel.style.top = `${y}px`;
    panel.style.maxHeight = `${Math.max(MIN_VISIBLE_H, h - y - MARGIN)}px`;
  };

  let drag = null; // { dx, dy } - punkt złapania względem lewego górnego rogu okienka
  panel.addEventListener("pointerdown", (e) => {
    if (e.button !== 0 || isNarrow()) return;
    if (!e.target.closest("h2, .detail-sub") || e.target.closest("button, a")) return;
    e.preventDefault();
    drag = { dx: e.clientX - panel.offsetLeft, dy: e.clientY - panel.offsetTop };
    panel.classList.add("dragging");
    try { panel.setPointerCapture(e.pointerId); } catch { /* wskaźnik już nieaktywny */ }
  });
  panel.addEventListener("pointermove", (e) => {
    if (!drag) return;
    pos = { x: e.clientX - drag.dx, y: e.clientY - drag.dy };
    place();
  });
  const end = () => {
    if (!drag) return;
    drag = null;
    panel.classList.remove("dragging");
    // zapisujemy pozycję po dopasowaniu do mapy, żeby po zmniejszeniu okna nie "uciekała"
    pos = { x: panel.offsetLeft, y: panel.offsetTop };
    try { localStorage.setItem(KEY, JSON.stringify(pos)); } catch { /* brak localStorage */ }
  };
  panel.addEventListener("pointerup", end);
  panel.addEventListener("pointercancel", end);
  // Dwuklik w nagłówek: z powrotem w domyślne miejsce.
  panel.addEventListener("dblclick", (e) => {
    if (isNarrow() || !e.target.closest("h2, .detail-sub")) return;
    pos = { x: MARGIN, y: MARGIN };
    try { localStorage.removeItem(KEY); } catch { /* brak localStorage */ }
    place();
  });

  // Zmiana rozmiaru mapy (okno, panel boczny) i samego okienka (pokazanie, nowa treść).
  const ro = new ResizeObserver(place);
  ro.observe(wrap);
  ro.observe(panel);
  window.addEventListener("resize", place);
}
