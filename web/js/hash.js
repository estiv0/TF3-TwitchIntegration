// Page state in the address hash: the pin (pin.js) and the selected object, side by side, e.g.
// #pin=120_-340_0.25&note=Port&line=429883. Parts are changed as "key=value" strings,
// so the pin description isn't re-encoded.

// owner = a viewer's estate, the id is a text key (owners.js), encoded: #owner=ala
const SEL_KINDS = ["town", "industry", "warehouse", "station", "line", "owner"];

const parts = () => location.hash.replace(/^#/, "").split("&").filter(Boolean);

/** The hash (without "#") from the current one, without the parts with `drop` keys, with `extra` ("k=v", already encoded) first. */
export function hashWith(drop, extra = []) {
  return [...extra, ...parts().filter((p) => !drop.includes(p.split("=")[0]))].join("&");
}

/** The selected object from the hash ({ kind, id }), or null. */
export function readSelection() {
  for (const p of parts()) {
    const [key, value = ""] = p.split("=");
    if (key === "owner") {
      let id = "";
      try { id = decodeURIComponent(value); } catch { /* broken link */ }
      if (id) return { kind: key, id };
      continue;
    }
    const id = Number(value);
    if (SEL_KINDS.includes(key) && Number.isInteger(id)) return { kind: key, id };
  }
  return null;
}

/** Stores the selection in the address (without a new history entry and without hashchange). */
export function writeSelection(sel) {
  const value = sel?.kind === "owner" ? encodeURIComponent(sel.id) : sel?.id;
  const hash = hashWith(SEL_KINDS, sel?.kind ? [`${sel.kind}=${value}`] : []);
  history.replaceState(null, "", hash ? `#${hash}` : location.pathname + location.search);
}
