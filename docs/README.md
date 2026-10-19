# TF3 → Twitch project documentation

| File | Contents |
|---|---|
| [tf3-api-findings.md](tf3-api-findings.md) | How TF3 differs from TF2 (mod structure, game script, API). `lua_mod` was rebuilt based on this. |
| [mod-known-issues.md](mod-known-issues.md) | Bugs and risks in the current mod skeleton (to fix later). |
| [reactive-dynamic-export.md](reactive-dynamic-export.md) | Postponed proposal: differential and reactive export of dynamic data (game events, `dynamicDelta`, event stream, stations). |
| [game-time-and-pause.md](game-time-and-pause.md) | Game time: what happens when paused and after the date is rolled back, an option for real-time effects, a plan for exporting `dynamic` while paused, and open questions. |
| [export-format-performance.md](export-format-performance.md) | Export format vs. performance: where the cost is, proposals (Lua tables instead of JSON, a columnar format, lower precision) and the `bench.lua` measurement. |
| [architecture.md](architecture.md) | Overall architecture: mod → middle app → Azure → website, plus costs and Twitch extension options. |
| [owners-tab.md](owners-tab.md) | The "Owners" tab: a viewer's whole estate (towns, industries, stations, lines) in one place, the main town, the `#owner=` link; then the map and the `!majatek` command. |
| [industry-api-dump.md](industry-api-dump.md) | Dump of the industry API (components, reading, commands) and how the game computes the production cycle; why a strike lingers after the effect ends; a console snippet. |
| [twitch-channel-points.md](twitch-channel-points.md) | Analysis of the Twitch channel points integration: API (Helix, EventSub), the Tf3Bridge → mod flow, ideas for rewards and game commands, risks. |

Code:
- [../lua_mod/](../lua_mod/README.md): the game mod;
- [../middle_app/](../middle_app/README.md): Tf3Bridge, .NET 9, runs locally next to the game;
- [../web/](../web/README.md): the website, static, hosted on Azure Static Web Apps.

Sources:
- API documentation: https://wiki.transportfever3.com/script-doc/api/cmd.html (and `engine`, `engine/util`, `engine/system`, `type`)
- Modding wiki: https://wiki.transportfever3.com/doku.php?id=modding
- `<game>` in these documents is the game install folder, e.g. `C:/Program Files (x86)/Steam/steamapps/common/Transport Fever 3`.
- Teal definitions in the game install: `<game>/api/tealdef/` and `base/tealdef/`
- Sample game scripts shipped with the game: `base/content/terrain/reforestation.gs.lua` + `reforestation.script.tl`
