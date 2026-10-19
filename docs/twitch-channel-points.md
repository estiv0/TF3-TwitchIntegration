# Twitch channel points: integration analysis

**Status (2026-10-04):** in Tf3Bridge the streamer login works ([description](../middle_app/README.md#twitch-login)), the Tf3Bridge → mod channel works in the game ([description](../middle_app/README.md#mod-commands-viewer-actions)), and EventSub receives chat and redemptions ([description](../middle_app/README.md#twitch-eventsub-chat-and-channel-points)). The author's channel doesn't have Affiliate status, so for now game changes are tested with the **Test actions…** button and the Twitch CLI.

**Rule (2026-10-04): only channel points change the game.** Buying towns, factory boosts and similar actions are channel points rewards only, because the streamer controls them: cost, cooldown, limits, pause. Chat commands are informational only (e.g. who owns a town, line status) and never go into the game command queue. The streamer doesn't control who uses them and how often, and they could break the balance. In code this is enforced by `CommandSource`: a mod command can only come from channel points or from a manual test in the window.

In short: the integration can be done without new Azure infrastructure. Tf3Bridge receives redemptions from Twitch and passes them to the mod the same way it already sends the mod settings. The game has ready-made commands for most sensible rewards.

## 1. Twitch API

### A. Helix REST: managing rewards

Base URL: `https://api.twitch.tv/helix/channel_points/...`

| Endpoint | Purpose |
|---|---|
| `POST /custom_rewards` | Creates a reward: title up to 45 characters, cost, optional text field for the viewer (`is_user_input_required`, prompt up to 200 characters), cooldown (`global_cooldown_seconds`), per-stream and per-viewer limits (`max_per_stream`, `max_per_user_per_stream`), color |
| `PATCH /custom_rewards` | Changes a reward, e.g. pauses it (`is_paused`) or disables it when the game isn't running |
| `GET` / `DELETE /custom_rewards` | Lists and deletes rewards |
| `GET /custom_rewards/redemptions` | Gets the queued redemptions (`UNFULFILLED`) |
| `PATCH /custom_rewards/redemptions` | Marks a redemption as `FULFILLED` or `CANCELED`. `CANCELED` refunds the viewer's points |

Requirements and limits:
- The streamer must be an Affiliate or Partner.
- Managing needs the `channel:manage:redemptions` scope, reading alone `channel:read:redemptions`.
- A channel can have at most 50 rewards, counting disabled ones.
- **A reward can only be changed and settled by the application (Client ID) that created it.** So rewards have to be created from Tf3Bridge, not by hand in the Twitch dashboard. Otherwise we can't refund points automatically when a command fails. A reward added by hand can't even be enabled or disabled through the API (403). Tf3Bridge tries this with rewards prefixed "TF3" and reports in its status the ones Twitch won't let it change ([description](../middle_app/README.md#channel-points-rewards)).
- `should_redemptions_skip_request_queue = true` marks a redemption as fulfilled right away. For us it should be `false`, so we can refund points.

### B. EventSub: real-time events

| Subscription type | Version | Scope |
|---|---|---|
| `channel.channel_points_custom_reward_redemption.add` | 1 | `channel:read:redemptions` |
| `channel.channel_points_custom_reward_redemption.update` | 1 | `channel:read:redemptions` |
| `channel.channel_points_custom_reward.add` / `.update` / `.remove` | 1 | `channel:manage:redemptions` |
| `channel.channel_points_automatic_reward_redemption.add` | 1, 2 | `channel:read:redemptions` |

- The `redemption.add` event arrives when a viewer redeems a reward. It contains the viewer's login and display name, `reward.id` and the `user_input` text.
- Twitch's automatic rewards (e.g. a highlighted message) can only be read, not managed. At most they suit a feed.
- **All of these events work over the WebSocket transport** (`wss://eventsub.wss.twitch.tv/ws`). No public server or webhook is needed, because the desktop app opens the connection itself.
- The old Twitch PubSub was shut down, so only EventSub matters.

### C. Login from a desktop app

- The best fit is the **Device Code Grant Flow**. Tf3Bridge shows a code, and the streamer enters it at `twitch.tv/activate`.
- The app is registered on dev.twitch.tv as a "public client", so no client secret has to be kept.
- The app's Client ID (`Twitch:ClientId` in `appsettings.json`): `odsqr5oc7n3xw7qezu9k1a36f1dvlh`.
- The refresh token is stored encrypted with DPAPI in `%LOCALAPPDATA%\Tf3Bridge`, like the SAS today (`azure.json`).

## 2. What we have on our side

- **The Tf3Bridge → mod channel already works.** Mod settings are written to `local\towns_industries\tf3export_settings.lua`, and the mod reads them with `app.loadUserdata` every `settingsEveryWallSec` (5 s). See `lua_mod/content/tf3export/tf3export.script.lua` and `middle_app/src/Tf3Bridge/ModSettings.cs`. The command queue can work the same way, e.g. as `local\towns_industries\tf3export_queue.lua`. Note: `loadUserdata` on a missing file shows a game error window, so `app.getAllUserdata` has to be checked first (like in `settingsFileExists`).
- **The mod → Tf3Bridge channel also works**, through `app.saveUserdata`. The mod can send command results ("ok" or "error") back the same way.
- **The game has a rich command API:** `api/tealdef/api/cmd.d.tl` in the game install, sent with `api.cmd.sendCommand`.
- **The website already understands the `Name | Owner` convention** for towns, industry, stations and lines (`splitOwners` in `web/js/format.js`). A natural base for viewers "adopting" objects.

## 3. Proposed flow

```
A viewer redeems a reward
  → EventSub WebSocket → Tf3Bridge
      validation: whitelist of types, matching the target by name/ID from the last dynamic.json,
      text filter, limits
  → queue.lua (app.saveUserdata format, with a unique redemptionId)
  → mod (loadUserdata every few s) → api.cmd.sendCommand(...)
  → the mod writes the result (redemptionId, ok/error)
  → Tf3Bridge: PATCH redemption → FULFILLED, or CANCELED (points refunded)
  → optionally: an entry in meta/dynamic → the website shows a feed "X did Y"
```

The delay will be a few seconds. The Azure cost doesn't change, because everything happens on the streamer's PC.

## 4. Ideas for rewards and game commands

| Reward | Game command | Notes |
|---|---|---|
| **Adopt a town, line or station** (the viewer becomes the "owner") | `makeEntitySetNameCmd` → `"Name \| login"` | Works right away with the current website: shows up as the owner. The best candidate to start with |
| **Name a train or vehicle** | `makeEntitySetNameCmd` | Text from viewers needs a profanity filter and a length limit |
| Change a vehicle's color | `makeEntitySetColorCmd` | Cosmetic and safe |
| Weather: cloud cover, time of day, wind | `makeGameSetCloudCoverageCmd`, `makeGameSetTimeOfDayCmd`, `makeWorldChangeWindCmd` | Looks good on stream, doesn't break the game |
| Release an animal | `makeAnimalSpawnAtCmd` | A curiosity |
| Town boost | `makeTownUpdateSizeCmd` / `makeTownDevelopAtCmd` | Affects gameplay, needs an expensive reward and a cooldown |
| Industry production boost | `makeStockListSetModifiersCmd` | As above |
| Financial "subsidy" or "sabotage" | `makeJournalBookAssetCmd` | Adds or removes company money |
| Troll: stop a random train for 30 s | `makeVehicleSetStoppedByUserCmd` (+ unblocking after a while) | Fun, but the mod has to remember to lift the block |
| Changing game speed | `makeGameSetSpeedCmd` | Probably not: easy to annoy the streamer |

Things beyond rewards:
- The **Predictions API** (scope `channel:manage:predictions`) allows betting points, e.g. "Will line X earn > 1M this year?". The outcome is resolved automatically from the export data.
- The **Polls API** (scope `channel:manage:polls`) suits votes like "Where to build the next line?".
- **Website and a future extension:** a ranking of adopters, a feed of redemptions, marking viewers' objects on the map.

## 5. Risks and things to check

1. **None of this was tested in TF3.** We have to check in the console whether `api.cmd.sendCommand` works from the GUI thread, where we have `loadUserdata`. If not, the mod has to pass the command to the simulation thread through `game.interface.sendScriptEvent`. A simple test with `makeEntitySetNameCmd` first.
2. **Pointing at the target.** The viewer types free text, so Tf3Bridge has to match it to a name from the last export and refund the points when it's ambiguous. The website could show a short "reward code" to copy next to each object.
3. **Security.** The viewer's text only reaches the Lua file as an escaped string. We need a whitelist of command types and length limits. Viewer content must never become code.
4. **Idempotency.** Every command carries a `redemptionId`, and the mod remembers the executed ids, so it doesn't run them twice after re-reading the file.
5. **Game off or paused.** Tf3Bridge disables the rewards (`is_enabled: false`) when the bridge is stopped, there are no fresh exports from a loaded save, or the app is closing; it enables them when game data flows again. Redemptions that couldn't be carried out are refunded with `CANCELED`.
6. **Multiplayer.** Commands from our mod may behave differently in a network game. To check if needed.

## 6. Proposed order

1. ✅ Register the app on dev.twitch.tv as a public client and add Device Code login to Tf3Bridge.
2. ✅ The `queue.lua` → mod → `results.lua` channel with a `setName` command and a **Test actions…** button in Tf3Bridge (adopting an object without Twitch). Checked in the game: `api.cmd.sendCommand` works from the GUI thread.
3. ✅ EventSub over WebSocket. Redemptions checked with Twitch CLI 1.1.24; the Twitch CLI doesn't support chat, so we test it on a real channel.
4. ✅ Informational chat commands, no game changes (`channel.chat.message`, the `user:read:chat` permission; chat replies need `user:write:chat`). Works without Affiliate status. Since 2026-10-05: `!pomoc`, `!mapa`, `!majatek`, `!miasto`, `!zaklad`, `!linia`, `!ranking`, `!wolne`, `!data`, `!firma`, `!najlepsza`/`!najgorsza [linia|firma]`, with per-viewer and per-command cooldowns ([description](../middle_app/README.md#chat-commands)). Not yet checked on a real chat.
5. ✅ Channel points: the only source of game changes. Buying a town (formerly "adoption"), town boost, industry boost, line stop (immediate) and line waits at stations ([description](../middle_app/README.md#channel-points-rewards)), with creating rewards, settling redemptions (FULFILLED/CANCELED) and auto-pause. Checked in the game with "Simulate reward" (2026-10-04); effect durations in game months, because a game day lasts 2–3 s. Not live on Twitch yet (channel without Affiliate). Costs, durations and multipliers are starting values, to be balanced on stream.
6. A new town and a new industry for channel points: place and name from the map on the website (the 🏗 button → a code to paste into the reward, because Twitch has no link that fills in the reward text), Tf3Bridge checks the place, the mod builds like the game's map editor (`create.lua`). Not yet checked in the game.
7. ✅ Effects on the website (badges in the list, details with the end date and the buyer) and a chat reply to the viewer after every redemption (the effect with its in-game end date and a link to the object on tf3.example.com, or the reason for the refund).
8. Sabotage and fuel (2026-10-05): an industry strike, a tax raid (all of a viewer's industries by nick), premium fuel, watered-down fuel, brake sabotage (vehicles disappear, the sale amount is taken in the books). Only other viewers' objects can be sabotaged. Productivity and speed are kept by a shared controller, so effects on one industry multiply; premium and watered-down fuel on one line run one after another (the second waits for the first to end). Not yet checked in the game ([description](../lua_mod/README.md#timed-effects-effectslua)).
9. Rewards carried out by the streamer (2026-10-05): bomb, line, nature, road vehicle, special vehicle. No effect in the mod; Tf3Bridge creates them and enables/disables them with the game, and the redemptions stay in the Twitch queue for the streamer ([description](../middle_app/README.md#channel-points-rewards)).
10. Subsidy and loan for the company (2026-10-13): a configurable amount (default $10M); the loan uses the game's loan system (2%, 60 game months, the game collects the installments). Not yet checked in the game ([description](../lua_mod/README.md#subsidy-and-loan-subsidy-loan-in-commandslua)).
11. More actions, a feed and a ranking on the website.

## Sources

- Twitch API reference, Channel Points: https://dev.twitch.tv/docs/api/reference/#create-custom-rewards
- EventSub subscription types: https://dev.twitch.tv/docs/eventsub/eventsub-subscription-types/
- Game commands: `<game>/api/tealdef/api/cmd.d.tl`
