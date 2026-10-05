// Budowa za punkty kanału: widz wskazuje miejsce na mapie, wybiera miasto albo zakład i nazwę,
// a strona daje kod do wklejenia w nagrodę na Twitchu (Twitch nie ma linku, który wypełnia tekst nagrody).
// Tf3Bridge czyta kod (middle_app/src/Tf3Bridge/Rewards/BuildCode.cs), sprawdza miejsce jeszcze raz
// i wysyła komendę do moda (lua_mod/content/tf3export/create.lua).
//
// Kod: "[x, y] Nazwa" (miasto) albo "[x, y, rodzaj] Nazwa" (zakład, np. coal_mine); x, y w metrach gry.
// Teren (woda, zbocza, regiony jak góry) i czerwona nakładka zakazanych miejsc: ground.js.

import { config } from "../config.js";
import { dict, locale, t } from "./i18n.js";
import { Ground, ON_WATER, TOWN_MAX_STEEP, TOWN_MAX_WATER } from "./ground.js";

// Te same liczby co w BuildCode.cs: tu tylko podpowiedź, rozstrzyga Tf3Bridge.
const MIN_NAME = 2, MAX_NAME = 40;
const TOWN_FROM_TOWN = 1500, TOWN_FROM_INDUSTRY = 600, INDUSTRY_FROM_TOWN = 800, INDUSTRY_FROM_INDUSTRY = 600, MAP_MARGIN = 300;
const pct = (share) => Math.round(share * 100);
// Kierunki co 45°, od północy zgodnie z zegarem (klucze dir.* w i18n).
const DIRECTIONS = ["n", "ne", "e", "se", "s", "sw", "w", "nw"];

/** "coal_mine" z "::/industries/coal_mine/coal_mine.con"; null dla innych plików. */
export const industryType = (file) => String(file ?? "").match(/^::\/industries\/(\w+)\/\1\.con$/)?.[1] ?? null;

/** Jak TargetResolver.Normalize w Tf3Bridge: małe litery, bez ogonków, pojedyncze spacje. */
const normalize = (s) => s.normalize("NFD").replace(/\p{Mn}/gu, "").replace(/[łŁ]/g, "l").toLowerCase()
  .replace(/[\s|]+/g, " ").trim();

/** Nazwa jak w BuildCode.cs: bez znaków sterujących i "|", pojedyncze spacje. */
const cleanName = (s) => s.replace(/[\p{Cc}|]/gu, "").replace(/\s+/g, " ").trim();

/** Kod do nagrody. */
export function buildCode({ x, y, type, name }) {
  return `[${Math.round(x)}, ${Math.round(y)}${type ? `, ${type}` : ""}] ${cleanName(name)}`;
}

export class BuildController {
  /** getDynamic() - ostatni dynamic.json (nazwy już bez " | właściciel", patrz splitAllOwners). */
  constructor(map, { form, button, wrap, getDynamic }) {
    this.map = map;
    this.form = form;
    this.button = button;
    this.wrap = wrap;
    this.getDynamic = getDynamic;
    this.kindInputs = [...form.querySelectorAll("input[name=build-kind]")];
    this.typeRow = form.querySelector("[data-build-type]");
    this.type = form.querySelector("#build-type");
    this.name = form.querySelector("#build-name");
    this.check = form.querySelector("[data-build-check]");
    this.code = form.querySelector("#build-code");
    this.copyBtn = form.querySelector("[data-build-copy]");
    this.how = form.querySelector("[data-build-how]");
    this.twitch = form.querySelector("[data-build-twitch]");
    this.draft = null; // { x, y }
    this.ground = null; // Ground albo null (brak terenu)
    this.rules = {};    // build-rules.json: rodzaj zakładu -> region ("mountains", ...)
    this.nearest = null; // { key, value } - ostatnio policzone najbliższe dozwolone miejsce

    if (config.twitchUrl) this.twitch.href = config.twitchUrl;
    else this.twitch.hidden = true;

    button.addEventListener("click", () => map.setPlaceMode(map.placeMode === "build" ? null : "build"));
    for (const input of this.kindInputs) input.addEventListener("change", () => this.update());
    this.type.addEventListener("change", () => this.update());
    this.name.addEventListener("input", () => this.update());
    form.addEventListener("submit", (e) => {
      e.preventDefault();
      this.copy();
    });
    form.querySelector("[data-build-close]").addEventListener("click", () => this.close());
    this.code.addEventListener("focus", () => this.code.select());

    document.addEventListener("keydown", (e) => {
      if (e.key !== "Escape") return;
      if (map.placeMode === "build") map.setPlaceMode(null);
      else if (!form.hidden) this.close();
      else return;
      e.preventDefault(); // obsłużone - main.js nie odznacza już zaznaczenia
    });
  }

