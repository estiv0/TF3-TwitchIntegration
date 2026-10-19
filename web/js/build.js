// Building for channel points: the button opens a window (town or industry, name), and every click
// on the map while the window is open places or moves the building spot. The website gives a code to paste into the Twitch reward (Twitch has no link that fills in the reward text).
// Tf3Bridge reads the code (middle_app/src/Tf3Bridge/Rewards/BuildCode.cs), checks the spot again
// and sends a command to the mod (lua_mod/content/tf3export/create.lua).
//
// Code: "[x, y] Name" (town) or "[x, y, kind] Name" (industry, e.g. coal_mine); x, y in game meters.
// Terrain (water, slopes, regions like mountains) and the red overlay of forbidden places: ground.js.

import { config } from "../config.js";
import { dict, locale, t } from "./i18n.js";
import { Ground, ON_WATER, TOWN_MAX_STEEP, TOWN_MAX_WATER } from "./ground.js";

// The same numbers as in BuildCode.cs: here only a hint, Tf3Bridge decides.
const MIN_NAME = 2, MAX_NAME = 40;
const TOWN_FROM_TOWN = 1500, TOWN_FROM_INDUSTRY = 600, INDUSTRY_FROM_TOWN = 800, INDUSTRY_FROM_INDUSTRY = 600, MAP_MARGIN = 300;
const pct = (share) => Math.round(share * 100);
// Directions every 45°, clockwise from north (dir.* keys in i18n).
const DIRECTIONS = ["n", "ne", "e", "se", "s", "sw", "w", "nw"];

/** "coal_mine" from "::/industries/coal_mine/coal_mine.con"; null for other files. */
export const industryType = (file) => String(file ?? "").match(/^::\/industries\/(\w+)\/\1\.con$/)?.[1] ?? null;

/** Like TargetResolver.Normalize in Tf3Bridge: lower case, no diacritics, single spaces. */
const normalize = (s) => s.normalize("NFD").replace(/\p{Mn}/gu, "").replace(/[łŁ]/g, "l").toLowerCase()
  .replace(/[\s|]+/g, " ").trim();

/** A name as in BuildCode.cs: no control characters and no "|", single spaces. */
const cleanName = (s) => s.replace(/[\p{Cc}|]/gu, "").replace(/\s+/g, " ").trim();

/** The code for the reward. */
export function buildCode({ x, y, type, name }) {
  return `[${Math.round(x)}, ${Math.round(y)}${type ? `, ${type}` : ""}] ${cleanName(name)}`;
}

export class BuildController {
  /** getDynamic() - the last dynamic.json (names already without " | owner", see splitAllOwners). */
  constructor(map, { form, button, getDynamic }) {
    this.map = map;
    this.form = form;
    this.button = button;
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
    this.ground = null; // Ground or null (no terrain)
    this.rules = {};    // build-rules.json: industry kind -> region ("mountains", ...)
    this.nearest = null; // { key, value } - the last computed nearest allowed spot

    if (config.twitchUrl) this.twitch.href = config.twitchUrl;
    else this.twitch.hidden = true;

    // The "build" mode lasts as long as the window: turning it on opens it, turning it off closes it (modeChanged).
    button.addEventListener("click", () => map.setPlaceMode(map.placeMode === "build" ? null : "build"));
    for (const input of this.kindInputs) input.addEventListener("change", () => this.update());
    this.type.addEventListener("change", () => this.update());
    this.name.addEventListener("input", () => this.update());
    form.addEventListener("submit", (e) => {
      e.preventDefault();
      this.copy();
    });
    form.querySelector("[data-build-close]").addEventListener("click", () => map.setPlaceMode(null));
    this.code.addEventListener("focus", () => this.code.select());

    document.addEventListener("keydown", (e) => {
      if (e.key !== "Escape") return;
      if (map.placeMode !== "build") return;
      map.setPlaceMode(null);
      e.preventDefault(); // handled - main.js no longer clears the selection
    });
  }

  get industry() {
    return this.kindInputs.find((i) => i.checked)?.value === "industry";
  }

  // Called by MapView (onPlaceMode): the window is open exactly while the build mode is on.
  modeChanged(on) {
    this.button.setAttribute("aria-pressed", String(on));
    if (on && this.form.hidden) this.open();
    else if (!on && !this.form.hidden) this.close();
  }

