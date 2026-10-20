# TF3 Live: website

A static website (HTML, CSS and JS modules, no build step) that shows the game data: the network map, towns, industries, lines and the company. It's meant to run on Azure Static Web Apps. It doesn't depend on Tf3Bridge; only the JSON format and the data address connect them.

The interface is in English and Polish (`js/i18n.js`). It picks Polish for browsers set to Polish and English for everyone else; `?lang=pl|en` and the language switch override it.

## Where the data comes from

`config.js` → `dataBaseUrl`:
- **locally:** `data/latest/`. Tf3Bridge in Development mode publishes to `web/data/latest/` (the folder is in `.gitignore`);
- **on Azure:** `dataUrl` from `site.json`, i.e. the Blob container URL, e.g. `https://<account>.blob.core.windows.net/live/latest/`.

Every `pollMs` (5 s) the website downloads `meta.json` (it doesn't poll in a hidden tab). It only downloads `dynamic.json` and `network.json` when their `seq` changes.

## Streamer settings (`site.json`)

The site name, logo, channel and data address live in `web/site.json`. The file isn't in the repository (it's in `.gitignore`); copy the `site.example.json` template:

| Field | Meaning |
|---|---|
| `title` | name in the top bar and the tab title; default "TF3 Live" |
| `logo` | an image before the name, e.g. `img/site/logo.png` (the `img/site/` folder is also in `.gitignore`); empty = no logo |
| `twitchUrl` | `https://www.twitch.tv/<channel>`: the stream button with a mini player and a link in the build window; empty = no stream |
| `dataUrl` | the data address outside localhost (see above) |

Without `site.json` the website works with the default name, no logo and no stream.

## Game icons

Vehicle, cargo and station icons (`web/img/vehicles`, `resources`, `stations`) are Urban Games artwork, so they aren't in the repository. A script (Python 3, no extra packages) extracts them from the game files, once after cloning and after game updates:

```powershell
python tools\extract_game_icons.py "<game folder>"
```

Without an argument the script uses `TF3_GAME_DIR` or the default Steam library. `infra/deploy-web.ps1` won't deploy the site without the icons.

## Running locally

1. Run Tf3Bridge (`middle_app`) so the data goes to `web/data/latest/`. It also serves this folder at http://localhost:5080 (the **Website** button), so that's all you need.
2. Without Tf3Bridge (e.g. working on the site with old data), serve the `web/` folder with a static server:

   ```powershell
   .\web\serve.ps1
   ```

   The script runs the `dotnet-serve` tool (installing it if needed) and sets `DOTNET_ROOT` for it, for an SDK in the user folder. It uses the same port, so close Tf3Bridge first or pass `-Port`. Any other static server works too, e.g. the Live Server extension in VS Code.
3. Open http://localhost:5080.

Opening `index.html` straight from disk won't work, because the browser blocks `fetch` from `file://`.

## Structure

```
web/
  index.html        layout: top bar, map, side panel
  config.js         data address, intervals, settings from site.json
  site.example.json template for site.json (name, logo, channel, data address)
  css/style.css     dark theme (the map uses the CSS variables too)
  js/main.js        ties together the data, the map and the panel
  js/api.js         DataFeed: polling meta.json, downloading dynamic/network when seq changes
  js/map.js         MapView: canvas, the network as Bézier curves (from TF tangents), pan/zoom/hover/select
  js/sidebar.js     lists of towns, industry, stations, lines and owners, details of the selection
  js/owners.js      viewers' estates: objects named "Name | Viewer" grouped by viewer, main town = the largest
  js/pin.js         a pin with a link (#pin=x_y_scale&note=description)
  js/build.js       building for channel points: a code for the Twitch reward
  js/ground.js      terrain for building: water and slopes for towns, regions (mountains, lowland...) for industries
  js/company.js     the Company tab (finances by year, history charts) and the balance sparkline in the top bar
  js/format.js      formatting numbers and dates, cargo and carrier names
  js/i18n.js        translations (EN / PL) and language detection
```

## Map

