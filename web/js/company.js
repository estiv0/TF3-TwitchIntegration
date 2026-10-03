// Zakładka Firma: finanse wg lat (tabela z okna Finanse w grze) i historia z logbooka świata
// (stan konta, wartość firmy, dzielnice miast). Dane: dynamic.company (lua_mod: collectors.financeTable /
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
  // jak wykres "Miasta" w grze: pojemność dzielnic wszystkich miast
  const towns = [
    { key: "company.residential", points: h.townResidentialCount, color: "var(--live)" },
    { key: "company.commercial", points: h.townCommercialCount, color: "var(--industry-raw)" },
    { key: "company.industrial", points: h.townIndustrialCount, color: "var(--stale)" },
  ].filter((s) => s.points?.length);
  if (!money.length && !towns.length) return "";
  return `<h3>${esc(t("company.history"))}</h3>`
    + (money.length ? chartBlock("company.chartBalance", money, fmtMoneyShort, (v) => fmtMoney(Math.round(v))) : "")
    + (towns.length ? chartBlock("company.chartTowns", towns, fmtShort, fmtInt) : "");
}

// axisFmt: etykiety osi (skrócone), fmt: ostatnia wartość w legendzie (pełna).
function chartBlock(titleKey, series, axisFmt, fmt) {
  const legend = series.map((s) => {
    const last = s.points[s.points.length - 1][1];
    return `<li><span class="lg-line" style="background:${s.color}"></span>${esc(t(s.key))}<b>${esc(fmt(last))}</b></li>`;
  }).join("");
  return `<figure class="chart"><figcaption>${esc(t(titleKey))}</figcaption>`
    + `${lineChart(series, axisFmt)}<ul class="chart-legend">${legend}</ul></figure>`;
}

// Krok podziałki osi y: 1, 2, 2.5 albo 5 razy potęga 10, tak żeby wyszło ok. count przedziałów.
function niceStep(range, count) {
  const raw = range / count, p = 10 ** Math.floor(Math.log10(raw));
  return ([1, 2, 2.5, 5, 10].find((m) => m * p >= raw) ?? 10) * p;
}

// Wykres liniowy SVG: oś x = lata (punkty [rok z ułamkiem, wartość]), oś y na okrągłych wartościach
// od min do max wszystkich serii (z zerem, jeśli wartości mają różne znaki). Etykiety osi y po lewej,
// poza obszarem wykresu, lata pod nim. Skaluje się do szerokości panelu (viewBox).
function lineChart(series, fmt, W = 320, H = 150) {
  const all = series.flatMap((s) => s.points);
  let x0 = Math.min(...all.map((p) => p[0])), x1 = Math.max(...all.map((p) => p[0]));
  let y0 = Math.min(...all.map((p) => p[1])), y1 = Math.max(...all.map((p) => p[1]));
  if (y0 > 0 && y0 < y1 * 0.5) y0 = 0; // mała wartość minimalna - oś od zera czyta się łatwiej
  if (x1 === x0) x1 = x0 + 1;
  if (y1 === y0) { y1 += 1; y0 -= 1; }
  const step = niceStep(y1 - y0, 4);
  y0 = Math.floor(y0 / step) * step;
  y1 = Math.ceil(y1 / step) * step;
  const ticks = [];
  for (let v = y0; v <= y1 + step / 2; v += step) ticks.push(Math.abs(v) < step / 2 ? 0 : v);

  // lewy margines na najdłuższą etykietę (ok. 5 px na znak przy 9 px fontu)
  const labels = ticks.map((v) => fmt(v));
  const pad = { l: Math.max(...labels.map((s) => s.length)) * 5 + 10, r: 8, t: 8, b: 20 };
  const sx = (x) => pad.l + (x - x0) / (x1 - x0) * (W - pad.l - pad.r);
  const sy = (y) => pad.t + (y1 - y) / (y1 - y0) * (H - pad.t - pad.b);

  const grid = ticks.map((v, i) =>
    `<line x1="${pad.l}" x2="${W - pad.r}" y1="${sy(v)}" y2="${sy(v)}" class="${v === 0 && y0 < 0 ? "chart-zero" : "chart-grid"}"/>`
    + `<text x="${pad.l - 5}" y="${sy(v)}" class="chart-label" text-anchor="end" dominant-baseline="middle">${esc(labels[i])}</text>`).join("");
  // lata na osi x: co 1, 2, 5, 10... tak, żeby było najwyżej ~6 etykiet; przy każdym pionowa linia siatki
  const span = x1 - x0;
  const yearStep = [1, 2, 5, 10, 20, 50].find((s) => span / s <= 6) ?? 100;
  let years = "";
  for (let y = Math.ceil(x0 / yearStep) * yearStep; y <= x1; y += yearStep) {
    const x = sx(y);
    years += `<line x1="${x}" x2="${x}" y1="${pad.t}" y2="${H - pad.b}" class="chart-grid"/>`
      + `<text x="${x}" y="${H - 5}" class="chart-label" text-anchor="middle">${y}</text>`;
  }
  const lines = series.map((s) =>
    `<polyline points="${s.points.map((p) => `${sx(p[0]).toFixed(1)},${sy(p[1]).toFixed(1)}`).join(" ")}" style="stroke:${s.color}" class="chart-line"/>`).join("");
  return `<svg viewBox="0 0 ${W} ${H}" class="chart-svg" role="img">${grid}${years}${lines}</svg>`;
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
