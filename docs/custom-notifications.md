# Custom in-game notifications

Status: researched in the game files on 2026-10-04; the notification after a channel points reward was
**implemented on 2026-10-05, not yet checked in the game** (see the
[Design](#design-notification-after-a-channel-points-reward) section at the end; the "To check in the game" list below still applies).

Goal: show in-game notifications like "new factory" or "subsidy", e.g.
"xyz founded the town Foo" after a channel points reward is carried out.
Related: [custom-subsidies.md](custom-subsidies.md).

## How the game does it

Patterns (paths relative to `<game>`):

- `base/content/landmarks/landmarks_notification*.res.lua` and `*.script.tl`
- `mods/release/urbangames_campaign_mission_03/content/notifications.zip` (`truckdespawn.script.tl`)
  and sending in `mission.zip` → `mission/tasks/fatal_maintenance/fatal_maintenance.tl`
- System implementation: `base/content/game_mechanics.zip` → `game_mechanics/notifications/`
- Types: `base/tealdef/game_mechanics/notifications/notifications.d.tl`

### 1. Registering the type: `xyz.res.lua`

```lua
function data()
	return {
		type = "notification",
		data = {
			guiType = "Info", -- Info | Caution | Problem | Opportunity | Achievement
			label = _("Twitch"), -- name in the notification log filter
			initiallyIgnoredType = false,
		},
	}
end
```

The game looks it up by the type name with `.script` replaced by `.res` (`api.res.genericRep.find`).
Without this file `guiType` is `"Unknown"`.

### 2. Appearance: `xyz.script` with `useDataState`

```lua
local notification_util = ug_require "::/game_mechanics/notifications/notification_util.tl"

local data = {}
data.useDataState = function(params)
	return {
		title = "Twitch: " .. params.viewer,
		description = params.text,
		icon = "::/game_mechanics/notifications/gui/icons/building_town_arrow_up.tga",
		-- optional:
		-- soundOnMount = { "<sound file>" },
		-- progress = { percentage = 0.5, text = "..." },
		wouldClick = params.entity and notification_util.makeDefaultWouldClick({ params.entity }) or nil,
		onClick = params.entity and notification_util.makeDefaultOnClick({ params.entity }) or nil,
	}
end
return data
```

`params.entity` is an `EntityAndRevision` (see `entity_util.makeEntityAndRevision` in the game);
clicking moves the camera to the entity.

### 3. Sending

```lua
api.cmd.sendCommand(api.cmd.makeScriptingSendEventCmd("", "Notifications", "add", {
	type = "estiv0_tf3export::/tf3export/twitch_notification.script",
	params = { viewer = "nick", text = "...", entity = ... },
	autoDismissDuration = 10000, -- ms; this "hides" the popup, the entry stays in the log
}))
```

For us this fits `lua_mod/content/tf3export/commands.lua`, which already sends commands
from `guiUpdate`, e.g. after `createTown` / `createIndustry` / `adopt` finish.

## Game icons to use

`::/game_mechanics/notifications/gui/icons/<name>.tga`, among others:
`building_industry_plus`, `building_town_arrow_up`, `company_rank_up`, `marketing`,
`relation_cargo_plus`, `building_temple`. You can add your own `.tga` (e.g. the Twitch logo);
the campaign has pairs `name@2x.tga` and `name_32@2x.tga`.

## To check in the game

- ~~Whether a notification type script works as `.script.lua`~~: checked on 2026-10-05: it works, but
  the file must have a **global `data()`** returning a table with `useDataState` (like `tf3export.script.lua`).
  With `return data` at the end (`.tl` style) the game throws "function data() not defined" in `NotificationPopups`,
  and the whole game UI breaks ("Script component root failed"). The type (`.res.lua`) and the `notify` command
  worked; the error only came when drawing the popup.
- Whether `_()` and `ug_require` of notification_util work in our Lua file.
- New files have to be added to `lua_mod/_content.json`.
- Notifications are stored in the save: what happens when the game is loaded without our mod
  (no type script): ignored or an error?

---

## Design: notification after a channel points reward

Goal: when a viewer redeems a reward that affects the game and it is **carried out and settled successfully**
(Twitch accepted FULFILLED, i.e. the points were taken), a notification pops up in the game
with the Twitch logo and a description of what happened. Rejections and refunds do **not** produce a notification
(the viewer gets a chat reply anyway).

### Flow

```
 Twitch: a viewer redeems a reward
   ▼
 RewardService.Process ── command (adopt / effect / createTown / ...) ──► queue.lua ──► mod
   ▲                                                                                   │
   └──── results.lua: status ok (+ entity for create*) ◄──────────────────────────────┘
   │
 OnCommandCompleted → Complete → CloseAsync: SetRedemptionStatusAsync(FULFILLED)
   │  only once Twitch answers with success:
   ▼
 notify command { title, text, entity? } ──► queue.lua ──► mod: Notifications "add"
                                                            ▼
                                     in-game popup (Twitch icon, "Opportunity" color)
```

Why a separate `notify` command sent by Tf3Bridge rather than a notification the mod shows
right after `ok`:

- Only Tf3Bridge knows "points taken" (Twitch's answer to FULFILLED). When settling fails
  (e.g. an expired token), the redemption stays in the Twitch queue and the streamer settles it by hand;
  then we don't announce in the game something that may still end in a refund.
- The texts already live in C# (`RewardMessages`); the mod only shows a ready title and description,
  so texts are not duplicated in Lua and a new reward needs no mod changes.
- `notify` is a general command: later it can be used for other events (effect ended, subsidy,
  a message from the streamer) without a new command kind.

Cost: the notification appears ~1–2 s after the action (one queue round every `commandsEveryWallSec`
plus Twitch's answer). When simulating from the Tf3Bridge window (no real redemption), `notify`
is sent right after `ok`, with `[TEST]` added to the title.

### The `notify` command (Tf3Bridge → mod)

```lua
{ id = "...", kind = "notify", created = <unix s>,
  title = "Twitch: Ala",                       -- ≤ 100 characters
  text = "Ala bought the town Kraków.",        -- ≤ 300 characters
  entity = 12345,                              -- optional: clicking moves the camera
  dismissMs = 10000 }                          -- optional: after how many ms the popup hides
```

- C#: `ModCommand.Notify(title, text, entity?, dismissMs, source, user)`. Texts go through
  `ModCommandLua.Str` (it already escapes characters coming from viewers); before that we cut the length
  and replace new lines with spaces.
- `notify` does **not** go into `_pending` in `RewardService`, because its result settles nothing.
  An error is only logged (`Game notification not shown: ...`).
- Mod: `KINDS.notify` in `commands.lua` sends the event and returns `verify = function() return true end`
  (the notification state can't be checked cheaply from the GUI thread). Commands older than `MAX_AGE_SEC`
  are rejected like the others, which is good: an old notification after a game restart makes no sense.

### New entity id for `createTown` / `createIndustry`

**Done on 2026-10-05** (not yet checked in the game). The bridge did not know the id of the new town/industry,
and without it clicking the notification does nothing. The command result now has `entity`:

- `create.lua`: once `verify` finds the entity it stores it in `info.entity`
  (just like effects return `until`); `commands.lua: finish` copies `info.entity` into the result.
- C#: `ModCommandResult.Entity` (`long?`). The chat reply link for `CreateTown` /
  `CreateIndustry` is now `#town=<id>` / `#industry=<id>`; the pin `#pin=x_y&note=...` stays
  only as a fallback when the mod gives no id (an older mod version in the game).
- Website (`web/js/main.js`): a link to an object that is not in the export yet waits up to
  6 more data batches before giving up (a freshly built town reaches the export with a delay).
- To do with `notify`: for the other rewards `RewardService` takes `target.Entity`,
  which it already has in `Process` (it has to be added to the `Pending` record).

### Texts (`RewardMessages.Notification`)

Title: `Twitch: <viewer>`. Description by reward kind (the texts are currently in Polish; English versions shown here):

| Reward | Description |
|---|---|
| AdoptTown | `<viewer> bought the town <target>.` |
| BuyIndustry | `<viewer> bought the industry <target>.` |
| BoostTown | `<viewer> sped up the growth of <target> by +X% until <date>.` |
| BoostIndustry | `<viewer> raised the production of <target> by +X% until <date>.` |
| StopLine | `<viewer> stopped the line <target> until <date>.` |
| HoldLine | `<viewer> made the vehicles of line <target> wait at stops until <date>.` |
| CreateTown | `<viewer> founded the town <target>.` |
| CreateIndustry | `<viewer> built the industry <target>.` |
| StrikeIndustry | `<viewer> started a strike at <target>: production -X% until <date>.` |
| TaxRaid | `<viewer> sent the tax office after the industries of viewer <victim> (N): production -X% until <date>.` |
| PremiumFuel | `<viewer> filled up premium fuel on line <target>: +X% speed until <date>.` |
| BadFuel | `<viewer> watered down the fuel on line <target>: -X% speed until <date>.` |
| Subsidy | `<viewer> gave the company a subsidy of <amount>.` |
| Loan | `<viewer> gave the company a loan of <amount> at X% for N months (installments in the Loans tab).` |
| BrakeSabotage | `Technical problems on line <target>: vehicles destroyed: N of M (sabotage: <viewer>).` (clicking shows the line) |

The Polish texts use gender-neutral forms like "kupił(a)", because we don't guess gender from a nick. `<date>` comes from `ModCommandResult.Until`
(`RewardMessages.Date`), and when it is missing: "for N months". No link to the website (it can't be clicked in the game).

### Mod files

```
lua_mod/content/tf3export/
  twitch_notification.res.lua      -- type registration: guiType "Opportunity" (purple accent), label "Twitch"
  twitch_notification.script.lua   -- useDataState: title/description/icon/onClick from params
  icons/twitch@2x.tga              -- 48×48, 8-bit grayscale (mask)
  icons/twitch_32@2x.tga           -- 64×64, 8-bit grayscale (mask)
infra/twitch_logo.png              -- icon source
infra/make_icon.ps1                -- logo -> TGA
```

`pack.ps1` also lists the `.tga` files in `_content.json` (like the game's own mods), and Tf3Bridge embeds them
in the exe together with the rest of the mod (`ModInstaller`).

`KINDS.notify` in `commands.lua` sends from the GUI thread (just like `effects.lua: persist`):

```lua
api.cmd.sendCommand(api.cmd.makeScriptingSendEventCmd("", "Notifications", "add", {
	type = "estiv0_tf3export::/tf3export/twitch_notification.script",
	params = { title = ..., text = ..., entity = entityAndRevision(c.entity) },
	autoDismissDuration = c.dismissMs or 10000,
}))
```

`entityAndRevision` recreates the game's `entity_util.makeEntityAndRevision`
(`{ entity, revision = { num = {…} } }` from `api.engine.getRevision`); when the entity is gone or the API
fails, the notification is sent without `entity` (no click). The mod trims the texts once more
in bytes (400 / 1200) without splitting UTF-8 characters; the real limits (100 / 300 characters) are applied by Tf3Bridge.

### Icon (Twitch logo)

In-game notification icons are **grayscale masks**, not color images: TGA type 3
(8-bit grayscale, rows bottom-up), a white glyph on black, `name@2x.tga` 48×48 and
`name_32@2x.tga` 64×64 (checked on `building_town_arrow_up*`). The game adds the color based on `guiType`.

- `make_icon.ps1` takes the purple pixels (the speech bubble) from the logo; the eyes and the inside stay holes;
  margins match the game icons. After changing the logo: `pwsh infra/make_icon.ps1`
  (the script switches itself to Windows PowerShell 5.1, because `System.Drawing` works there without trouble).
- The TGAs are in the repo (Tf3Bridge embeds the mod at build time, without `pack.ps1`).
- To check: whether a color RGBA TGA also shows (then the purple logo could be used 1:1).

### Settings and testing (Tf3Bridge)

- `RewardSettings.GameNotifications` (on by default): a field in the "Rewards" window.
- The popup hides after 10 s (`RewardService.NotificationDismissMs`); the entry stays in the game log.
- Command test window: a "Test notification" button (clicking it shows the selected object),
  and a reward simulation sends a notification with `[TEST]`.

### Edge cases

- **Several redemptions at once**: each gets its own notification; the game stacks the popups itself.
- **Game notifications paused** (`notificationsState.paused`): the game then adds no entry.
  We accept that; the command ends with `ok`, and chat works anyway.
- **Save loaded without the mod**: notifications are in the save, and a type without a script may cause an error
  on load. To check in the game; if there is an error, the mod dismisses our entries at startup
  or we give up on the log entry.
- **Old mod version in the game**: the mod answers `unknown kind notify`; Tf3Bridge only logs it
  (`Game notification not shown`), without a refund, since the reward is already settled.

### Tests

- `ModCommandTests`: `Notify` → a single line, length trimming, no `entity`.
- `RewardTests`: `notify` only after a successful FULFILLED; none on a refund, on a failed
  settlement (403) or with `GameNotifications` off; `[TEST]` and `entity` from the result when
  simulating `createTown`; the effect end date; the text for every `RewardKind`.
- In the game (not done yet): icon look, click → camera, log entry and the
  "Twitch" filter, saving and loading a save.