  // The window right away; the spot is picked afterwards on the map (and can be changed at will).
  open() {
    this.draft = null;
    this.fillTypes();
    this.form.hidden = false;
    this.update();
    this.name.focus();
  }

  // New terrain from DataFeed: { info, sites, zones } (images = ImageBitmap or null).
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

  // A new build-rules.json (the streamer changed where industries may be placed).
  setRules(rules) {
    this.rules = rules?.industries ?? {};
    this.nearest = null;
    this.fillTypes();
    this.showOverlay();
    this.refresh();
  }

  /** The region that restricts this industry kind on this map; null = none. */
  zoneOf(type) {
    return type ? this.ground?.activeZone(this.rules[type]) ?? null : null;
  }

  get selectedType() {
    return this.industry ? this.type.value || null : null;
  }

  // Red areas where the selected town or industry may not be built: while picking the spot and in the build window.
  showOverlay() {
    const building = !this.form.hidden;
    if (this.industry && !this.type.value) this.fillTypes(); // before the first window the list of kinds is empty
    const type = this.selectedType;
    this.map.setOverlay(building && this.ground && (!this.industry || type) ? this.ground.overlay(type, this.zoneOf(type)) : null);
  }

  // Called by MapView (onPlace): a click on the map with the window open places/moves the spot.
  place({ x, y }) {
    if (this.form.hidden) return;
    this.draft = { x, y };
    this.update();
  }

  // The industry kinds present on this map (Tf3Bridge only accepts those), by name.
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
    if (this.form.hidden) return;
    const industry = this.industry;
    this.typeRow.hidden = !industry;
    this.showOverlay();
    this.name.placeholder = t(industry ? "build.industryPlaceholder" : "build.townPlaceholder");
    this.how.textContent = t("build.how", { reward: t(industry ? "build.rewardIndustry" : "build.rewardTown") });
    this.copyBtn.textContent = t("build.copy");

    if (!this.draft) {
      // No spot yet: a hint instead of the check, no code and no marker.
      this.check.textContent = t("build.pick");
      this.check.classList.remove("neg");
      this.code.value = "";
      this.copyBtn.disabled = true;
      this.map.setMarker(null);
      return;
    }

    const name = cleanName(this.name.value);
    const type = industry ? this.type.value || null : null;
    const problem = this.problem(name, industry, type);
    this.check.textContent = problem ?? t("build.ok");
    this.check.classList.toggle("neg", !!problem);
    // On water and on slopes we give no code: the game wouldn't build anything there anyway.
    const ready = name.length >= MIN_NAME && (!industry || type) && !this.groundProblem(industry, type);
    this.code.value = ready ? buildCode({ ...this.draft, type, name }) : "";
    this.copyBtn.disabled = !ready;
    this.map.setMarker({
      ...this.draft,
      label: name || t(industry ? "build.industry" : "build.town"),
      radius: industry ? INDUSTRY_FROM_TOWN : TOWN_FROM_TOWN, // the largest of the distances: a circle that shouldn't contain towns
      ok: !problem,
    });
  }

  // The first reason Tf3Bridge will reject the code (and refund the points); null = looks fine.
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

  // Water, slopes or a wrong region at the building spot; null = fine or unknown.
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

  // "Nearest: 3.2 km to the north-east." - where the nearest spot for what was chosen is allowed.
  nearestHint(type) {
    const zone = this.zoneOf(type);
    const key = `${this.draft.x},${this.draft.y},${type},${zone}`;
    if (this.nearest?.key !== key) {
      this.nearest = { key, value: this.ground.nearestAllowed(this.draft.x, this.draft.y, type, zone) };
    }
    const n = this.nearest.value;
    if (!n) return t("build.nowhere");
    const d = Math.hypot(n.dx, n.dy);
    const angle = (Math.atan2(n.dx, n.dy) * 180) / Math.PI; // 0 = north, clockwise
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

  // New data or another language: industry kinds and the spot check in the open window again.
  refresh() {
    if (this.form.hidden) return;
    this.fillTypes();
    this.update();
  }

  // Copies the code; without a clipboard (e.g. http) it stays selected for a manual Ctrl+C.
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