  get industry() {
    return this.kindInputs.find((i) => i.checked)?.value === "industry";
  }

  // Wywoływane przez MapView (onPlaceMode).
  modeChanged(on) {
    this.button.setAttribute("aria-pressed", String(on));
    this.showOverlay();
  }

  // Nowy teren z DataFeed: { info, sites, zones } (obrazki = ImageBitmap albo null).
  setGround({ info, sites, zones }) {
    try {
      this.ground = sites || zones ? new Ground(info, sites, zones) : null;
    } catch (err) {
      console.warn("terrain-site.png / terrain-zone.png:", err);
      this.ground = null;
    }
    this.nearest = null;
    this.showOverlay();
    this.refresh();
  }

  // Nowe build-rules.json (streamer zmienił, gdzie wolno stawiać zakłady).
  setRules(rules) {
    this.rules = rules?.industries ?? {};
    this.nearest = null;
    this.fillTypes();
    this.showOverlay();
    this.refresh();
  }

  /** Region, który ogranicza ten rodzaj zakładu na tej mapie; null = żaden. */
  zoneOf(type) {
    return type ? this.ground?.activeZone(this.rules[type]) ?? null : null;
  }

  get selectedType() {
    return this.industry ? this.type.value || null : null;
  }

  // Czerwone miejsca, gdzie nie wolno budować wybranego miasta albo zakładu: przy wybieraniu miejsca i w okienku budowy.
  showOverlay() {
    const building = this.map.placeMode === "build" || !this.form.hidden;
    if (this.industry && !this.type.value) this.fillTypes(); // przed pierwszym okienkiem lista rodzajów jest pusta
    const type = this.selectedType;
    this.map.setOverlay(building && this.ground && (!this.industry || type) ? this.ground.overlay(type, this.zoneOf(type)) : null);
  }

  // Wywoływane przez MapView (onPlace): miejsce budowy + formularz obok.
  place({ x, y, sx, sy }) {
    this.draft = { x, y };
    this.fillTypes();
    this.form.hidden = false;
    this.update();
    this.position(sx, sy);
    this.name.focus();
  }

  position(sx, sy) {
    const w = this.wrap.clientWidth, h = this.wrap.clientHeight;
    const fw = this.form.offsetWidth, fh = this.form.offsetHeight;
    this.form.style.left = `${Math.max(8, Math.min(sx + 16, w - fw - 8))}px`;
    this.form.style.top = `${Math.max(8, Math.min(sy - 24, h - fh - 8))}px`;
  }

  // Rodzaje zakładów, które są na tej mapie (Tf3Bridge przyjmuje tylko takie), po nazwie.
  fillTypes() {
    const keep = this.type.value;
    const types = new Set((this.getDynamic()?.industries ?? []).map((i) => industryType(i.file)).filter(Boolean));
    const names = dict().industry ?? {};
    const where = (k) => ON_WATER.has(k) ? t("build.water") : this.rules[k] ? t(`zone.${this.rules[k]}`) : null;
    const label = (k) => (names[k] ?? k.replace(/_/g, " ")) + (where(k) ? ` (${where(k)})` : "");
    const sorted = [...types].sort((a, b) => label(a).localeCompare(label(b)));
    this.type.replaceChildren(...sorted.map((k) => new Option(label(k), k)));
    if (types.has(keep)) this.type.value = keep;
  }

  update() {
    if (!this.draft) return;
    const industry = this.industry;
    this.typeRow.hidden = !industry;
    this.showOverlay();
    this.name.placeholder = t(industry ? "build.industryPlaceholder" : "build.townPlaceholder");
    this.how.textContent = t("build.how", { reward: t(industry ? "build.rewardIndustry" : "build.rewardTown") });

    const name = cleanName(this.name.value);
    const type = industry ? this.type.value || null : null;
    const problem = this.problem(name, industry, type);
    this.check.textContent = problem ?? t("build.ok");
    this.check.classList.toggle("neg", !!problem);
    // Na wodzie i na zboczach kodu nie dajemy: gra i tak by tu nic nie zbudowała.
    const ready = name.length >= MIN_NAME && (!industry || type) && !this.groundProblem(industry, type);
    this.code.value = ready ? buildCode({ ...this.draft, type, name }) : "";
    this.copyBtn.disabled = !ready;
    this.copyBtn.textContent = t("build.copy");
    this.map.setMarker({
      ...this.draft,
      label: name || t(industry ? "build.industry" : "build.town"),
      radius: industry ? INDUSTRY_FROM_TOWN : TOWN_FROM_TOWN, // największa z odległości: okrąg, w którym nie powinno być miast
      ok: !problem,
    });
  }

