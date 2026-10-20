# Tf3Bridge: middle app

A .NET 9 desktop app (WinForms) that receives data from the `estiv0_tf3export` mod and publishes it as ready-made JSON files for the website.

```
TF3 (mod) ──app.saveUserdata──▶ ...\3493540\local\tf3export\*.lua
                                      │  Tf3Bridge (every 500 ms)
                                      ▼
                               output\latest\dynamic.json   the newest dynamic snapshot
                               output\latest\network.json   the full network (dump + applied diffs)
                               output\latest\meta.json      session, seq, game date, edge count
```

Output always goes to a local folder (`LocalFolderPublisher`), and optionally also to Azure Blob Storage with the same `latest/*` layout (see "Upload to Azure").

## Running

Tf3Bridge is a small desktop app (WinForms). The window shows the state (running / stopped), the time of the last publish, the input and output folders, and a log. The bridge starts right away.

- **Stop / Start** pauses and resumes processing without closing the app.
- **Minimizing** hides the window to the tray (green dot = running). Double-clicking the icon restores the window; the icon menu has "Exit".
- **Closing the window** finishes the current pass and exits the app.
- Only one instance can run; a second one shows a message and exits.

A ready exe with a desktop shortcut:

```powershell
infra\publish.ps1
```

The script builds `middle_app\dist\Tf3Bridge.exe` (the `dist\` folder is in `.gitignore`) and creates a `Tf3Bridge` shortcut on the desktop (`-NoShortcut` skips the shortcut). Close the running app before publishing again.

The exe is self-contained single-file (win-x64, about 55 MB): it needs no installed .NET, and next to it there's `appsettings.json` (and possibly `appsettings.Local.json`, see below). To give it to someone else, `Tf3Bridge.exe` + `appsettings.json` is enough. The bridge finds the mod folder on its own (see `InputDir`), and outside the repository it publishes to `output\` next to the exe.

The mod is built into the exe (`lua_mod\` as of build time, `ModInstaller`). At startup the bridge installs or updates it in `Steam\userdata\<account>\3493540\local\staging_area\estiv0_tf3export` (not `mods\`: from there the game only shows Workshop mods): it writes new and changed files, and only deletes those `.lua` files in `content\` that the mod no longer has. So a new exe also updates the mod. If the game is running, the new version only takes effect after the save is loaded again. The bridge doesn't touch a development copy (a junction `staging_area\estiv0_tf3export` → the repository). The `Embedded_mod_is_complete` test makes sure `_content.json` matches the `.lua` files (refresh it with `lua_mod\pack.ps1 -ContentOnly`).

From source: `dotnet run` in `middle_app/src/Tf3Bridge`, or `Tf3Bridge.sln` in Rider. If the SDK is in your user folder (e.g. installed by Rider) and `dotnet` isn't on `PATH`, use `%USERPROFILE%\.dotnet\dotnet.exe`.

Tests:

```bash
cd middle_app
dotnet test
```

## Language

The app speaks English and every language that has a file in `src/Tf3Bridge/Locales/` (now: Polish). One setting, the language list at the end of the button row (**English / Polski**, each language under its own name), covers everything: the windows, the chat replies, the in-game notifications and the default reward texts. The first start follows the Windows display language when there is a file for it, English otherwise; the choice is saved in `%LOCALAPPDATA%\Tf3Bridge\language.txt` as the language code (`Loc`). The log stays in English.

The default titles and descriptions of the channel points rewards are translated like any other text (`RewardKinds.DefaultsFor`). After a language switch, the rewards whose title or description is still the default of another language get the default of the new one and go to Twitch right away; texts the streamer changed stay as they are. Chat command names are not translated: the streamer sets them in the "Chat commands" window (`Chat/ChatCommandSettings.cs`), and by default every command answers to both its English and Polish name regardless of the setting; the language only decides which name `!help` shows (see "Chat commands").

### How texts work in the code

The code holds only English, which is both the key and the fallback (gettext style):

- `Loc.T("Save")`: a fixed text.
- `Loc.F("{0} live map: {1}", mention, url)`: a text with values, as a `string.Format` format, so a translation can reorder them. Never pass `$"..."` to `Loc`: the key would change with every value (a test fails on that).
- `Loc.P(n, "{0} town", "{0} towns")`: a count with the plural form of the language.
- `Loc.N("TF3: buy a town")`: marks a text that is translated later (`Loc.T(x)` / `Loc.In(language, x)` where it is shown), e.g. tables of names or reward defaults.

Tests run in Polish by default (a `[ModuleInitializer]` in the test project); English tests use `using var _ = Loc.Use("en");`. `LocaleTests` scans the source for the literal keys of `Loc.T` / `Loc.F` / `Loc.P` / `Loc.N` and fails when a locale file misses one of them, has a key the code no longer uses, changes the placeholders (`{0}`, `{months}`, …) or has the wrong number of plural forms. So after adding or changing an English text, add it to every locale file too.

### Adding a language

1. Copy `src/Tf3Bridge/Locales/pl.json` to `<code>.json`, where `<code>` is the two-letter language code (`de.json`, `cs.json`, …; it must be a .NET culture: number formats and the name in the list come from it).
2. Translate the values, never the keys. Keep the placeholders: `{0}`, `{1}` (may move inside the sentence) and `{months}`, `{percent}`, `{site}`, `{amount}` in reward descriptions; a literal brace in a `Loc.F` text is written `{{` / `}}`. A value left as `""` is not translated yet and shows in English.
3. Plurals (`"{0} town": [...]`) are arrays with one form per plural category of the language, in the order of the rule in `Loc.Rule`: English-like languages 2 (one, other), Polish, Czech, Slovak, Russian, Ukrainian, Croatian, Serbian 3, Japanese, Chinese, Korean 1. A language with another rule needs a line in `Loc.Rule`.
4. Build and run the tests (`dotnet test`). The file is embedded in the exe (`Locales\*.json` in `Tf3Bridge.csproj`), so the language shows up in the list without other changes.

## Configuration (`src/Tf3Bridge/appsettings.json`, `Bridge` section)

Put your own values (e.g. `Twitch:SiteUrl`, your website's address) into `src/Tf3Bridge/appsettings.Local.json` next to `appsettings.json`. That file is in `.gitignore`, overrides `appsettings.json` and ends up in `dist` when publishing:

```json
{ "Twitch": { "SiteUrl": "https://your-site.example/" } }
```

| Key | Default | Description |
|---|---|---|
| `InputDir` | empty: detected from Steam (`SteamLocator`): the Steam folder from the registry, and in it `userdata\<account>\3493540\local\tf3export` of the logged-in account; without a logged-in Steam, the account that last played TF3 with the mod | the folder the mod writes to |
| `OutputDir` | empty: `web\data` of the repository the exe lives in (searched upwards from the exe folder), i.e. where the local website reads from; outside the repository, `output\` next to the exe. A relative path counts from the exe folder | where to publish `latest/*.json` |
| `PollIntervalMs` | 500 | how often to check the folder |
| `IncompleteFileTimeoutSec` | 30 | a file that still can't be read after this time goes to `InputDir\failed` |
| `KeepProcessedFiles` | false | moves files to `InputDir\processed` instead of deleting them (debug) |
| `IndentOutput` | false | readable JSON output (debug) |

Any key can be overridden from the command line, e.g. `Tf3Bridge.exe --Bridge:OutputDir=D:\tf3out` (in the shortcut's "Target" field) or `dotnet run -- --Bridge:OutputDir=D:\tf3out`.

## Mod settings

The **Mod settings…** button in the window changes how the mod behaves in the game without editing files:

| Field | Default | Key in the mod |
|---|---|---|
| Export data every … game days | 2 (1–30) | `dynamicEveryDays` |
| …but not more often than every … s | 10 (2–300) | `minDynamicWallSec` |
| Work time per frame | 3 ms (1–10) | `dynamicBudgetMs` |
| Vehicles (list, positions, count on lines) | yes | `vehicles` |
| Terrain export | yes | `terrain` |
| Terrain resolution | 20 m (5–100) | `terrainStepM` |

The mod has no file access apart from `app.loadUserdata`, so the bridge saves the settings as a userdata file in the export folder: `local\towns_industries\tf3export_settings.lua` (`ModSettingsStore`, written through a temporary file). The mod reads it every 5 s, so changes apply without restarting the game; the game log shows `[tf3export] settings: …`. Deleting the file restores the defaults.

## Upload to Azure

Output always goes locally (`web\data`), and if Azure is enabled in the window, `AzureMirror` also uploads the changed files to the Blob Storage container the website reads from.

**First-time setup:**

1. Install the Azure CLI: `winget install Microsoft.AzureCLI` (then open a new PowerShell window).
2. Create the resources: `.\infra\azure-setup.ps1 -Name <name>`. Account name: 3–24 lower-case letters and digits, unique in Azure, because it's part of the address (`https://<name>.blob.core.windows.net`). The script creates the `tf3-live` resource group in `polandcentral`, a Storage account (Standard_LRS), a public `live` container and CORS, and prints the container address and a SAS token (valid for a year, create + write rights only).
3. In Tf3Bridge click **Azure…**, paste the address and the token, **Test connection**, **Save**.
4. On the website set `dataUrl` in `web/site.json` to `https://<name>.blob.core.windows.net/live/latest/` (see [../web/README.md](../web/README.md)). To test without editing the file: `http://localhost:5080/?data=https://<name>.blob.core.windows.net/live/latest/`.

A new token (e.g. when the old one expires): `.\infra\azure-setup.ps1 -Name <name> -SasOnly`.

**How the upload works:**

- The SAS token is in `%LOCALAPPDATA%\Tf3Bridge\azure.json`, encrypted with DPAPI for the current Windows account. It never goes into the repo or `appsettings.json`.
- After each pass the locally changed files are uploaded. After enabling Azure (or starting the app) everything already in `latest\` is sent.
- JSON is gzipped (`Content-Encoding: gzip`), and everything has `Cache-Control: no-cache`.
- `meta.json` always goes last and only when the rest succeeded, because after a seq change in meta the website immediately downloads the other files.
- An error (no network, expired token) doesn't stop the local output. Files stay queued and are retried every 10 s; the state shows in the window ("Azure: error: …").

## Twitch login

Twitch integration ([../docs/twitch-channel-points.md](../docs/twitch-channel-points.md)): streamer login, EventSub, channel points rewards and chat commands (described below). Only channel points change the game; chat is informational only.

- The app on dev.twitch.tv is a **Public** client, without a client secret. `Twitch:ClientId` in `appsettings.json` is public and shared by all streamers. If you distribute your own build, register your own app on dev.twitch.tv and put its Client ID in `appsettings.Local.json`.
- **Twitch…** → **Log in with Twitch**: the window shows a code and opens `twitch.tv/activate`, where the streamer approves access (Device Code Grant Flow). Permissions: `channel:read:redemptions`, `channel:manage:redemptions`, `user:read:chat`, `user:write:chat` (replies to viewers after a redemption). When the app needs a new permission, the saved account is forgotten and you have to log in again.
- Only a user access token is used. No app access token is needed: EventSub over WebSocket and the channel points endpoints don't accept it.
- The tokens are in `%LOCALAPPDATA%\Tf3Bridge\twitch.json`, encrypted with DPAPI. Login, ID and permissions are in plain text.
- `TwitchSession` validates the token at startup and every hour (`/oauth2/validate`, Twitch requires this) and refreshes it before it expires. A public client's refresh token is single-use and valid for 30 days, so after every refresh the new one is saved right away. When Twitch rejects it (a longer break, a password change, disconnecting the app), the account is forgotten and you have to log in again. Without network the account stays.
- The window also shows the channel type. Without Affiliate/Partner status you can't create channel points rewards.

## Twitch EventSub (chat and channel points)

`EventSubClient` connects to `wss://eventsub.wss.twitch.tv/ws` with the streamer's token (no public server, no webhooks) and subscribes to:

- `channel.chat.message` (channel chat): works on any channel;
- `channel.channel_points_custom_reward_redemption.add` (reward redemptions): only makes sense on an Affiliate/Partner channel.

How it works:
- Subscriptions are created in Helix (`POST /eventsub/subscriptions`) after the `session_welcome` greeting. Twitch allows 10 s for this.
- When one subscription fails, the connection stays with the others, and the reason is logged. When all fail, the connection is dropped and retried.
- Keepalive every 30 s. No message for keepalive + 10 s means a broken connection. The client then reconnects with delays of 1, 2, 5, 10, 30 and 60 s and creates the subscriptions again.
- `session_reconnect`: moving to a new address without resubscribing (Twitch carries them over).
- Events are dropped as duplicates by `message_id`. The log shows redemptions and chat messages starting with `!`.
- The state shows in the window in the "EventSub: …" line.

## Chat commands

Our commands start with `!` (no prefix). They only read the last `dynamic.json` and reply in chat as the streamer; they never send anything to the game (`Chat/ChatCommandService.cs`: list of commands and cooldowns, `Chat/ChatAnswers.cs`: the reply texts). Links only when `Twitch:SiteUrl` is set. Object names are matched the same way as in rewards (`TargetResolver`): case- and diacritics-insensitive, first exactly, then by prefix, then by substring; when several match, the reply lists them. By default every command answers to its English and its Polish name (and the arguments too: `!free towns` = `!wolne miasta`); the reply is in the app language (see "Language"), with numbers formatted for it ("$833,881" / "$ 833 881").

The **Chat commands…** window (`Ui/ChatCommandsDialog.cs`) lets the streamer switch each command off, rename it, edit its extra names (aliases) and its cooldown, e.g. remove the Polish names on an English stream. Settings: `%LOCALAPPDATA%\Tf3Bridge\chat-commands.json` (`Chat/ChatCommandSettings.cs`); no file = the defaults below. Names are letters, digits and `_` (case-insensitive, stored without `!`), each name may belong to one command only (off commands included). Saved changes apply from the next chat message, without a restart. A command that is off is ignored silently and left out of `!help`; answers that point to another command (`!ranking` → "see !free", usage hints) use the configured names. Unchanged default names follow a language switch (the current language's name first), like the reward texts.

| Command | Polish name (and other aliases) | Reply | Cooldown (whole chat, default) |
|---|---|---|---|
| `!help` | `!commands`, `!pomoc`, `!komendy` | list of commands (in the app language) | 30 s |
| `!map` | `!mapa` | link to the website | 30 s |
| `!assets [viewer]` | `!majatek`, `!majątek` | a viewer's estate (without an argument: the asker's), e.g. "@ala estiv0: 2 towns (Szczecin – main, Barcin), 1 industry (Brewery), 2 lines, 16 vehicles, line profit (12 mo.) $833,881. Map: https://…/#owner=estiv0". Grouping like the "Owners" tab (`Commands/OwnerAssets.cs`): case-insensitive, main town = the largest, stations named after a bought town don't belong to the viewer | 30 s |
| `!town <name>` | `!miasto` | owner, capacity, growth since game start, needs satisfaction, active reward effects (with end date and buyer), link | 30 s |
| `!industry <name>` | `!zaklad`, `!zakład` | owner, level, productivity, production in the last year, effects, link | 30 s |
| `!line <name>` | `!linia` | owner, vehicles (and how many stopped), 12-month profit, load factor, transported this year, effects, link | 30 s |
| `!ranking` | `!top` | the 5 viewers with the largest estate, as on the website: town 100 pts + size/25 + growth since game start ×100, industry 50, station and line 1, vehicle on lines: train/plane 10, ship 2, road 1; ties broken by line profit | 60 s |
| `!free [towns\|industries]` | `!wolne [miasta\|zaklady]` | unowned objects to buy with points: the 5 largest towns and the highest-level industries, with the total count | 60 s |
| `!date` | `!data` | the in-game date; when the export is older than 2 min, a note "paused or game off" | 30 s |
| `!company` | `!firma` | the streamer's company: balance, company value, profit this year and last year (Finances table), lines, vehicles | 60 s |
| `!best [line\|company]` | `!najlepsza`, `!najlepszy` (`[linia\|firma]`) | the line with the highest 12-month profit or (`company`) the viewer with the highest total profit of their lines | 60 s |
| `!worst [line\|company]` | `!najgorsza`, `!najgorszy` | as above, lowest profit | 60 s |

- There are two cooldowns, both silent (no reply): one viewer gets at most one reply per 10 s (any command), and the same command with the same argument is repeated in chat at most once per the time in the table. Arguments are compared case- and diacritics-insensitively, so `!town Łódź` and `!town lodz` are the same reply. English and Polish names of a command share it. `!assets` without an argument counts separately for each viewer.
- With the Twitch CLI test server the reply only goes to the log (the server has no chat).

**Testing without Twitch (Twitch CLI):**

```
twitch event websocket start-server
Tf3Bridge.exe --Twitch:EventSubUrl=ws://127.0.0.1:8080/ws --Twitch:SubscriptionsUrl=http://127.0.0.1:8080/eventsub/subscriptions
twitch event trigger channel.channel_points_custom_reward_redemption.add --transport=websocket
```

Visual Studio/Rider has a "Tf3Bridge (Twitch CLI)" launch profile for this. Checked with Twitch CLI 1.1.24:
- the redemption arrives;
- `channel.chat.message` isn't supported by the test server: the subscription fails with 400, and `trigger` refuses. Chat has to be tested on a real channel.
- `trigger` has no port option, so the server has to run on the default 8080.
- The Twitch account has to be logged in, but the test server doesn't check the token.

## Channel points rewards

The only way for viewers to change the game (`RewardService`). Fifteen rewards; each can be disabled and balanced in the **Rewards…** window: title, description, cost, cooldown, per-stream and per-viewer limits, duration in game months and multiplier. Duration is in months, not days: a game day lasts only 2–3 s, so an effect of a few days was over before you could see it.

| Reward | Target (text from the viewer) | Mod command |
|---|---|---|
| Buy a town | an unowned town | `adopt`: "Name \| viewer"; an owned town = points refunded |
| Buy an industry | an existing unowned industry | `adopt`: "Name \| viewer"; an owned industry = points refunded |
| Town boost | a town | `effect townBoost` (months, multiplier) |
| Industry boost | an industry | `effect industryBoost` (months, multiplier) |
| Line stop (immediate) | a line | `effect lineStop` (months) |
| Line waits at stations | a line | `effect lineHold` (months) |
| New town | a code from the map: `[x, y] Name` | `createTown`: a town "Name \| viewer" at point (x, y) |
| New industry | a code from the map: `[x, y, kind] Name` | `createIndustry`: an industry "Name \| viewer" (kind = `coal_mine` etc.) |
| Industry strike | another viewer's industry | `effect industryStrike` (months, multiplier 0.01 = −99%) |
| Tax raid | another viewer's nick | `effect industryTaxRaid`: all their industries at once (`entities`, key `target` = the viewer), multiplier 0.5 |
| Premium fuel | any line | `effect lineFuelPremium`: vehicles' top speed × 1.2 |
| Watered-down fuel | another viewer's line | `effect lineFuelBad`: vehicles' top speed × 0.8 |
| Brake sabotage | another viewer's line | `wreck`: 1 to "multiplier" (default 50%) of the vehicles disappear, can't be undone |
| Subsidy for the company | none (the viewer types nothing) | `subsidy`: "Amount" (default $10M) booked as a subsidy |
| Loan for the company | none (the viewer types nothing) | `loan`: a loan in the game's loan system, "Amount" ($10M) at "%" (2%) for "Game months" (60) |

- **Sabotage** (strike, tax raid, watered-down fuel, brakes) only works on other viewers' objects: an unowned target or the buyer's own = points refunded. The tax raid matches the nick like names (case, diacritics and `@` don't matter) against the owners in the export; a viewer without industries = refund. The multiplier's range depends on the reward (`RewardKinds.FactorRange`): boosts and premium fuel above 1, sabotage below 1, for brakes it's the largest share of the line's vehicles. In the description `{percent}` is the change without a sign ("drops by {percent}%").
- **Subsidy and loan** (`RewardKinds.HasAmount`): the streamer sets the amount in the "Amount" column of the **Rewards…** window (`RewardConfig.Amount`, $1,000 – $100B; in the description `{amount}`, e.g. "$10M"). For the loan the multiplier column is the interest rate in % (0–50), and "Game months" is the repayment period (1–360). The loan goes to the Finances → Loans tab in the game: the game collects an installment every month (amount / months + interest); interest in TF3 is simple, amount × % in total, so $10M at 2% is $200k of interest. The streamer can pay it off early in the game. The game allows at most 4 loans at once (including the streamer's own): when full, and also in a game with unlimited money, the points are refunded.
- **Brake sabotage:** the mod reports how many vehicles disappeared (`count` out of `total`). The chat reply says that the brake sabotage on line X destroyed 2 of 5 vehicles, and the in-game notification says "Technical problems on line X: vehicles destroyed: 2 of 5 (sabotage: viewer)"; clicking shows the line.

- **New town and industry:** Twitch has no link that opens a reward with the text filled in, so the viewer takes the code from the website: the 🏗 button on the map, click a spot, kind and name, "Copy code" ([../web/README.md](../web/README.md#building-for-channel-points)). `BuildCode` checks the code again against the last export: name 2–40 characters and not taken, the point at least 300 m from the map edge (`terrain.json`), a town ≥ 1500 m from towns and ≥ 600 m from industries, an industry ≥ 800 m from towns and ≥ 600 m from industries, the industry kind only among those on the map (the `.con` file is taken from the export). A town can't stand on water or in the mountains: `TownSites` (computed on every new terrain export, also published as `latest/terrain-site.png` for the website) says what share of the ±250 m area around the point is water and slopes > 15%; above 30% water or 50% slopes the code is rejected (the game would found a town there without a single street). Industries have regions (`TerrainZones`, `latest/terrain-zone.png`, a ±1 km window): **mountains** = average slope ≥ 8% and also at most the 15% most rugged land on the map, **by the water** = water in the window, **lowland** = average slope ≤ 3%, **away from water** = no water in the window. By default coal and ore mines and the quarry only in the mountains, the sand pit, sand dredger and clay pit by the water, farms in the lowland, the logging camp away from water; the streamer changes this in the "Rewards" window (stored in `rewards.json`, the `Zones` field), and Tf3Bridge publishes it to the website as `latest/build-rules.json`. A region the map doesn't have restricts nothing. Tf3Bridge logs how much land each region covers after a terrain export. The mod only sends the terrain after a save is loaded or after terraforming, so Tf3Bridge keeps a copy of the last export (`%LOCALAPPDATA%\Tf3Bridge\terrain-cache.json`, `Bridge:TerrainCacheFile`) and after a restart takes the terrain from there if it's the same game session. Without any terrain export these checks don't happen. Collisions with tracks, streets and buildings are only checked by the game; then the points are refunded. In the reward description `{site}` is replaced with the map address (`Twitch:SiteUrl`), and the chat reply links to the new object by the id the mod returns in the command result (`#town=<id>`, `#industry=<id>`); the website waits a moment for the object to appear in the export. When the game runs an older mod version without the id, the link points to a pin at the building spot (`#pin=x_y&note=Name`).

- **On Twitch:** Tf3Bridge creates the rewards itself (only those can it later change and settle), remembers their IDs in `%LOCALAPPDATA%\Tf3Bridge\rewards.json` and updates them after the settings are saved. The rewards are enabled only while the bridge runs and has received game data since starting (a save is loaded); on "Stop", without a new export for 2 minutes (e.g. the game is paused) and when Tf3Bridge closes they are disabled (viewers don't see them). Redemptions that still arrive while the game isn't running are refunded right away. Needs an Affiliate/Partner channel; without that nothing reaches Twitch.
- **Rewards carried out by the streamer** (`RewardKinds.Manual`): "TF3: bomb", "TF3: line", "TF3: nature", "TF3: road vehicle", "TF3: special vehicle". They have no effect in the mod. Tf3Bridge creates them like the others so it can enable and disable them together with the game (it couldn't change a reward added by hand in the Twitch dashboard). Their redemptions stay in the Twitch queue, and the streamer carries them out and accepts them or refunds the points. Tf3Bridge doesn't settle them, not even when the game isn't running, and doesn't refund old ones at startup. The streamer sets the description, cost and limits in the **Rewards…** window; there they also choose whether the viewer types text (the "Viewer text" column, off by default). They aren't in "Simulate reward".
- **Your own rewards prefixed TF3** (`ExtraReward`): the streamer's rewards whose title starts with "TF3" and which aren't any of our actions (e.g. "TF3: choose the next line"). Tf3Bridge does nothing with them in the game, and their redemptions stay in the Twitch queue for the streamer. It enables and disables them (`is_enabled`, without deleting) exactly when it does our actions. It reads the list once, when connecting to Twitch (Tf3Bridge start, login), and after the **Rewards…** window is saved; there each can be unchecked, and then it stays disabled for good. Note: Twitch only lets the application (Client ID) that created a reward change it. For a reward added by hand in the dashboard it answers 403. Tf3Bridge then shows it in the status ("Twitch won't let Tf3Bridge enable and disable: …") and doesn't touch it until the next start. A manual reward with the title of one of our actions blocks creating ours (Twitch rejects a repeated title) and also shows up in the status.
- **Redemption:** the viewer's text is matched to the names from the last `dynamic.json` (`TargetResolver`). Case, diacritics, spaces and "\| owner" don't matter. An exact match wins, then an unambiguous name prefix, then an unambiguous substring.
- **Settlement:** when the game confirms, the redemption gets FULFILLED. When the target is missing or ambiguous, the reward is disabled, or the game refuses or runs out of time, the redemption gets CANCELED and Twitch refunds the points.
- **Redemptions from while Tf3Bridge was off:** those fresher than 5 minutes are carried out, older ones are refunded.
- **Chat reply** (`RewardMessages`, `ChatApi`, Helix `POST /chat/messages` as the streamer). The switch is in **Rewards…**. Examples (in English; in Polish the same in Polish):
  - "@viewer town boost Szczecin (+50%) runs until 12 May 1922 (in-game date). https://tf3.example.com/#town=225799" The mod gives the end date in the game calendar in the command result (`until`).
  - "@viewer the town Kraków is now yours! https://tf3.example.com/#town=…"
  - The link points to the object on the website (`#town=`, `#industry=`, `#line=`). Website address: `Twitch:SiteUrl` (in `appsettings.Local.json`); empty = no links.
  - On a refund: "@viewer town boost: "gdańsk" not found. Your points have been refunded."
- **In-game notification:** after a reward is carried out, and only once Twitch accepts FULFILLED (points taken), Tf3Bridge sends the mod a `notify` command: a popup with the Twitch logo, "Twitch: viewer" and a description ("viewer bought the town Kraków."); clicking shows the object. There's none on a refund. The switch and display time (3–300 s, default 30 s; then the popup disappears and the entry stays in the game's notification log) are in **Rewards…**; in a simulation it gets `[TEST]`, and **Test actions…** also has a "Test notification" button. Details: [../docs/custom-notifications.md](../docs/custom-notifications.md).
- **Effects on the website:** the mod exports active effects in `dynamic.json` (`effects`), and the website shows them next to the town, industry and line ([../web/README.md](../web/README.md#effects-from-viewers)).
- **Testing without Twitch:** **Test actions…** → "Channel points reward (simulation)". The same path as a real redemption, with the result, whether there would be a refund, and the chat reply text. With "Also send the reply to chat" checked the reply really goes to the channel chat, so this also works without Affiliate status. In Twitch CLI mode `twitch event trigger channel.channel_points_custom_reward_redemption.add --transport=websocket -i AdoptTown` picks the reward by kind name, but the CLI always sends the text "Test Input From CLI", so it ends in a refund.

## Mod commands (viewer actions)

The path for viewers' game changes. Sources (`CommandSource`): channel points and the manual test in the window. Chat deliberately can't send anything here: chat commands are informational only, because the streamer has no control over them and they could break the balance.

- `ModCommandQueue` writes the queue to `local\towns_industries\tf3export_queue.lua` (`app.saveUserdata` format, written through a temporary file). The mod reads it every second and writes the results to `tf3export_results.lua` next to it. Format details: [../lua_mod/README.md](../lua_mod/README.md#commands-from-tf3bridge-viewer-actions).
- A command stays in the queue until the mod reports a result. The mod skips ids it has already handled, so rewriting the queue never runs anything twice. After 320 s without an answer (game off or without the mod) the command is marked `expired`.
- Text from viewers only reaches the Lua file as an escaped literal (`ModCommandLua.Str`), and argument names must be identifiers.
- Object names have the form `Name | Owner`, the same as on the website (`OwnerName`).
- **Test actions…** in the window: pick a town, industry, station or line from the last `dynamic.json`, a viewer and an action (set owner, remove owner, new name), then send it to the game and see the result. TF3 has to be running with the mod enabled for the test.

## What one pass does (`ExportProcessor`)

1. Reads the `<kind>_<session>_<seq>.lua` files. Other files, e.g. `bench_*`, are skipped.
2. **The session** is the one with the newest file. Files from older sessions are deleted, because they come from an earlier game run or a loaded save.
3. **Network:** applies `network` (full dump) and `networkDelta` in `seq` order. A diff without this session's dump is skipped with a warning. A diff after a gap (a missing `seq`) is applied anyway, because it's upserts and removals, and a warning is logged.
4. **Dynamic:** publishes only the newest snapshot and deletes older ones.
5. Publishes the changed `latest/*.json` files atomically (temporary file, then rename) and deletes the processed input.

A file that can't be read yet (no closing `end`, i.e. the game is still writing it) stays for the next pass. Later network diffs wait for it to keep the order.

## Format of the game files

`app.saveUserdata` writes JSON as a Lua string literal:

```lua
function data()
return { 
		json = "{\"v\":5,\"kind\":\"dynamic\",...}",
	}
end
```

`LuaUserdataReader` cuts out the literal and reverses Lua escaping (`\"`, `\\`, `\n`, `\ddd`, `\xXX`…). It works on bytes, so UTF-8 (e.g. Polish names) passes through unchanged. The JSON fields are described in [../lua_mod/README.md](../lua_mod/README.md).

## Structure

```
middle_app/
  Tf3Bridge.sln
  src/Tf3Bridge/
    Program.cs                    service registration, window start
    BridgeOptions.cs              configuration
    BridgeRunner.cs               loop every PollIntervalMs (start/stop from the window): a pass + upload to Azure
    Ui/MainForm.cs                window: state, log, tray
    Ui/AzureDialog.cs             Azure settings with a connection test
    Ui/ModSettingsDialog.cs       mod settings (export frequency, vehicles, terrain)
    ModSettings.cs                mod settings and writing them as settings.lua for app.loadUserdata
    Ui/UiLogProvider.cs           logs to the window
    Cloud/AzureSettings.cs        container address + SAS, stored encrypted (DPAPI)
    Cloud/AzureMirror.cs          queue of changed files and upload to Blob Storage
    Cloud/MirroringPublisher.cs   local output + marking the file for upload
    Twitch/TwitchAuthClient.cs    Twitch OAuth: device code, refresh, validate, revoke, channel type
    Twitch/TwitchAccount.cs       streamer account + tokens, stored encrypted (DPAPI), TwitchOptions
    Twitch/TwitchSession.cs       hourly validation and token refresh, state for the window
    Twitch/EventSubClient.cs      EventSub over WebSocket: subscriptions, keepalive, reconnect, chat and points events
    Ui/TwitchDialog.cs            login with a code from twitch.tv/activate, logout
    Commands/ModCommand.cs        a mod command and writing it as Lua (escaping viewers' text)
    Commands/ModCommandQueue.cs   the queue.lua queue, reading results from results.lua, expiry
    Commands/GameObjects.cs       action targets from dynamic.json, "Name | Owner" names
    Commands/OwnerAssets.cs       viewers' estates from dynamic.json (like the Owners tab on the website)
    Chat/ChatCommandService.cs    chat commands (!...): matching names from the settings, cooldowns; no game changes
    Chat/ChatCommandSettings.cs   chat command names, aliases, on/off, cooldowns, stored in chat-commands.json
    Ui/ChatCommandsDialog.cs      chat command settings
    Chat/ChatAnswers.cs           reply texts for chat commands (from dynamic.json)
    Ui/CommandTestDialog.cs       testing without Twitch: reward simulation and manual rename
    Rewards/RewardSettings.cs     rewards (kinds, costs, months, multipliers), stored in rewards.json
    Rewards/TargetResolver.cs     matching the viewer's text to a town, industry or line
    Rewards/RewardService.cs      rewards on Twitch, redemptions -> commands, FULFILLED/CANCELED, disabling without the game
    Twitch/ChannelPointsApi.cs    Helix: rewards and redemption status
    Twitch/ChatApi.cs             Helix: a chat message as the streamer
    Rewards/RewardMessages.cs     reply texts for viewers (effect end date in the game, refund reason)
    Ui/RewardsDialog.cs           reward settings (balance)
    Input/LuaUserdataReader.cs    extracting JSON from .lua files
    Input/ExportFile.cs           parsing file names
    Processing/ExportProcessor.cs one pass: sessions, network, dynamic, publishing
    Processing/NetworkState.cs    assembling the network from a dump and diffs
    Processing/JsonRead.cs        safe reads of numbers and strings from JsonNode
    Output/IPublisher.cs          publish target interface
    Output/LocalFolderPublisher.cs
  tests/Tf3Bridge.Tests/          xUnit: reader, network assembly, processor pass, Azure and mod settings, Twitch login, EventSub, command queue, rewards
```

Script that creates the Azure resources: [../infra/azure-setup.ps1](../infra/azure-setup.ps1).

## Next steps

- The website is a separate project in [../web/](../web/README.md). Locally it reads what Tf3Bridge publishes to `web/data/latest/`.
