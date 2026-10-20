# TF3 Twitch Integration

Let your Twitch viewers watch and play along with your **Transport Fever 3** game.

The project has three parts:

- **a game mod** (`lua_mod/`) that exports the live game state and carries out viewers' actions,
- **Tf3Bridge** (`middle_app/`), a small Windows app that runs next to the game, talks to Twitch and publishes the data,
- **a static website** (`web/`) with a live map of your world: towns, industries, stations, lines and moving vehicles.

Viewers browse the map on the website, buy towns and industries, boost or sabotage them with **channel points**, and ask the chat bot about the game. Chat commands only *read* the game; the game itself changes only through channel points rewards, which you control (costs, cooldowns, limits).

The interfaces of Tf3Bridge and the website are in **English and Polish**.

## How it works

```mermaid
flowchart LR
    subgraph PC["Streamer's PC"]
        TF3["Transport Fever 3<br/>+ mod"]
        BR["Tf3Bridge"]
        LOCAL["web/data<br/>(local copy)"]
    end
    BLOB[("Azure Blob Storage<br/>'live' container")]
    SITE["Static website"]
    TW["Twitch<br/>(EventSub, Helix)"]
    V(["Viewers"])

    TF3 -- "export files<br/>(app.saveUserdata)" --> BR
    BR -- "command queue" --> TF3
    BR --> LOCAL
    BR -- "changed files<br/>(gzip, SAS token)" --> BLOB
    SITE -- "polls meta.json,<br/>fetches data on change" --> BLOB
    V --> SITE
    V -- "channel points,<br/>chat commands" --> TW
    TW -- "redemptions,<br/>chat messages" --> BR
    BR -- "chat replies,<br/>fulfill / refund" --> TW
```

**Game → website**

1. The mod collects the game state on the GUI thread, spread over frames so the game doesn't stutter: a dynamic snapshot (towns, industries, stations, lines, vehicles with positions, company finances, active effects) every couple of game days, the street and track network as a full dump plus diffs after every build, and a terrain heightmap. It writes them with `app.saveUserdata`, the only file access a TF3 mod has.
2. Tf3Bridge polls that folder, assembles the network from dumps and diffs, renders the terrain images and publishes ready-made JSON files (`latest/meta.json`, `dynamic.json`, `network.json`, …) to `web/data/` locally.
3. If Azure is enabled, Tf3Bridge mirrors every changed file to a public, read-only Blob Storage container (gzipped, `meta.json` last).
4. The website is plain static files. Every few seconds it fetches the small `meta.json` and downloads the rest only when the data changed. There is no server code anywhere.

**Twitch → game**

