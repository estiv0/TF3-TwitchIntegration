// Drawing time per map step: ?perf in the address shows a table over the map (the last WINDOW_MS).
// Steps are named "parent/child" - a child is a part of its parent's time. The time is the JS time of issuing
// the canvas commands; rasterizing on the GPU isn't in it, it shows as a lower FPS / a longer worst frame gap.

export const perfOn = new URLSearchParams(location.search).has("perf");

const WINDOW_MS = 2000;
const REFRESH_MS = 500;

const stats = new Map(); // name -> [{ t, ms }]
const info = new Map();  // name -> the last value (e.g. how many lines were drawn)
const frames = [];       // performance.now() at the start of each frame
let box = null;

/** fn() timed as the step name (without ?perf just fn()). */
export function timed(name, fn) {
  if (!perfOn) return fn();
  let list = stats.get(name);
  if (!list) stats.set(name, list = []); // before fn, so a parent's row comes before its children
  const t0 = performance.now();
  try {
    return fn();
  } finally {
    const t = performance.now();
    list.push({ t, ms: t - t0 });
  }
}

/** The start of a map frame: frames per second and the gaps between them. */
export function frameStart() {
  if (perfOn) frames.push(performance.now());
}

/** A value shown under the table (the last one set). */
export function perfInfo(name, value) {
  if (perfOn) info.set(name, value);
}

function prune(list, from) {
  let i = 0;
  while (i < list.length && (list[i].t ?? list[i]) < from) i++;
  if (i) list.splice(0, i);
}

const f2 = (x) => x.toFixed(2).padStart(7);

function render() {
  const now = performance.now(), from = now - WINDOW_MS, sec = WINDOW_MS / 1000;
  prune(frames, from);
  let worst = 0;
  for (let i = 1; i < frames.length; i++) worst = Math.max(worst, frames[i] - frames[i - 1]);
  const out = [
    `frames/s ${(frames.length / sec).toFixed(1)}   worst gap ${worst.toFixed(0)} ms   (last ${sec} s)`,
    "",
    "step                     calls/s   avg ms   max ms   ms/s",
  ];
  // A step first seen later (e.g. terrain after it loads) still goes under its parent: sort by the order of the
  // ancestors, then by its own.
  const order = [...stats.keys()];
  const sortKey = (name) => name.split("/").map((_, i, parts) => {
    const at = order.indexOf(parts.slice(0, i + 1).join("/"));
    return at < 0 ? order.indexOf(name) : at;
  });
  const keys = new Map(order.map((name) => [name, sortKey(name)]));
  order.sort((a, b) => {
    const ka = keys.get(a), kb = keys.get(b);
    for (let i = 0; i < Math.min(ka.length, kb.length); i++) if (ka[i] !== kb[i]) return ka[i] - kb[i];
    return ka.length - kb.length;
  });
  for (const name of order) {
    const list = stats.get(name);
    prune(list, from);
    const parts = name.split("/");
    const label = ("  ".repeat(parts.length - 1) + parts.at(-1)).padEnd(24);
    if (!list.length) {
      out.push(`${label}       -`);
      continue;
    }
    let sum = 0, max = 0;
    for (const s of list) {
      sum += s.ms;
      if (s.ms > max) max = s.ms;
    }
    out.push(`${label}${(list.length / sec).toFixed(1).padStart(8)} ${f2(sum / list.length)}  ${f2(max)} ${f2(sum / sec)}`);
  }
  if (info.size) out.push("", ...[...info].map(([k, v]) => `${k}: ${v}`));
  box.textContent = out.join("\n");
}

if (perfOn) {
  box = document.createElement("pre");
  box.style.cssText = "position:fixed;left:8px;bottom:8px;z-index:9999;margin:0;padding:8px 10px;" +
    "font:12px/1.35 ui-monospace,Consolas,monospace;color:#e8e8e8;background:rgba(0,0,0,.78);" +
    "border-radius:6px;pointer-events:none;white-space:pre";
  document.body.append(box);
  setInterval(render, REFRESH_MS);
}
