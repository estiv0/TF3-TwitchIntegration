# Known bugs and risks in `lua_mod`

Status as of 2026-10-02, after the rebuild to the TF3 structure. API details are in [tf3-api-findings.md](tf3-api-findings.md).

Legend: ✅ fixed · ⚠️ partly, or to be confirmed in the game · ⏳ open

## Loading blockers

| # | Problem | Status |
|---|---|---|
| B1 | TF2 structure (`mod.lua`, `res/`) | ✅ `mod.json`, `_metadata/modinfo.json`, `_content.json`, `content/tf3export/*.gs.lua` + `*.script.lua` |
| B2 | `data()` with `save` / `load` / `update` | ✅ the module returns `{ update, handleEvent }` like `reforestation.script.tl` |
| B3 | `require` | ✅ `ug_require "x.lua"` (relative, one folder) |
| B4 | Availability of `io` / `os` | ✅ checked with a probe: no `io` and no `os.rename`, but `app.saveUserdata` works on the GUI thread (see #13) |
| B5 | `.script.lua` format | ✅ The game recognizes the file but requires a global `function data() return { update = ..., ... } end` (pattern: `base/base_mod.script.lua`). The `return {...}` style only works in `.tl`. Libraries loaded with `ug_require` may return a module with `return`. |

## Logic bugs

| # | Problem | Status |
|---|---|---|
| 1 | A shared `seq` counter for dynamic and network | ✅ separate `seqDynamic` and `seqNetwork` |
| 2 | Loading an older save rolls `seq` back and overwrites files | ✅ `session` is the GUI thread ID, new at game start and after game time is rolled back. `seq` never repeats within a session. |
| 3 | Network snapshot is not atomic | ⏳ accepted and documented. The website has to tolerate missing nodes. |
| 4 | Empty maps encoded as `[]` | ✅ `output` and `cargoNeeds` use `json.EMPTY_OBJECT` |
| 5 | No error checking on writes | ✅ `app.saveUserdata` is called in `pcall`, and on error the mod writes to the log |
| 6 | Time budget per tick | ✅ the network is built in `guiUpdate` in 2 ms chunks per frame (there is one GUI thread, so the module state survives between frames) |
| 10 | `getEntitiesWithComponent` doesn't work for LINE and BASE_EDGE | ✅ lines from `lineSystem.getLinesForPlayer` and from vehicles, edges from `streetSystem.getNode2SegmentMap` |
| 11 | Coroutines break the game's C calls (`log.message`) | ✅ coroutines removed |
| 12 | update() called from many threads (`Sim Pool 1..N`), each with its own Lua state | ✅ the whole export moved to `guiUpdate` (one GUI thread), and `update` is empty |
| 13 | No file writing (`io`, `os.rename`) | ✅ `app.saveUserdata` on the GUI thread writes to `local\tf3export\*.lua`, and the log stays as a fallback |
| 7 | Network changes detected by edge count | ⚠️ the `onPostBuildProposal` event (in `handleEvent`) bumps `netVersion` in the shared state, and the GUI rebuilds the network and writes a `networkDelta`. Plus a full comparison every 5 min. Revisions turned out useless, because they change all the time. Confirmed in the game: `networkDelta` works (260–600 B after building or demolishing). But the event also arrives every ~3 s without network changes, so I added a `touchesNetwork()` filter, not yet tested in the game. |
| 8 | Fixed day length | ✅ `GAME_SPEED.millisPerDay` with `api.util.getDefaultDayDuration()` as a fallback. To check whether the component sits on the world entity. |
| 9 | Edge type, silent `{0,0,0}` | ✅ `road` = `STREET`/`TRACK` (from `roadType`), `type` = `NORMAL`/`BRIDGE`/`TUNNEL`. Edges without a position are skipped and counted in the log. |

## Minor and open

- ✅ The dynamic export works in the game: about 120 KB, about 5 ms to collect, about 6 ms to encode. Enum names, date, balance and cargo are correct.
- ✅ Network: 8698 edges, about 1 MB, 150–200 ms of encoding in total. Now spread over GUI frames. A measurement with the new layout is still pending.
- ⚠️ Cargo is exported as resource names (`cargoTypeRep.getName`). Display names (`cargoTypeRep.get(id).name`, localized) can be added later.
- ⏳ `rules[].input` are raw stock indices.
- ⏳ `sessionId` relies on `os.time()` and `math.random`. If `os` is not available and `random` is not seeded, the ID may repeat between runs.