1. Tf3Bridge logs in as the streamer and listens to Twitch EventSub over WebSocket: chat messages and channel points redemptions. No public server or webhook is needed.
2. A redemption is matched to a game object (the viewer types a town, industry or line name, a viewer's nick, or a build code copied from the website) and turned into a command in a queue file the mod reads every second.
3. The mod executes the command through the game API and writes the result back. Tf3Bridge then marks the redemption as fulfilled, or cancels it so Twitch refunds the points, and replies in chat with a link to the object on the website. Fulfilled rewards also pop up as an in-game notification.
4. Rewards are enabled on Twitch only while the game is running with the mod, so viewers can't spend points on a paused or closed game.

More detail: [docs/architecture.md](docs/architecture.md), [docs/twitch-channel-points.md](docs/twitch-channel-points.md).

## Features

### Website

- A live map of the street and track network on a shaded terrain relief, with vehicles moving along their lines. Pan and zoom, also on touch screens.
- Lists and details for towns, industries, stations and lines, an **Owners** tab with each viewer's estate and a **Company** tab with your finances and history charts.
- Badges for active viewer effects (boosts, strikes, stopped lines…) with their end date in game time.
- A build tool that lets viewers pick a spot for a new town or industry and copy a code for the reward. It checks the same rules as Tf3Bridge (distances, water, slopes, terrain regions per industry type).
- Shareable links to any object (`#town=…`, `#line=…`, `#owner=…`), an optional Twitch mini player, and an English/Polish switch.

### Channel points rewards

Tf3Bridge creates and manages the rewards on your channel. Each one can be disabled and balanced in the **Rewards…** window (cost, cooldown, limits, duration in game months, strength).

| Group | Rewards |
|---|---|
| Ownership | buy a town, buy an industry (the object gets the viewer's name: `Kraków \| viewer`) |
| Building | new town, new industry (at a spot chosen on the website) |
| Boosts | town boost, industry boost, premium fuel for a line |
| Sabotage (other viewers' objects only) | industry strike, tax raid, watered-down fuel, brake sabotage |
| Line control | stop a line, make a line wait at stations |
| Company | subsidy, loan |

You can also add rewards you carry out yourself; Tf3Bridge enables and disables them together with the game. The full list with all rules: [middle_app/README.md](middle_app/README.md#channel-points-rewards).

### Chat commands

Informational only, answered by Tf3Bridge as the streamer. Each command also has a Polish name (`!mapa`, `!majatek`, …).

`!help`, `!map`, `!assets [viewer]`, `!town <name>`, `!industry <name>`, `!line <name>`, `!ranking`, `!free [towns|industries]`, `!date`, `!company`, `!best [line|company]`, `!worst [line|company]`

Details and cooldowns: [middle_app/README.md](middle_app/README.md#chat-commands).

## Requirements

- **Transport Fever 3** (Steam) on **Windows**.
- **Tf3Bridge**: the published exe is self-contained and needs no .NET install. To build it you need the **.NET 9 SDK** (Windows, WinForms).
- A **Twitch** account. Channel points rewards need **Affiliate or Partner** status; without it the website and chat commands still work.
- For a public website: an **Azure** subscription with a Storage account (for the data) and, optionally, a Static Web App (for the site). The setup is designed to be close to free: a few MB of blob data, pennies a month in writes, and Static Web Apps' Free plan. Check current Azure pricing before you deploy.
- Tools for setup: Azure CLI, Python 3 (game icon extraction) and Node.js (website deployment to Static Web Apps).

## Setup

### 1. Build and start Tf3Bridge

```powershell
.\infra\publish.ps1
```

This builds a self-contained `middle_app\dist\Tf3Bridge.exe` and creates a desktop shortcut. During development you can also run it from source:

```bash
cd middle_app/src/Tf3Bridge
dotnet run
```

The app finds your Steam folder and the TF3 user data on its own.

### 2. Install the mod

Nothing to copy: Tf3Bridge carries the mod inside the exe and installs or updates it in TF3's `staging_area` at every start. Enable **TF3 Data Export** when you create or load a game.

To install the mod without Tf3Bridge, build a zip with `lua_mod\pack.ps1` (see [lua_mod/README.md](lua_mod/README.md)).

Start the game. Within a few seconds Tf3Bridge shows the first publish, and the local website (step 7) has data.

### 3. Log in to Twitch

In Tf3Bridge click **Twitch…** → **Log in with Twitch**. The app shows a code and opens `twitch.tv/activate`, where you approve access (device code flow, no password ever goes through the app). The tokens are stored encrypted for your Windows account.

Once logged in, Tf3Bridge connects to EventSub, answers chat commands and, on an Affiliate/Partner channel, creates the rewards. Tune them in **Rewards…**.

### 4. Create the Azure storage

```powershell
winget install Microsoft.AzureCLI
.\infra\azure-setup.ps1 -Name <storageaccount>
```

The account name is 3–24 lower-case letters and digits and must be unique in Azure. The script creates a resource group, a Storage account with a public read-only `live` container and CORS, and prints the container URL and a write-only SAS token valid for a year (`-SasOnly` issues a new one later).

In Tf3Bridge click **Azure…**, paste the URL and the token, **Test connection**, **Save**. From now on every change is mirrored to the container.

#### Storage backends

| Backend | Status |
|---|---|
| Azure Blob Storage | supported |
| S3-compatible storage | planned |

### 5. Configure the website

Copy the template and fill in your values:

```powershell
Copy-Item web\site.example.json web\site.json
```

| Field | Meaning |
|---|---|
| `title` | site name in the top bar and the browser tab (default "TF3 Live") |
| `logo` | an image before the name; put it in `web/img/site/` (e.g. `img/site/logo.png`); empty = no logo |
| `twitchUrl` | optional, `https://www.twitch.tv/<channel>`; the stream button and mini player only appear when it's set |
| `dataUrl` | where the site reads the data when not on localhost: `https://<storageaccount>.blob.core.windows.net/live/latest/` |

Both `web/site.json` and `web/img/site/` are gitignored, so your branding stays out of the repository.

Tell Tf3Bridge where the site lives, so chat replies and reward descriptions can link to it. Create `middle_app/src/Tf3Bridge/appsettings.Local.json` (gitignored, copied next to the exe by `publish.ps1`):

```json
{ "Twitch": { "SiteUrl": "https://your-site.example/" } }
```

### 6. Extract the game icons

Vehicle, cargo and station icons are Urban Games artwork, so they aren't in the repository. Extract them from your own game install once after cloning and after game updates:

```powershell
python tools\extract_game_icons.py "<path to Transport Fever 3>"
```

Without an argument the script uses the `TF3_GAME_DIR` environment variable or the default Steam library.

### 7. Preview and deploy the website

Preview locally (reads `web/data/` written by Tf3Bridge):

```powershell
.\web\serve.ps1
```

Then open http://localhost:5080.

Deploy to **Azure Static Web Apps** (create a Static Web App in the Azure portal first, then copy its deployment token from **Overview → Manage deployment token**):

```powershell
.\infra\deploy-web.ps1 -DeployToken "<token>"   # first time, the token is remembered encrypted
.\infra\deploy-web.ps1                          # next times
```

Alternatively, host the site as a Storage account static website with `-Target storage`. The script refuses to deploy without the game icons from step 6. Details: [web/README.md](web/README.md#deploying-to-azure).

`.\infra\make_all.ps1` does a full release in one go: publish Tf3Bridge, zip it and deploy the website.

## For forks and developers

### Use your own Twitch application

Tf3Bridge is a public Twitch client (no client secret). The Client ID in `middle_app/src/Tf3Bridge/appsettings.json` belongs to this project's Twitch application. If you distribute your own build, register your own application at [dev.twitch.tv](https://dev.twitch.tv/console) as a **Public** client and put its Client ID in `appsettings.Local.json`:

```json
{ "Twitch": { "ClientId": "<your client id>", "SiteUrl": "https://your-site.example/" } }
```

Note that Twitch only lets the application that created a reward change it, so rewards created under one Client ID can't be managed under another.

### Repository layout

```
lua_mod/       the TF3 mod (Lua 5.2): export, commands, timed effects, in-game notifications
middle_app/    Tf3Bridge (.NET 9, WinForms) and its xUnit tests
web/           the static website (HTML, CSS, JS modules, no build step)
infra/         PowerShell scripts: Azure setup, publishing, website deployment, notification icon
tools/         extract_game_icons.py
docs/          design notes and findings about the TF3 modding API
```

Each part has its own README with the details: [lua_mod](lua_mod/README.md), [middle_app](middle_app/README.md), [web](web/README.md). The [docs index](docs/README.md) lists the design notes.

### Running the tests

```bash
cd middle_app
dotnet test
```

Twitch can be tested without a live channel: **Test actions…** in Tf3Bridge simulates reward redemptions, and the Twitch CLI's EventSub test server is supported too ([middle_app/README.md](middle_app/README.md#chat-commands)).

### Translations

Tf3Bridge and the website currently speak English and Polish. Translations into other languages are welcome; see [middle_app/README.md](middle_app/README.md#language) and `web/js/i18n.js` for where the texts live.

## License and trademarks

The code is released under the [MIT License](LICENSE).

Transport Fever 3 is a trademark of Urban Games. No game assets are included in this repository; the icons are extracted from your own copy of the game. Twitch and the Twitch logo are trademarks of Twitch Interactive, Inc.; the logo is used as the icon of the in-game reward notification, in line with the Twitch brand guidelines. This project is not affiliated with or endorsed by Urban Games or Twitch.