- A TF edge is a Hermite curve (positions + tangents). On the canvas it's drawn as a Bézier curve with control points `p0 + t0/3` and `p1 - t1/3`.
- The network is built once into a `Path2D` in world coordinates. Pan and zoom only change the transform, so about 9k segments draw smoothly.
- The game's y axis grows to the north, and on screen downwards, so the map uses `(x, -y)`.
- Vehicles: the mod sends their position (`vehicles[].pos`). A position near the line route (up to 40 m from the segment leading to `stop`) gives movement along the route, any other (ships, planes) a straight line. Without `pos` (positions turned off in the mod or an older export) the location is guessed: `AT_TERMINAL` at the stop, `EN_ROUTE` on the route from the previous stop to `stop`, several vehicles on a segment spread evenly (on a long segment the vehicle then stands still).
- Vehicle movement is interpolated: after a new data batch a vehicle travels from where it is on screen to the new position over the average interval between batches. So the picture is one batch (about 10–15 s) behind. The animation runs at 30 fps.
- Line route: a stop is snapped to a network node in the same connected piece as the neighboring stop, because the export has cut-off road fragments (station access roads).
- The selection is in the address (`#line=<id>`, `#town=`, `#industry=`, `#station=`, and a viewer's estate `#owner=<name in lower case>`), next to the pin: `#pin=...&note=...&line=<id>`. Selecting an owner doesn't move the map. For an object that isn't in the data yet (a chat link to a freshly built town), the website watches the next 6 data batches.
- On touch: pan with one finger, pinch to zoom.

## Building for channel points

The 🏗 button on the map (`build.js`): the viewer clicks a spot, picks a town or an industry (kinds: the ones in `dynamic.json`), types a name and copies the code `[x, y] Name` or `[x, y, coal_mine] Name`. They paste the code into the "new town" / "new industry" reward on Twitch (Twitch has no link that fills in the reward text). Tf3Bridge builds it in the game ([../middle_app/README.md](../middle_app/README.md#channel-points-rewards)).

- The website warns right away about what Tf3Bridge will reject: too close to a town or industry, at the map edge, a name already taken. The numbers are the same as in `BuildCode.cs`; on the map a dashed circle shows the minimum distance from towns.
- Water and mountains: from the heightmap Tf3Bridge computes for every point what share of the town area (a ±250 m square) is water and what share is slopes steeper than 15%, and publishes this as `terrain-site.png` (R = water, G = slopes, B = 255 under water; a pixel = a sample as in `terrain.png`). A town has to be on land, with at most 30% water and 50% slopes around it (the same thresholds as in `TownSites.cs`); otherwise the game founds it but places neither a street nor a house. While choosing a spot such areas are red on the map, and the website gives no code. Industry: the oil platform, fishing grounds and sand dredger only on water, the rest only on land.
- Regions for industries (`ground.js`): Tf3Bridge publishes `terrain-zone.png` (R = mountains, G = water within about 1 km, B = flat lowland) and `build-rules.json` (`{ "industries": { "coal_mine": "mountains", ... } }`, the streamer's settings; a change bumps `meta.rulesVersion`). After an industry kind is chosen, everything where it may not be placed is red, the list of kinds says "(in the mountains)" etc., and for a wrong spot the website gives no code and suggests how far and in which direction the nearest allowed spot is. A region the map doesn't have (e.g. mountains on a flat map) isn't checked.
- The website doesn't check collisions with tracks, streets and buildings; the game does.
- `twitchUrl` in `site.json` (the channel address) adds an "Open Twitch" link to the window.

## Effects from viewers

`dynamic.effects` (from the mod, `effects.lua`) are the active effects from channel points rewards: `{ type, entity, user, factor, startTime, untilTime, until: {year, month, day} }`.

- `attachEffects` (`format.js`) attaches them to towns (`townBoost`), industries (`industryBoost`, `industryStrike`, `industryTaxRaid`) and lines (`lineStop`, `lineHold`, `lineFuelPremium`, `lineFuelBad`) as `x.effects`. It also adds `progress`, i.e. how much time has passed, computed from the export's `gameTime`.
- In the list: a badge next to the name (⚡ boost, ⛔ line stopped, ⏸ line waits, ✊ strike, 🔍 tax raid, ⛽ premium fuel, 💧 watered-down fuel; a decrease with a − sign, e.g. "Strike −99%") with a description in the tooltip.
- In the details: an "Effects from viewers" section with the strength, the end date in the game calendar, the buying viewer and a remaining-time bar.
- An older export without `effects` works as before.

## Deploying to Azure

The website runs on **Azure Static Web Apps** (Free plan: custom domain and HTTPS for free). It reads the data from the `live` container of the Storage account (`dataUrl` in `site.json`). Deploying: [../infra/deploy-web.ps1](../infra/deploy-web.ps1), needs Node.js (`winget install OpenJS.NodeJS.LTS`).

1. Portal → Static Web App → **Overview → Manage deployment token**, copy the token.
2. The first time: `.\infra\deploy-web.ps1 -DeployToken "<token>"` (the token is remembered, encrypted, in `%LOCALAPPDATA%\Tf3Bridge`).
3. Next times: `.\infra\deploy-web.ps1`. `-DryRun` only shows the file list.

The script uploads the whole site except `data\`, `img_res\`, `serve.ps1`, `README.md` and `site.example.json` (`site.json` and the game icons are taken from disk), using the Static Web Apps CLI (`npx @azure/static-web-apps-cli`).

As a fallback the website can also run as a Storage account "Static website" (the `$web` container, address `https://<account>.z<N>.web.core.windows.net/`, no HTTPS for a custom domain): `.\infra\deploy-web.ps1 -Target storage -SasUrl "<Blob SAS URL of the $web container>"` (SAS with Create + Write rights). This variant only uploads changed files (`-Force` all of them) and sets the content types and `Cache-Control` itself.

## Debug

In the browser console: `tf3.map` (view, data) and `tf3.sidebar`.