  // Pierwszy powód, dla którego Tf3Bridge odrzuci kod (i odda punkty); null = wygląda dobrze.
  problem(name, industry, type) {
    const dynamic = this.getDynamic();
    if (!dynamic) return t("build.noData");
    if (industry && !type) return t("build.noTypes");

    const ter = this.map.terrain;
    if (ter) {
      const { x, y } = this.draft;
      const minX = ter.x, maxX = ter.x + ter.w, maxY = -ter.y, minY = maxY - ter.h;
      if (x < minX + MAP_MARGIN || x > maxX - MAP_MARGIN || y < minY + MAP_MARGIN || y > maxY - MAP_MARGIN) return t("build.edge");
    }

    const ground = this.groundProblem(industry, type);
    if (ground) return ground;
    if (name.length < MIN_NAME) return t("build.noName", { n: MIN_NAME });

    const key = normalize(name);
    const same = (industry ? dynamic.industries : dynamic.towns)?.find((o) => normalize(o.name ?? "") === key);
    if (same) return t("build.taken", { name: same.name });

    const [fromTown, fromIndustry] = industry ? [INDUSTRY_FROM_TOWN, INDUSTRY_FROM_INDUSTRY] : [TOWN_FROM_TOWN, TOWN_FROM_INDUSTRY];
    return this.tooClose(dynamic.towns, fromTown) ?? this.tooClose(dynamic.industries, fromIndustry);
  }

  // Woda, zbocza albo zły region w miejscu budowy; null = w porządku albo nie wiadomo.
  groundProblem(industry, type) {
    const ground = this.ground;
    if (!ground) return null;
    const { x, y } = this.draft;
    const k = ground.index(x, y);
    if (k < 0) return null;
    let problem = null;
    if (industry) {
      const zone = this.zoneOf(type);
      if (ground.sites && ON_WATER.has(type) && !ground.onWater(k)) problem = t("build.needsWater");
      else if (ground.sites && !ON_WATER.has(type) && ground.onWater(k)) problem = t("build.industryOnWater");
      else if (zone && !ground.inZone(k, zone)) {
        const name = dict().industry?.[type] ?? type.replace(/_/g, " ");
        problem = t("build.zone", { name, where: t(`zone.${zone}`) });
      }
    } else {
      const site = ground.site(x, y);
      if (site && (site.onWater || site.water > TOWN_MAX_WATER)) problem = t("build.onWater", { p: pct(site.water) });
      else if (site && site.steep > TOWN_MAX_STEEP) problem = t("build.steep", { p: pct(site.steep) });
    }
    return problem && `${problem} ${this.nearestHint(type)}`.trim();
  }

  // "Najbliżej: 3,2 km na północny wschód." - gdzie najbliżej wolno postawić to, co wybrano.
  nearestHint(type) {
    const zone = this.zoneOf(type);
    const key = `${this.draft.x},${this.draft.y},${type},${zone}`;
    if (this.nearest?.key !== key) {
      this.nearest = { key, value: this.ground.nearestAllowed(this.draft.x, this.draft.y, type, zone) };
    }
    const n = this.nearest.value;
    if (!n) return t("build.nowhere");
    const d = Math.hypot(n.dx, n.dy);
    const angle = (Math.atan2(n.dx, n.dy) * 180) / Math.PI; // 0 = północ, zgodnie z zegarem
    const dir = DIRECTIONS[Math.round(((angle + 360) % 360) / 45) % 8];
    const km = new Intl.NumberFormat(locale(), { maximumFractionDigits: 1 }).format(Math.max(0.1, d / 1000));
    return t("build.nearest", { km, dir: t(`dir.${dir}`) });
  }

  tooClose(items, min) {
    let best = null, bestD = Infinity;
    for (const o of items ?? []) {
      if (!o.pos) continue;
      const d = Math.hypot(o.pos[0] - this.draft.x, o.pos[1] - this.draft.y);
      if (d < bestD) { best = o; bestD = d; }
    }
    return best && bestD < min ? t("build.tooClose", { name: best.name, d: Math.round(bestD), min }) : null;
  }

  // Nowe dane albo inny język: rodzaje zakładów i sprawdzenie miejsca w otwartym okienku od nowa.
  refresh() {
    if (this.form.hidden || !this.draft) return;
    this.fillTypes();
    this.update();
  }

  // Kopiuje kod; bez schowka (np. http) zostaje zaznaczony do ręcznego Ctrl+C.
  async copy() {
    if (!this.code.value) return;
    this.code.select();
    try {
      await navigator.clipboard.writeText(this.code.value);
      this.copyBtn.textContent = t("pin.copied");
    } catch {
      this.copyBtn.textContent = "Ctrl+C";
    }
  }

  close() {
    this.form.hidden = true;
    this.draft = null;
    this.map.setMarker(null);
    this.showOverlay();
  }
}
