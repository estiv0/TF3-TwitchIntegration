# TF3: findings from the documentation and game files

Status as of 2026-10-02. Sources: the script-doc on the TF3 wiki, the wiki pages about mods, and the game files in `<game>`.

**Conclusion:** `lua_mod` had the TF2 structure and would not load in TF3. The mod has since been rebuilt to the TF3 structure; the status of each point is in [mod-known-issues.md](mod-known-issues.md).

Locations on a PC:
- staging area for mods under development: `C:\Program Files (x86)\Steam\userdata\<SteamID>\3493540\local\staging_area\`
- manually installed mods: `...\3493540\local\mods\`
- game log: probably `...\3493540\local\crash_dump\stdout.txt`

## 1. Mod structure

TF3 doesn't use `mod.lua` with `function data()`. The required layout ([wiki: Mod Definition](https://wiki.transportfever3.com/doku.php?id=modding:general:moddefinition)):

```
mods/
  author_tf3export/             folder name doesn't matter, recommended = modId
    mod.json                    technical definition (modId, revision, severity, scripts)
    _metadata/
      modinfo.json              name, description, authors, tags (["Script Mod"])
    content/                    mod resources, same structure as base/content
      ...
    _content.json               file list; present in the game's own mods (to check whether the game generates it itself)
```

- `modId` may only contain `a-z0-9_`. **There is no `_1` suffix anymore** (the game's own mods still have it, e.g. `urbangames_sandbox_1`, but the wiki says it's not required).
- Manually installed mods: a `mods/` folder (to determine whether in the game install or in the user profile). The game has `mods/release/` with Urban Games mods.

Example from the game (`mods/release/urbangames_sandbox/mod.json`):

```json
{
    "modId": "urbangames_sandbox_1",
    "revision": 1,
    "preRunScript": { "fileName": "urbangames_sandbox_1::/mod.script@preRunFn" },
    "runScript": { "fileName": "" },
    "postRunScript": { "fileName": "" },
    "severityAdd": "None",
    "severityRemove": "None"
}
```

## 2. Game script: a completely different mechanism

TF2 had `res/config/game_script/*.lua` with `data()` returning `{ save, load, update, guiUpdate }`.

TF3 uses a **pair of files** in `content/` (pattern: `base/content/terrain/`):

`reforestation.gs.lua` is the descriptor:
```lua
function data()
	return {
		updateScript = { fileName = "reforestation.script@update" },
		handleEventScript = { fileName = "reforestation.script@handleEvent" },
	}
end
```

`reforestation.script.tl` is the implementation (Teal; plain Lua should also work):
```lua
local ret : GameScript<State> = {
	update = function(_userParams, state : GameScriptState<State>, dt : number) : any
		if dt == 0 then return end           -- paused
		local s = state:get()
		-- ...
		state:set(s)                          -- persistent state, stored in the save
	end,
	handleEvent = function(_userParams, state, src, id, name, param) : any
		if id == "" and (name == "initNewGame" or name == "handleLegacy") then ... end
	end,
}
return ret
```

The most important differences:
- **There is no `save()` or `load()`.** Persistent state lives in `state:get()` / `state:set()` (the `GAME_SCRIPT` component), so our `state` (seq, lastDynamicTime) has to move there. The game stores it in the save itself.
- `update(userParams, state, dt)`: `dt` is the simulation step in seconds, `dt == 0` means paused.
- `handleEvent` receives `initNewGame` and `handleLegacy`, among others. Other events can be subscribed to with `state:subscribeToEvent(name)` (e.g. `ArriveAtStop`, see `content.scripts.gamescript`).
- There is a `GameScriptWithGui` variant with `guiUpdate` / `guiHandleEvent` (GUI thread, read-only state).
- **To check:** how the game discovers `*.gs.lua` from a mod. Nobody registers them explicitly in the game, so it's probably enough for the file to sit in `content/`. Other scripts refer to them as `::/landmarks/landmarks.gs`.

## 3. Require

- `require` is replaced by `ug_require "path"` ([wiki: syntax](https://wiki.transportfever3.com/doku.php?id=modding:general:syntax)).
  - `ug_require "::/scripts/x.tl"`: from the `content/` root
  - `ug_require "x.tl"`: relative to the current file
  - `ug_require "modid::/scripts/x.tl"`: a file from a specific mod
- So we keep mod libraries e.g. in `content/scripts/tf3export/*.lua` and load them with `ug_require "::/scripts/tf3export/json.lua"`.

## 4. Lua environment

- The engine is **Lua 5.2** (`TransportFever3.exe` contains the string "Lua 5.2.2"), not LuaJIT. `coroutine`, `table.concat` and `string.format` work as standard. In 5.2 `pcall` allows yield, but **you can't yield from a callback** passed to a C function (e.g. `forEachEntityWithComponent`).
- **`io` and `os`: UNKNOWN.** No game script uses them. This is the most important console test, because without `io.open` the whole JSON-file plan falls apart:
  ```lua
  print(io, os, io and io.open, os and os.rename, os and os.clock)
  ```
  Plan B if `io` is blocked: write data with `print`/`log.message` to the game log with a prefix (`[tf3export]{json...}`), and have the middle app tail the log file and parse the lines with that prefix. This trick worked in TF2 mods.
- Logging: `debugPrint(x)` (also prints tables) and `log.message/warning/error`.

## 4a. Confirmed in the game (2026-10-02)

- **A `.script.lua` file must define a global `function data() return { update = ..., handleEvent = ... } end`.** The `return {...}` style only works in `.tl`. Libraries loaded with `ug_require "x.lua"` may return a module with `return`, because they are cached in `_ug_loadedModules` (`base/init.lua`).
- **Coroutines are out.** The game's C functions, definitely `log.message`, called from a coroutine throw `Value is not a string, but a thread`. Tasks spread over many ticks have to be written as objects with a cursor.
- **`getEntitiesWithComponent(LINE)` and `(BASE_EDGE)` return `Cannot loop over this component type`.** It works for TOWN, INDUSTRY and TRANSPORT_VEHICLE, because the game scripts iterate over them with `forEachEntityWithComponent`. Replacements:
  - lines: `api.engine.system.lineSystem.getLinesForPlayer(player)`;
  - edges: `api.engine.system.streetSystem.getNode2SegmentMap()` (node → segments). There are also `getNode2StreetEdgeMap` and `getNode2TrackEdgeMap`.
- **The game script's `update()` is called from many simulation threads (`Sim Pool 1..N`), and each has its own Lua state.** Module variables (file-level `local`) are not shared: one run had 13 independent copies. The only shared state is `state:get()` / `state:set()`. So every export has to finish within a single `update()` call, and the schedule has to be kept in `state`.
- Game log: `C:\Program Files (x86)\Steam\userdata\<SteamID>\3493540\local\crash_dump\stdout.txt` (confirmed). `debugPrint` in `base/init.lua` uses `log.message`.
- **Environment probe results** (the console doesn't open in TF3, so the mod checked this):
  - simulation threads (`update`): `io=false`, `os.rename=false`, `os.remove=false`, `os.time=true`, `os.clock=true`, `app=false`;
  - GUI thread (`guiUpdate`, "Main Thread"): the same, but `app=true` and **`app.saveUserdata` works**. It writes `local\<dir>\<name>.lua` as `function data()\r\nreturn { \r\n\t\tjson = "...",\r\n\t}\r\nend`, i.e. strings as escaped Lua literals, with UTF-8 preserved.
  - Conclusion: the export runs in `guiUpdate` and writes with `app.saveUserdata`. The mod has used this since format v4.
- **Edge revisions (`getRevision`) change all the time**, even when the network doesn't. A signature based on them doesn't detect changes, so the mod compares the content of the encoded network instead.
- **Network measurement:** 8698 edges give about 1 MB of JSON. The scan takes about 20 ms and encoding 150–200 ms, so the whole thing can't be done in one tick. The mod spreads it over GUI frames, 2 ms each.
- **(Historical) Writing files with `io` is unavailable.** The first run reported `writer mode 'log'`, i.e. `io.open` or `os.rename` is missing. `os.clock` works, because the times in the log are non-zero. To confirm in the console what exactly is missing: `print(io, io and io.open, os.rename, os.remove, os.time)`. For now the data goes through the log, in `[tf3x] ...` lines of ≤8000 bytes, cut without splitting UTF-8 characters.
- Results of the first dynamic export (a map with about 75 towns, about 130 industries and about 400 vehicles): 5 ms collecting, 4 ms encoding, 102 KB result in 13 log chunks. Working: entity names, `bbox` (`min`/`max`), `getCalendarDate`, `getPlayersBalance`, enum names (`RAIL`, `EN_ROUTE`…), cargo names and `cargoNeeds`.

## 5. Engine API: what fits our code

From the [api.engine](https://wiki.transportfever3.com/script-doc/api/engine.html) page:

| Used in our code | Status in TF3 |
|---|---|
| `api.engine.getEntitiesWithComponent(type, filters)` | ✅ exists, returns `{Entity}`. There is also `forEachEntityWithComponent(fn, type)`. Filters: `requireOwnedByPlayer`, `filterFn`. |
| `api.engine.getComponent(entity, type)` | ✅ |
| `api.engine.ComponentType.X` | ✅ in the documentation, but **the game scripts use `api.type.ComponentType.X`**. Safer to switch to `api.type`. |
| `api.engine.util.getWorld()`, `getPlayer()` | ✅. There is also `getEntityName(e)`, which can replace the NAME component. |
| `GAME_TIME.gameTime` | ✅ (int). There are also `tickCount` and `dates`. |
| `BASE_EDGE.position0/1, tangent0/1, node0/1` | ✅ **present in TF3** (TF2 didn't have them). Plus `distance`, `roadType`, `roadTemplate`. |
| `BASE_EDGE.type` | ⚠️ type `BaseEdgeType`, probably normal edge / bridge / tunnel, not street / track. A `BASE_EDGE_TRACK` component **doesn't exist**, there is only `BASE_EDGE_STREET`. We'll recognize streets by the presence of `BASE_EDGE_STREET`, tracks by its absence or by `roadType`. Needs checking. |
| `BOUNDING_VOLUME.bbox` | ✅ type `Box3f`, field names (`min`/`max`?) to check with `debugPrint`. |
| `TOWN`: noise, pollution, developmentActive, initialLandUseCapacities, cargoNeeds | ✅ all exist. **`initialLandUseCapacities` are the starting values and don't grow with the town** (Szczecin: 457 vs ~1800 in the town window). Current district capacities come from `townBuildingSystem.getTown2personCapacitiesMap()` (town → {residential, commercial, industrial}); the game also has `town_util.getTownSize`, but its source is packed |
| `INDUSTRY`: level, maxLevel, upgradeProgress, manualDevelopment, construction, stockList | ✅ |
| `STOCK_LIST`: rules (input, output, capacity, booster), stocks (cargoType, capacity), modifiers.productivity | ✅ |
| `TRANSPORT_VEHICLE`: line, stopIndex, userStopped, carrier | ✅. Plus state, loadState, closestTown, sectionTimes… |
| `LINE.stops[].stationGroup/station/terminal` | ✅ |
| `COLOR.color`, `PLAYER_OWNED.player`, `NAME.name` | ✅ |

### Useful things we don't use yet

- **Time:** `GAME_SPEED.millisPerDay` (a component on the world) and `api.util.getDefaultDayDuration()`. Day length **depends on the calendar speed settings**, so `cfg.gameDay` shouldn't be a constant but a value read from the game.
- **Change detection:** `api.engine.getRevision(entity)` returns the entity's revision number. Better than counting edges (see known issues #7).
- **In-game date:** `api.engine.util.getCalendarDate(gameTime)` and `getYear()`, useful for display on the website.
- **Ready-made game statistics** (`api.engine.util.*`):
  - `finance.getPlayersBalance(player)`, `finance.calculateEarnings(player)`
  - `stock.getCargoShippedPerYear(stockList)`, `getCargoDeliveredPerYear(...)`
  - `line.getLineCapacityUsages(line)`, `line.getLineProblems()`. Note: despite the `{CargoTypeId : ...}` type in tealdef, the result is a 1-based Lua array, so `CargoTypeId = index - 1` (that's how `gui/main/cargo_util.tl` computes it). Without that, passengers (id 0) come out as grain (id 1).
  - `industry.getIndustryProductivityInfo(e)`
  - `headquarters.getTransportedData()`, `getCompaniesValue()`
  - `logbook.getLogValueMostRecent(e, name)`
  - `logbook.getChart(e, names)` (confirmed in the log): `series[i]` is a **pair of lists** `{ game times (ms), values }`, not a list of `{x, y}` points as the `{{{number}}}` type suggests. Without a `ChartConfig` each series has 50 points from the start of the game until now, values averaged per bucket. `xRange`/`yRange` are `{min, max}`.
- **Systems** (`api.engine.system.*`): `getLineVehicles(line)`, `getLinesForPlayer(player)`, `getStations(town)`, `getNodeTrackSegments(node)` / `getNodeStreetSegments(node)` (this settles whether an edge is a track or a street).
- **Cargo names:** `api.res.cargoTypeRep.get(id).name` (used in `landmarks_notification.script.tl`).

### Terrain (heightmap)

From `api/tealdef/api/engine.d.tl` (`UtilTerrain`), `type.d.tl` (`GameMap`) and `app.d.tl`:

- `api.engine.terrain.getBaseHeightAt(Vec2f)`: height from the 4 m grid, without leveling (cuts and embankments for tracks and streets). `getHeightAt` includes them.
- `getBoundingBox()` returns the world extent, and `getTerrainEntityRevisions()` the revisions of the 256×256 m tiles, which can be used to detect terraforming.
- `makeMapFromGame(withHeightmap, ...)` returns a `GameMap` with a flat `heightmap` array and `numTilesX/Y`, `resolution`, `offsetZ`, `waterLevel` fields. The map editor uses it (`gui/map_editor/map_editor.tl`).
- `app.writeHeightmap(map, name, writeStl, sixteenBits, biomes)` is the native heightmap image writer, the same as the map editor export. The file goes to `userdata\heightmaps`, and `app.getAllHeightmaps()` returns the list.
- `api.gui.util.takeScreenshot(scale)` takes a perspective screenshot from the current camera, so it's no good for a 2D map.

The export uses method A (`getBaseHeightAt` every 32 m, in chunks). `terrain.bench` (`cfg.terrainBench`) measures A, B (`makeMapFromGame` and reading the array from Lua) and C (`writeHeightmap`) once per game start. The results go to the log as `[tf3export] terrain bench ...`.

Measured in the game on 2026-10-02. The map had 80×160 tiles, i.e. 20.5×41 km, `waterLevel` 0, and `TERRAIN` on the world entity works:

| Method | Result | Conclusion |
|---|---|---|
| A `getBaseHeightAt` | 1.8 µs/call (`getHeightAt` 2.25 µs). With a 32 m step that's 821k samples, about 1.5 s of CPU spread over frames | **chosen**, because it doesn't stutter the game |
| B `makeMapFromGame` | the call itself takes 55 ms, but `.heightmap` turns into a Lua table of 52M numbers in 476 ms in one frame and takes hundreds of MB | out |
| C `writeHeightmap` (16 bit) | 5.3 s in one frame, the PNG is 5121×10241 px and 38 MB | out |

- Values from `heightmap` are meters, identical to `getBaseHeightAt`. The array has a `(numTiles*64+1)` × `(numTilesY*64+1)` layout (4 m grid), row by row from min y.
- `getTerrainEntityRevisions()` works on the GUI thread. The revisions are too large for `string.format("%d")`, so the signature is built with `tostring`.

## 6. Console checklist (replaces the one from `lua_mod/README.md`)

```lua
print(io, os, io and io.open, os and os.rename, os and os.clock)                  -- 1. do we have I/O
local f = io.open("C:/TF3Export/test.txt", "w"); print(f); if f then f:write("x"); f:close() end
print(#api.engine.getEntitiesWithComponent(api.type.ComponentType.TOWN, {}))        -- 2. entity list
local t = api.engine.getEntitiesWithComponent(api.type.ComponentType.TOWN, {})[1]
debugPrint(api.engine.getComponent(t, api.type.ComponentType.BOUNDING_VOLUME))       -- 3. bbox
debugPrint(api.engine.getComponent(api.engine.util.getWorld(), api.type.ComponentType.GAME_SPEED))  -- 4. millisPerDay
debugPrint(api.engine.getComponent(api.engine.util.getWorld(), api.type.ComponentType.GAME_TIME))
local e = api.engine.getEntitiesWithComponent(api.type.ComponentType.BASE_EDGE, {})[1]
debugPrint(api.engine.getComponent(e, api.type.ComponentType.BASE_EDGE))             -- 5. type / roadType
print(api.engine.getRevision(e))                                                    -- 6. revisions
```

It also remains to check whether `#list` and 1-based indexing work on the results (point 4 of the old list). The documentation types them as plain `{Entity}` arrays plus separate `*_native: Vector<...>`, so we **probably** get plain Lua tables.

## Build 40420 (2026-10-08): app.*Userdata only in selected folders

- `app.saveUserdata` / `loadUserdata` / `getAllUserdata` now only accept the `towns_industries`, `heightmaps` and `mod_presets` folders;
  others fail with "The directory you trying to access is not available or invalid" (text from `TransportFever3.exe`, `app_script_util.cpp`).
- The game also checks file names ("Forbidden file name … another valid version could be …"); the game's save patterns are reserved
  (`autosave_…`, `crash_…`, `exitsave_…`).
- So the mod and Tf3Bridge write everything to `local\towns_industries\` with a `tf3export_` prefix
  (`util.USERDATA_DIR` / `util.FILE_PREFIX`, `SteamLocator.UserdataDir` / `FilePrefix`). `mod_presets` is out,
  because its contents show up as mod presets in the menu; `towns_industries` only shows up in the map editor import.

## Construction footprints and street widths (2026-10-26)

Probes: `docs/probes/footprint-probe.lua` and `footprint-probe2.lua` (console, output with `[probe]` / `[probe2]` in `stdout.txt`).

- `CONSTRUCTION.frozenEdges` of stations, depots and industries holds only **one** edge (the one connecting the construction to the road); harbors and warehouses have none. Useless for an outline, and harbors/warehouses didn't bump `netVersion` - since footprints are in the network export, `touchesNetwork` also reacts to constructions with `stations`, `depots` or a `/warehouses/` file.
- `BOUNDING_VOLUME.bbox` is axis-aligned in world coordinates (and its z reaches below the terrain), so it's much too big for a rotated construction.
- `CONSTRUCTION.transf` is a `Mat4f` indexable `t[1..16]`: x axis `t[1], t[2]`, y axis `t[5], t[6]`, position `t[13], t[14], t[15]`.
- **Ground lots (used):** the subconstructions (not the construction) have `LOT_LIST` (`ComponentType.LOT_LIST` = 17, missing from the Teal definitions): `lots[]` with `vertices` (`Vec2f`, world coordinates), `triangles`, `texKey`, `solid`. Solid lots are the module areas (a warehouse: `entrance.gtex`, `wh_flatbed.gtex`, `wh_bulk.gtex`; harbor/stations: `industry_floor.gtex`, asphalt, gravel; airfield: runway, hangar, terminal); non-solid ones are borders (`street_border`, `pedestrian_connection_border`, ~50-100 vertices). This matches the area the game highlights; a modular rail station's lots only cover the squares by the building, not the platforms. Probe: `footprint-probe3.lua`.
- **Outline from models (fallback):** `MODEL_INSTANCE_LIST.fatInstances` (`modelId`, `transf`) of the construction and its `subconstructions`, with `api.res.modelRep.get(modelId).boundingInfo` (`bbMin`/`bbMax`, local). `thinInstances` were empty everywhere. Examples: road depot 56×41 m, warehouse 90×82 m, modular rail station 34×202 m, modular harbor 120×204 m (with the pier), airfield 405×176 m (852 models). Its convex hull is too coarse (a T-shaped warehouse becomes a triangle), and lamps/lights have bounding boxes with the light cone (lamp post 22×25 m, `ind_light_huge` 90×82 m), so `footprint.lua` uses it only without lots, and without lamps.
- **Street width:** `BASE_EDGE.laneConfigs[i].width` is filled for every edge, sidewalks included (e.g. `[2,5,5,2]` = 14 m, `[3,5,4,4,5,3]` = 24 m, `[3,20,3]` = 26 m); tracks have one 4 m lane. `typeIndex` is -1 for streets. The sum of lane widths is the street width.
