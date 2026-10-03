// Zakładka Firma: finanse wg lat (tabela z okna Finanse w grze) i historia z logbooka świata
// (stan konta, wartość firmy, mieszkańcy). Dane: dynamic.company (lua_mod: collectors.financeTable /
// worldHistory), odświeżane przez moda raz na miesiąc gry.

import { carrierIcon, carrierName, escapeHtml as esc, fmtInt, fmtMoney, fmtMoneyShort, fmtShort } from "./format.js";
import { t } from "./i18n.js";

// Wiersze tabeli: klucz i18n -> czy wiersz z moda ({ type, maintenance }) do niego należy.
const FIN_ROWS = [
  ["fin.income", (r) => r.type === "INCOME"],
  ["fin.vehicleCosts", (r) => r.type === "MAINTENANCE" && (r.maintenance === "VEHICLE" || r.maintenance === "VEHICLE_MAINTENANCE")],
  ["fin.infraCosts", (r) => r.type === "MAINTENANCE" && r.maintenance !== "VEHICLE" && r.maintenance !== "VEHICLE_MAINTENANCE"],
  ["fin.purchases", (r) => r.type === "ACQUISITION"],
  ["fin.construction", (r) => r.type === "CONSTRUCTION"],
  ["fin.subsidy", (r) => r.type === "SUBSIDY"],
  ["fin.other", (r) => r.type === "OTHER" || r.type === "LOAN"],
];
const CARRIER_ORDER = ["RAIL", "ROAD", "TRAM", "WATER", "AIR", "OTHER"];

/** Sumy kolumn (lata) dla wierszy pasujących do match. */
function sumRows(rows, n, match) {
  const out = new Array(n).fill(0);
  let any = false;
  for (const r of rows) {
    if (!match(r)) continue;
    any = true;
    (r.values ?? []).forEach((v, i) => { if (i < n) out[i] += v ?? 0; });
  }
  return any ? out : null;
}

/** HTML zakładki Firma; company = dynamic.company. */
export function companyHtml(company) {
  if (!company || (!company.finance && !company.history)) return `<p class="empty">${esc(t("company.noData"))}</p>`;
  return historyHtml(company.history) + financeHtml(company.finance) + `<p class="hint">${esc(t("company.hint"))}</p>`;
}

function historyHtml(h) {
  if (!h || Array.isArray(h)) return ""; // pusta tabela z Lua przychodzi jako []
  const money = [
    { key: "company.balance", points: h.companyBalance, color: "var(--accent)" },
    { key: "company.value", points: h.companyTotalValue, color: "var(--industry-raw)" },
  ].filter((s) => s.points?.length);
  const residents = h.townResidentialCount?.length
    ? [{ key: "company.residents", points: h.townResidentialCount, color: "var(--town)" }] : [];
  if (!money.length && !residents.length) return "";
  return `<h3>${esc(t("company.history"))}</h3>`
    + (money.length ? chartBlock(money, fmtMoneyShort, (v) => fmtMoney(Math.round(v))) : "")
    + (residents.length ? chartBlock(residents, fmtShort, fmtInt) : "");
}

// axisFmt: etykiety osi (skrócone), fmt: ostatnia wartość w legendzie (pełna).
function chartBlock(series, axisFmt, fmt) {
  const legend = series.map((s) => {
    const last = s.points[s.points.length - 1][1];
    return `<li><span class="lg-line" style="background:${s.color}"></span>${esc(t(s.key))}<b>${esc(fmt(last))}</b></li>`;
  }).join("");
  return `<figure class="chart">${lineChart(series, axisFmt)}<ul class="chart-legend">${legend}</ul></figure>`;
}

