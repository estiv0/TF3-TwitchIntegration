# The "Owners" tab: viewers' estates (2026-10-05)

Status: **stage 1 done without the map** (list, viewer details, `#owner=` link), **stage 2 (`!majatek`) done.**
To do: highlighting the estate on the map.

## Problem

A viewer can own several towns, industries, stations and lines. They don't have to be in one place or be
connected to the viewer's "main" town. Before, ownership was only visible in the details of a single
object, and searching by viewer name worked on each tab separately.

## Where the data comes from

The source of truth is the object name in the game: `Name | Owner` (`adopt`, `createTown`, `createIndustry`),
split on the website in `splitAllOwners` (`web/js/format.js`). The summary is computed in the browser
from `dynamic.json` (`web/js/owners.js`, `ownersIndex`). **No changes to the mod or Tf3Bridge.**

| Asset | From |
|---|---|
| Towns | `towns[].owner` |
| Industries | `industries[].owner` |
| Stations | `stations[].owner`; a station of the same viewer's industry is counted with the industry. The game names stations after the town, including a bought one ("Dworzec Szczecin \| estiv0 Zachód"), so `splitAllOwners` first replaces full town and industry names with their owner in station and stop names with the bare names ("Dworzec Szczecin Zachód", without the owner) |
| Lines | `lines[].owner` |

Decisions:
- **Main town = the viewer's largest town** (`townSize`). It can change when another of their towns grows.
- **Identity:** we group case-insensitively (`Ala` = `ala`); the list shows the most common
  spelling. Tf3Bridge stores the viewer's display name, so after a name change on Twitch the old objects
  stay under the old name. This is a known limitation that we deliberately don't solve.

## Stage 1: the website

Done:
- an "Owners" tab with a viewer count; a row is the name and object counters (towns, industries,
  stations, lines, non-zero only);
- sorting: estate (town 100 pts + size/25 + growth since game start ×100, industry 50, station and line 1, vehicle on lines: train/plane 10, ship 2, road 1; the same formula in `!ranking`), line earnings (sum of `stats.profit12m`), name;
- searching by viewer name and by the names of their objects ("Szczecin" shows the owner of Szczecin);
- viewer details in a floating window: main town, summary, clickable object lists (★ next to the
  main town); hovering an object highlights it on the map;
- vehicles on the viewer's lines: count (also in the list), split by carrier, on the way / at a stop /
  stopped (from `vehicles[]`; without it only the sum of `lines[].vehicleCount`); statistics of their lines combined:
  profit for 12 months, for the previous year and the total over all finished years (`stats.profitTotal`,
  "Total earnings" or "Total loss"), transported, capacity (sums), load factor (average weighted
  by vehicle count) and cargo in vehicles right now (sum per cargo);
- the owner's name in the details of every object is a link to their estate;
- the `#owner=<key>` address (the name in lower case, `encodeURIComponent`), also works when opened from scratch;
- the tab bar scrolls horizontally, because six tabs don't fit in a 360 px panel or on a phone.

To do: **the map.** After selecting a viewer the map should highlight all their objects at once and fit
the view to their bounds. Today `map.js` knows a single selection `{ kind, id }`, so choosing a viewer only clears
the map selection (`main.js`, `onPick`). Needed:
- `selected = { kind: "owner", items: [{ kind, id }] }` and outlines for each item (lines thinner than
  for a single selected line, so the map stays readable with many lines);
- `fit()` to the objects' bounds (`fit(bbox, maxScale)` already exists).

## Stage 2: the chat command (Tf3Bridge): done, described in `middle_app/README.md`

`!majatek [viewer]` answers e.g. "ala: 2 towns (Gdańsk, Sopot), 1 industry, 3 lines, line profit
$1.2M, map: <link #owner=ala>". Without an argument it shows the asker's estate. The command only reads
the last `dynamic.json` (`OwnerName.Split` in `GameObjects.cs`, grouping as in `owners.js`) and **sends nothing
to the game**, following the rule that only channel points change the game (`CommandSource`). It needs to read
chat (`user:read:chat`, `user:write:chat`), see `twitch-channel-points.md`, point 4.
Our commands have no prefix.

## Postponed: an owner registry in Tf3Bridge

Not needed for now. Let's come back to it if we need a purchase history (when and for which reward),
a "main town" chosen by the viewer, or merging objects after a viewer renames (`user_id` from EventSub).
Idea: Tf3Bridge keeps an `owners.json` appended after a successful `RewardOutcome`, while the name in the game stays the source of truth
for who owns what right now.
