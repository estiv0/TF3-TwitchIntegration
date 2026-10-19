# Architecture (proposal)

Goal: as cheap as possible on Azure, ideally close to zero per month.

## Flow

```
 TF3 + Lua mod
   │  app.saveUserdata → ...\3493540\local\tf3export\*.lua (JSON inside a Lua literal)
   │  fallback: [tf3x] lines in crash_dump\stdout.txt
   ▼
 Middle app (C# / .NET, on the streamer's PC)
   │  gzip, HTTPS upload straight to Blob Storage (write-only SAS token)
   ▼
 Azure Blob Storage ("live" container, public read)
   │  latest/dynamic.json.gz     ← overwritten on every snapshot
   │  latest/network.json.gz     ← overwritten when the network changes
   │  latest/meta.json           ← sessionId, seq, time: a small file to poll
   │  history/<session>/<seq>.json.gz   (optional, for charts)
   ▼
 Website (Azure Static Web Apps, Free plan)
     polls meta.json every ~5–10 s; when seq changes, it downloads dynamic.json
     downloads network.json only when its version changes
```

### Why no server or database to start with

- The data has **one writer** (the streamer's mod) and **many readers**. That is a "static file + CDN" case, not a database.
- Blob Storage costs pennies: a few MB of data, and writing every ~30 s is about 90k writes a month. Static Web Apps Free hosts the site for free.
- No server code, no Functions cold starts and no connection management.
- Files are gzipped, the middle app sets `Content-Encoding: gzip` on upload, and the browser unpacks them on its own.
- CORS is set on the Storage Account for the site's domain.

> Check Free plan prices and limits in the current Azure price list before deploying.

### When to add more

| Need | Add-on | Cost |
|---|---|---|
| History, charts over time | Azure Table Storage (NoSQL, key `session/seq`) or Cosmos DB with the free tier (one per subscription) | pennies / 0 |
| Hiding the SAS token, validating data | Azure Functions (Consumption plan, free call allowance) as a `POST /ingest` endpoint | ~0 |
| Real push instead of polling | Azure Web PubSub (the Free plan has a very low connection limit, so it does not scale to viewers) or SignalR | paid with more traffic |

Polling every 5–10 s is enough for an MVP. The data changes every 2 game days anyway, i.e. every few dozen seconds.

## Middle app (C#)

**Status (2026-10-02):** the local version is done in `middle_app/` (`Tf3Bridge`) and tested on real game files. It publishes `latest/dynamic.json`, `latest/network.json` and `latest/meta.json` to a local folder. Details in [../middle_app/README.md](../middle_app/README.md). Upload to Azure Blob Storage is done (`AzureMirror`, `infra/azure-setup.ps1` creates the resources) but not yet tested on a real account. Hosting the site on Static Web Apps is not done yet.

- `FileSystemWatcher` on `local\tf3export\*.lua` plus a periodic folder scan as a safety net, because the watcher loses events when its buffer overflows.
- It extracts the `json = "..."` literal from the file and reverses Lua escaping (`\"`, `\\`, `\n`, `\r`, `\t`, `\ddd`). A file without the closing `end` is still being written, so the read has to be retried.
- Files are handled in `(session, seq)` order. Uploads retry with backoff, and a file is deleted **only after** a successful upload. When there is a backlog, sending the newest `dynamic` and the newest `network` is enough.
- Fallback (when `app.saveUserdata` fails): tail `crash_dump\stdout.txt` and join the `[tf3x] <kind> <session> <seq> <part>/<total> ...` lines.
- Folder settings live in `appsettings.json`; the container URL and SAS token are entered in the app window and kept in `%LOCALAPPDATA%\Tf3Bridge\azure.json` (SAS encrypted with DPAPI), outside the repo.
- Best as a tray-icon app with a status view: last seq and errors.

## Twitch extension (after the MVP)

An extension is a static frontend (HTML/JS) hosted by Twitch and shown in one of three places:

| Type | Where | Idea for us |
|---|---|---|
| **Panel** | below the stream, in the About tab | Mini dashboard: company, balance, number of lines and vehicles, top towns |
| **Video overlay** | a transparent layer over the whole stream (desktop) | The viewer hovers over the map or an icon and sees line or town details. The network map as a toggleable overlay. |
| **Component** | a small, movable element on the video | A counter or event ticker ("new line", "town X grew") |

Limits to know about:
- Frontend files are uploaded to Twitch as a package. External requests (to our Blob) must be on the extension's allowed URL list.
- Twitch suits a small push via Extension PubSub from a backend (small messages, rate limited), e.g. a "new seq available" signal. Viewers still download the large data from the Blob.
- A public release needs a review by Twitch. Testing on your own channel does not.
- Viewers interacting with the game (votes for a new line, train names from subs) is a separate, bigger topic: it needs a site → mod direction that the mod could read from a file.

**Recommendation:** build the MVP as a normal website, but so that its components (map, dashboard) can later be plugged into an extension without a rewrite. A static build without SSR and a single data source (the Blob) is enough.