// Wykres liniowy SVG: oś x = lata (punkty [rok z ułamkiem, wartość]), oś y od min do max wszystkich serii
// (z zerem, jeśli wartości mają różne znaki). Skaluje się do szerokości panelu (viewBox).
function lineChart(series, fmt, W = 320, H = 130) {
  const pad = { l: 4, r: 4, t: 8, b: 18 };
  const all = series.flatMap((s) => s.points);
  let x0 = Math.min(...all.map((p) => p[0])), x1 = Math.max(...all.map((p) => p[0]));
  let y0 = Math.min(...all.map((p) => p[1])), y1 = Math.max(...all.map((p) => p[1]));
  if (y0 > 0 && y0 < y1 * 0.5) y0 = 0; // mała wartość minimalna - oś od zera czyta się łatwiej
  if (x1 === x0) x1 = x0 + 1;
  if (y1 === y0) { y1 += 1; y0 -= 1; }
  const sx = (x) => pad.l + (x - x0) / (x1 - x0) * (W - pad.l - pad.r);
  const sy = (y) => pad.t + (y1 - y) / (y1 - y0) * (H - pad.t - pad.b);

  const grid = [y1, (y0 + y1) / 2, y0].map((v) =>
    `<line x1="${pad.l}" x2="${W - pad.r}" y1="${sy(v)}" y2="${sy(v)}" class="chart-grid"/>`
    + `<text x="${pad.l + 2}" y="${sy(v) - 3}" class="chart-label">${esc(fmt(v))}</text>`).join("");
  const zero = y0 < 0 && y1 > 0 ? `<line x1="${pad.l}" x2="${W - pad.r}" y1="${sy(0)}" y2="${sy(0)}" class="chart-zero"/>` : "";
  // lata na osi x: co 1, 2, 5, 10... tak, żeby było najwyżej ~6 etykiet
  const span = x1 - x0;
  const step = [1, 2, 5, 10, 20, 50].find((s) => span / s <= 6) ?? 100;
  let years = "";
  for (let y = Math.ceil(x0 / step) * step; y <= x1; y += step) {
    const x = sx(y);
    const anchor = x < 16 ? "start" : x > W - 16 ? "end" : "middle"; // skrajne lata nie wychodzą poza wykres
    years += `<text x="${x}" y="${H - 4}" class="chart-label" text-anchor="${anchor}">${y}</text>`;
  }
  const lines = series.map((s) =>
    `<polyline points="${s.points.map((p) => `${sx(p[0]).toFixed(1)},${sy(p[1]).toFixed(1)}`).join(" ")}" style="stroke:${s.color}" class="chart-line"/>`).join("");
  return `<svg viewBox="0 0 ${W} ${H}" class="chart-svg" role="img">${grid}${zero}${years}${lines}</svg>`;
}

function financeHtml(f) {
  const n = f?.header?.length ?? 0;
  if (!n) return "";
  const rows = f.rows ?? [];
  const cell = (v) => (v == null ? "<td>–</td>"
    : `<td class="${v < 0 ? "neg" : v > 0 ? "pos" : ""}" title="${esc(fmtMoney(v))}">${esc(fmtMoneyShort(v))}</td>`);
  // Najnowszy rok w pierwszej kolumnie - w wąskim panelu widać go bez przewijania.
  const tr = (label, values, cls = "") =>
    (values ? `<tr class="${cls}"><th>${label}</th>${[...values].reverse().map(cell).join("")}</tr>` : "");
  const head = `<thead><tr><th></th>${[...f.header].reverse().map((h) => `<th>${esc(h)}</th>`).join("")}</tr></thead>`;

  let body = FIN_ROWS.map(([key, match]) => tr(esc(t(key)), sumRows(rows, n, match))).join("");
  if (f.interest?.some((v) => v)) body += tr(esc(t("fin.interest")), f.interest);
  body += tr(esc(t("fin.total")), f.total, "fin-total");
  body += tr(esc(t("company.balance")), f.balance, "fin-balance");
  if (f.loan?.some((v) => v)) body += tr(esc(t("fin.loan")), f.loan.map((v) => -v));

  const carriers = CARRIER_ORDER.map((c) => [c, sumRows(rows, n, (r) => r.type === "INCOME" && r.carrier === c)])
    .filter(([, v]) => v?.some((x) => x));
  const byCarrier = carriers.length
    ? `<h3>${esc(t("company.incomeByCarrier"))}</h3><div class="fin-wrap"><table class="fin">`
      + `${head}<tbody>`
      + carriers.map(([c, v]) => tr(`${carrierIcon(c, c)} ${esc(carrierName(c) ?? c)}`, v)).join("")
      + `</tbody></table></div>`
    : "";

  return `<h3>${esc(t("company.finances"))}</h3><div class="fin-wrap"><table class="fin">`
    + `${head}<tbody>${body}</tbody></table></div>${byCarrier}`;
}

/** Sparkline SVG salda (ostatnie lata historii) do górnego paska; "" bez danych. */
export function balanceSparkline(company, years = 3, W = 72, H = 22) {
  const pts = company?.history?.companyBalance;
  if (!Array.isArray(pts) || pts.length < 2) return "";
  const end = pts[pts.length - 1][0];
  const recent = pts.filter((p) => p[0] >= end - years);
  const use = recent.length >= 2 ? recent : pts;
  const xs = use.map((p) => p[0]), ys = use.map((p) => p[1]);
  const x0 = Math.min(...xs), x1 = Math.max(...xs) || x0 + 1;
  const y0 = Math.min(...ys), y1 = Math.max(...ys);
  const sx = (x) => (x - x0) / ((x1 - x0) || 1) * (W - 2) + 1;
  const sy = (y) => (y1 === y0 ? H / 2 : (y1 - y) / (y1 - y0) * (H - 4) + 2);
  const up = ys[ys.length - 1] >= ys[0];
  return `<svg class="spark ${up ? "up" : "down"}" viewBox="0 0 ${W} ${H}" width="${W}" height="${H}" aria-hidden="true">`
    + `<polyline points="${use.map((p) => `${sx(p[0]).toFixed(1)},${sy(p[1]).toFixed(1)}`).join(" ")}"/></svg>`;
}
