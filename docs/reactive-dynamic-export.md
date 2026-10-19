# Proposal: reactive export of dynamic data

**Status:** idea postponed (2026-10-02). For now the current export stays: a full `dynamic` snapshot every 2 game days, no more often than every 10 s.

Goal: like with the network (a full dump once, then `networkDelta` after build events), limit the data sent to what actually changed, and react faster to important changes.

## What events the game provides

The list comes from `state:subscribeToEvent(...)` in the game scripts (`base/content`). Events reach `handleEvent` on simulation threads, so writing to a file still has to go through the shared state and `guiUpdate`, just like `netVersion` for the network.

| Event | What it says | Use |
|---|---|---|
| `onPostBuildProposal` | a build or demolition was applied (by the player or a town, param `[4] = playerInitiated`) | network (already used), new stations and depots |
| `ConstructionCompleted` | a construction finished | immediate refresh of stations and industry |
| `OnArriveAtStop` | a vehicle arrived at a stop (`vehicleEntity`, `lineEntity`, `stopIndex`, `lastStopIndex`) | event stream, live traffic |
| `OnCargoLoaded` / `OnCargoUnloaded` | loading and unloading | event stream ("train X brought 120 t of coal") |
| `OnLevelUp` | a town levels up (`townEntity`, `newLevel`) | town refresh |
| `DeliveryRatingChanged`, `NewCargoTypeDemand`, `OnHappyChange` | changes in town ratings and demand | town refresh |

**No events were found** for creating and editing lines, buying and selling vehicles, factory production, stock levels, balance or pollution. Some of these values change all the time, so an event wouldn't help much anyway.

## Proposed approach: a hybrid

### 1. Polling with diffs (`dynamicDelta`)
- The mod still collects data every ~10 s, because it's cheap: about 5 ms to collect, about 6 ms to encode.
- Each entity (town, industry, line, vehicle) is encoded separately into an `id → JSON` map, like the edge rows in the network.
- A full `dynamic` is produced once per session. The following files are `dynamicDelta`, and the shared `seq` counter gives `base = seq - 1`:
  ```
  { v, kind:"dynamicDelta", session, seq, base, gameTime, date, player,
    changed: { towns:[...], industries:[...], lines:[...], vehicles:[...] },
    removed: { towns:[ids], industries:[ids], lines:[ids], vehicles:[ids] } }
  ```
- Works for everything, including lines and vehicles, which have no events.
- The files get small, because most entities don't change between reads. So that diffs don't come from noise, continuous values (e.g. `pollution`, `noise`, `upgrade`) should be rounded more coarsely.

### 2. Events speed up refreshing
- `handleEvent` bumps a counter in the shared state, e.g. `dynVersion`, after `ConstructionCompleted`, `OnLevelUp`, `DeliveryRatingChanged` and `onPostBuildProposal`.
- After the counter changes, `guiUpdate` reads right away (after a short wait) instead of waiting for the next 10 s cycle.

### 3. Event stream (a separate feature, later)
- `OnArriveAtStop` and `OnCargoLoaded`/`OnCargoUnloaded` would go into a buffer in the shared state. Every few seconds `guiUpdate` would flush the buffer to an `events_<session>_<seq>` file.
- Mind the performance: there are many arrivals, so the buffer in the shared state must be bounded (e.g. the last N events), and `state:set` on every event may be costly. This needs measuring.

### While at it: stations
Stations are not exported yet. To add:
- the station group's name and position;
- the lines served, from `api.engine.system.lineSystem.getLineStops(stationGroup)` / `getLinesForStationGroup`;
- the town, from `stationSystem.getTown`.

## Rollout order (when we come back to this)
1. Confirm in the game that `onPostBuildProposal` and `networkDelta` work, because it's the same mechanism.
2. Add stations to the `dynamic` snapshot.
3. Implement `dynamicDelta` (point 1), then the event speed-up (point 2).
4. The event stream (point 3), if it's needed on the website or the stream.

On the receiving side (middle app or server) we have to add assembling the state from a full dump and diffs. When a `seq` is missing, the receiver waits for the next full dump.
