# Subsidies from viewers (idea for later)

Status: researched in the game files on 2026-10-04, **nothing implemented, nothing tested in the game**.

Goal: a viewer proposes a subsidy for channel points (e.g. "deliver cargo X to town Y"),
and the streamer accepts or rejects it in the game's normal subsidy window.
Related: [custom-notifications.md](custom-notifications.md).

## Sources in the game

Paths relative to `<game>`:

- `base/content/game_mechanics.zip` → `game_mechanics/subventions/`
  - `subventions.script.tl`: game script (`Subvention` events, state, limits)
  - `subvention_util.tl`: rewards/penalties, cards, notifications
  - `deliver_cargo/`, `deliver_cargo_town/`, `deliver_passengers/`, `deliver_workers/`: built-in types
- Types: `base/tealdef/game_mechanics/subventions/*.d.tl`
- A custom type from the campaign: `mods/release/urbangames_campaign_mission_06/content/subventions.zip`
  (`deliver_visitors`, ~300 lines)

## Creating a subsidy: a ready-made game event

A comment in `subventions.script.tl` describes it as "Spawn a specific subvention for missions":

```lua
api.cmd.sendCommand(api.cmd.makeScriptingSendEventCmd("", "Subvention", "subventionSpawn", {
	name = "::/game_mechanics/subventions/deliver_cargo_town/deliver_cargo_town.res",
	specific = { target = townEntity, cargoTypeId = cargoId, uid = UNIQUE_NUMBER },
}))
```

The game calls the type script's `spawnSpecific(specific)` and adds the result to
`proposedSubventions`; the player sees the proposal and can accept it (`onAccept`) or decline it (`onDecline`).

For us this fits `lua_mod/content/tf3export/commands.lua` as a new command kind
(next to `createTown`, `createIndustry`, `adopt`), which already sends commands from `guiUpdate`.

### Built-in types (`specific`)

| Type (`name` = `::/game_mechanics/subventions/<type>/<type>.res`) | Task | Parameters |
|---|---|---|
| `deliver_cargo_town` | deliver cargo to a town | `target` (town), `cargoTypeId` |
| `deliver_cargo` | supply a factory | `target` (factory) |
| `deliver_passengers` | connect two towns with a passenger line | `source`, `target` (towns) |
| `deliver_workers` | bring workers to a factory | `target` (factory) |

All of them also take an optional `uid` and `expireDurationProposed` (how long the proposal waits
for a decision; `-1` = never expires).

The game rolls the amount, time and rewards itself (`createDeliverCargoState` etc.), taking
`advancedOptions.subventionRisk` and `subventionFinancialRewardMultiplier` into account. Rewards/penalties are
`Money`, `Reputation` (towns) and `TownExperience` (`SubventionBonusMalus`).

Useful console commands (from the game's comments), e.g. for testing:

```lua
api.cmd.sendCommand(api.cmd.makeScriptingSendEventCmd("", "Subvention", "_debugSpawn", { name = "::/game_mechanics/subventions/deliver_cargo/deliver_cargo.res" }))
api.cmd.sendCommand(api.cmd.makeScriptingSendEventCmd("", "Subvention", "_debugComplete", { index = 1 }))
api.cmd.sendCommand(api.cmd.makeScriptingSendEventCmd("", "Subvention", "_debugFail", { index = 1 }))
```

## Pitfalls

- **`uid` must be unique for the whole save.** `spawnSubvention` records every used `uid` in
  `state.usedUids[type]` and rejects repeats ("Could not spawn …" in the log). The default `uid` in
  `deliver_cargo_town` is `town * 10000 + cargo`, so without our own `uid` each town + cargo
  pair works once per game. We have to pass our own number that won't collide with the
  game's subsidies (e.g. negative or very large, with a counter kept on our side).
- **No limit.** The game's random subsidies have `MaxAvailable = 5`, but the `subventionSpawn` path
  seems not to check it, so a limit/cooldown has to be done on our side.
- **`deliver_cargo_town` needs the town to demand that cargo**: `toDeliver` is capped
  by `town_util.getCargoSupplyDemands`, and without demand it comes out as 0. Take the list of cargo to choose from
  from the town's needs (we already export them in `collectors.lua`, `cargoNeeds`).
- If the entity (town/factory) disappears, the subsidy ends as `InvalidEntity`.
- The game can put subsidies in `setPause` mode (e.g. missions); check whether that also blocks ours.

## Option for later: a custom subsidy type

If the built-in ones are not enough (e.g. a fixed reward, the viewer's nick in the title, a different task):

- `xyz.res.lua` with `type = "subvention"` and `data = { scriptFile = "estiv0_tf3export::/…/xyz.script@xyz", icons = { fallback = "…tga" } }`
- a script implementing `SubventionScript<T>` (`subvention.d.tl`): `canSpawn`, `onSpawn`,
  `spawnSpecific`, `isComplete`, `onUpdate`, `shouldDespawn`, `getCardData`,
  `getNotificationData` etc.; the pattern is `deliver_visitors` from campaign mission 6.
- Note: a type with `canSpawn` = true will also be rolled by the game (`trySpawnRandom` takes
  all resources of type `subvention`), so for "viewers only" `canSpawn` should return false.

## To check in the game

- Whether `subventionSpawn` sent from our mod's `guiUpdate` works (it should; it's an ordinary event).
- Whether our own `uid` (negative/large) breaks the subsidy window or notifications.
- What the proposal looks like in the UI and whether the viewer's nick can be added (the built-in types won't show it;
  we can send our own notification alongside, see custom-notifications.md).
