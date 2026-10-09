// Stan strony w hashu adresu: pinezka (pin.js) i zaznaczony obiekt, obok siebie, np.
// #pin=120_-340_0.25&note=Port&line=429883. Części zmieniamy jako napisy "klucz=wartość",
// żeby nie przekodować opisu pinezki.

// owner = majątek widza, id to klucz tekstowy (owners.js), zakodowany: #owner=kinek
const SEL_KINDS = ["town", "industry", "warehouse", "station", "line", "owner"];

const parts = () => location.hash.replace(/^#/, "").split("&").filter(Boolean);

/** Hash (bez "#") z bieżącego, bez części o kluczach `drop`, z `extra` ("k=v", już zakodowane) na początku. */
export function hashWith(drop, extra = []) {
  return [...extra, ...parts().filter((p) => !drop.includes(p.split("=")[0]))].join("&");
}

/** Zaznaczony obiekt z hasha ({ kind, id }) albo null. */
export function readSelection() {
  for (const p of parts()) {
    const [key, value = ""] = p.split("=");
    if (key === "owner") {
      let id = "";
      try { id = decodeURIComponent(value); } catch { /* zepsuty link */ }
      if (id) return { kind: key, id };
      continue;
    }
    const id = Number(value);
    if (SEL_KINDS.includes(key) && Number.isInteger(id)) return { kind: key, id };
  }
  return null;
}

/** Zapisuje zaznaczenie w adresie (bez nowego wpisu w historii i bez hashchange). */
export function writeSelection(sel) {
  const value = sel?.kind === "owner" ? encodeURIComponent(sel.id) : sel?.id;
  const hash = hashWith(SEL_KINDS, sel?.kind ? [`${sel.kind}=${value}`] : []);
  history.replaceState(null, "", hash ? `#${hash}` : location.pathname + location.search);
}
