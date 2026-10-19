# Game time: pause, date rollback and exporting while paused

**Status:** analysis and plan (2026-10-05), no code changes. To decide: the questions at the end.

## How it works today

The mod uses two clocks:
- **game time** (`gameTime`, `util.gameTime()`): the `dynamic` export (every `dynamicEveryDays` game days), the duration of reward effects (`untilTime`, in game months), monthly and yearly statistics;
- **real time** (`os.time`, `wallTime()` in `tf3export.script.lua`): reading the command queue (every 1 s), command limits (`MAX_AGE_SEC` 300 s, checking the outcome 15–45 s), `minDynamicWallSec`, the periodic network comparison, terrain, settings.

Tf3Bridge uses game time only to display dates. It disables rewards when no new `dynamic` export arrives for 2 min (`WhyInactive` in `RewardService.cs`).

## Pause (the date stands still)

`guiUpdate` detects a pause: `gameTime` is the same as in the previous frame.

- **Rewards keep working.** Commands are read on real time. A boost, a line stop or a build is carried out. The result only shows after resuming, because the town doesn't grow and the industry doesn't produce.
- **Effects don't run out.** `untilTime` is in game time, so a 3-month effect lasts 3 game months regardless of the pause. That's by design (`effects.lua`). The "until" date in chat is correct.
- **The website doesn't refresh.** There is no `dynamic` export and no periodic network comparison while paused. Building still triggers a network export through `onPostBuildProposal`.
- **After 2 min of pause the bridge disables the rewards on Twitch**, because it receives no data.
- Commands don't expire because of the pause, since their limits count in real time. Condition: the game accepts commands while paused (building while paused works).

## Date rollback

The mod detects `now < lastGameTime` and starts a new session (`newSession`): a full export with a new `session`, effects reloaded from the save state.

**Loading an older save** is consistent:
- effects, towns and industries bought after the save disappear together with the save, so the list of effects matches the game;
- owners are in the object names ("Name | viewer"), so they always match the loaded save, with no risk of mixing up entities;
- downside: the viewer loses points for a reward that no longer exists, and the bridge doesn't refund them.

**Rolling the date back in a running game** (if possible at all, e.g. with a mod or the console):
- `untilTime` is absolute game time, so effects **get longer by the rollback**: rolling back a year turns a 3-month effect into about 15 months, and the date given in chat no longer matches;
- the effect's progress bar on the website drops to 0% (`format.js` clamps the value to 0..1);
- "previous year" statistics recompute on their own.

**Small risk:** in `guiUpdate`, `commands.poll` (together with `effects.update`) runs **before** the new-session check. If the Lua state of the GUI thread survives loading another save (unchecked), for one read the effects of the previous game act on the new map. For example, `maintain` of an industry boost would set productivity from the old base. Fix: check `newSession` before `commands.poll`.

## Option: real-time effects (postponed)

We considered counting effects in minutes instead of game months. Clock variants:

1. **Wall clock** (`os.time`): the effect ends N minutes after the purchase, even with the game closed. The simplest, but after loading the save the next day all effects end at once.
2. **Game running time:** a `playSec` counter grows only while the game is running, regardless of pause and speed. The clock stands still between streams.
3. **Game time without pauses:** like 2, but a pause also stops the counter. Game speed has no effect. This is the recommended variant.

Implementation (2 and 3): `playSec` in `guiUpdate` (increments of `os.time`, skipping big jumps, e.g. after the computer sleeps) saved to the save state with the same event as the effects. In the effect, `untilSec` instead of `untilTime`; in the export, `remainingSec`. In the bridge `Months` becomes `Minutes` (the rewards window, `{minutes}`, the chat message). Effects from old saves have to be converted once from months to minutes. As a bonus, a date rollback stops affecting effects.

**Decision:** not for now. Refreshing the website while paused (below) matters more.

## Plan: exporting `dynamic` while paused

Goal: the website refreshes even when game time stands still, because the player keeps building and buying vehicles, and viewers keep redeeming rewards.

1. **A new setting `pausedDynamicWallSec`.** While paused, export every N seconds of real time. Default 30, range 0–600, 0 = as today (no export). `minDynamicWallSec` still applies.
   - mod: `cfg` and `SETTINGS` in `tf3export.script.lua`, a new function `dynamicDue(now, wall, paused)` instead of the current condition in `guiUpdate`;
   - bridge: a `PausedDynamicWallSec` field in `ModSettings` (range, `Parse`, `ToLua`), a row "While paused, export every: N s (0 = no export)" in `ModSettingsDialog`, tests in `ModSettingsTests`.
2. **More reliable pause detection:** a pause is `gameTime` unchanged for at least 2 s (`S.gameTimeWall`, `PAUSE_AFTER_SEC = 2`), not one frame without a change. With GUI frames more frequent than simulation steps the current condition can wrongly report a pause during play. The periodic network comparison uses this flag too.
3. **Side effect:** data flows while paused, so **the rewards on Twitch stay enabled**. A reward bought while paused is carried out and shows on the website, and the effect timer starts after resuming.
4. **Documentation to fix:** `lua_mod/README.md` ("While paused the mod exports nothing"), the settings table in `middle_app/README.md`, point 5 in `twitch-channel-points.md`.

Cost: every `dynamic` export is uploaded to Azure. During play that's about every 10 s, so 30 s while paused is less often than usual.

### Open questions

- **Rewards while paused:** leave them enabled (point 3)? Alternative: a `paused` flag in the export, and the bridge disables rewards after 2 min of pause despite the data flow.
- **Faster refresh after an event:** export right after a reward is carried out or something is built (after `minDynamicWallSec`), so a new town or industry is on the website after about 10 s instead of up to 30 s. Overlaps with point 2 in [reactive-dynamic-export.md](reactive-dynamic-export.md).
- **Skipping identical exports:** while paused, without changes, the content is the same (apart from `seq`). We could skip writing it and save the upload, but then the bridge needs another signal that the game is running (e.g. a small heartbeat file). Otherwise it disables the rewards after 2 min.
