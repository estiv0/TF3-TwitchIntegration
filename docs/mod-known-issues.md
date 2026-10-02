# Znane błędy i ryzyka w `lua_mod`

Stan na 2026-10-02, po przebudowie na strukturę TF3. Szczegóły API opisuje [tf3-api-findings.md](tf3-api-findings.md).

Legenda: ✅ naprawione · ⚠️ częściowo lub do potwierdzenia w grze · ⏳ otwarte

## Blokery ładowania

| # | Problem | Status |
|---|---|---|
| B1 | Struktura TF2 (`mod.lua`, `res/`) | ✅ `mod.json`, `_metadata/modinfo.json`, `_content.json`, `content/tf3export/*.gs.lua` + `*.script.lua` |
| B2 | `data()` z `save` / `load` / `update` | ✅ moduł zwraca `{ update, handleEvent }` jak `reforestation.script.tl` |
| B3 | `require` | ✅ `ug_require "x.lua"` (względnie, jeden folder) |
| B4 | Dostępność `io` / `os` | ✅ sprawdzone sondą: brak `io` i `os.rename`, za to `app.saveUserdata` działa w wątku GUI (patrz #13) |
| B5 | Format `.script.lua` | ✅ Gra rozpoznaje plik, ale wymaga globalnego `function data() return { update = ..., ... } end` (wzór: `base/base_mod.script.lua`). Styl `return {...}` działa tylko w `.tl`. Biblioteki ładowane przez `ug_require` mogą zwracać moduł przez `return`. |

## Błędy logiczne

| # | Problem | Status |
|---|---|---|
| 1 | Wspólny licznik `seq` dla dynamic i network | ✅ osobne `seqDynamic` i `seqNetwork` |
| 2 | Wczytanie starszego save'a cofa `seq` i nadpisuje pliki | ✅ `session` to ID wątku GUI, nowe przy starcie gry i po cofnięciu czasu gry. `seq` nigdy się nie powtarza w obrębie sesji. |
| 3 | Snapshot sieci nieatomowy | ⏳ zaakceptowane i opisane. Strona musi tolerować brakujące węzły. |
| 4 | Puste mapy kodowane jako `[]` | ✅ `output` i `cargoNeeds` używają `json.EMPTY_OBJECT` |
| 5 | Brak kontroli błędów zapisu | ✅ `app.saveUserdata` jest wołane w `pcall`, a przy błędzie mod pisze do logu |
| 6 | Budżet czasu na tick | ✅ sieć jest budowana w `guiUpdate` porcjami po 2 ms na klatkę (wątek GUI jest jeden, więc stan modułu przetrwa między klatkami) |
| 10 | `getEntitiesWithComponent` nie działa dla LINE i BASE_EDGE | ✅ linie z `lineSystem.getLinesForPlayer` i z pojazdów, krawędzie z `streetSystem.getNode2SegmentMap` |
| 11 | Korutyny psują wywołania C gry (`log.message`) | ✅ korutyny usunięte |
| 12 | update() wołane z wielu wątków (`Sim Pool 1..N`), każdy z osobnym stanem Lua | ✅ cały eksport przeniesiony do `guiUpdate` (jeden wątek GUI), a `update` jest pusty |
| 13 | Brak zapisu plików (`io`, `os.rename`) | ✅ `app.saveUserdata` w wątku GUI zapisuje do `local\tf3export\*.lua`, a log zostaje jako zapas |
| 7 | Zmiany sieci wykrywane po liczbie krawędzi | ⚠️ zdarzenie `onPostBuildProposal` (w `handleEvent`) podbija `netVersion` we wspólnym stanie, a GUI przebudowuje sieć i zapisuje `networkDelta`. Do tego pełne porównanie co 5 min. Rewizje okazały się bezużyteczne, bo zmieniają się ciągle. Potwierdzone w grze: `networkDelta` działa (260–600 B po budowie lub wyburzeniu). Zdarzenie przychodzi jednak co ~3 s także bez zmian w sieci, więc dodałem filtr `touchesNetwork()`, jeszcze nie testowany w grze. |
| 8 | Stała długość dnia | ✅ `GAME_SPEED.millisPerDay` z zapasem `api.util.getDefaultDayDuration()`. Do sprawdzenia, czy komponent wisi na encji świata. |
| 9 | Typ krawędzi, ciche `{0,0,0}` | ✅ `road` = `STREET`/`TRACK` (z `roadType`), `type` = `NORMAL`/`BRIDGE`/`TUNNEL`. Krawędzie bez pozycji są pomijane i liczone w logu. |

## Drobne i otwarte

- ✅ Eksport dynamiczny działa w grze: ok. 120 KB, zbieranie ok. 5 ms, kodowanie ok. 6 ms. Nazwy enumów, data, saldo i ładunki są poprawne.
- ✅ Sieć: 8698 krawędzi, ok. 1 MB, kodowanie 150–200 ms w sumie. Teraz rozłożone na klatki GUI. Pomiar w nowym układzie jeszcze czeka.
- ⚠️ Ładunki są eksportowane jako nazwy zasobów (`cargoTypeRep.getName`). Nazwy wyświetlane (`cargoTypeRep.get(id).name`, lokalizowane) można dodać później.
- ⏳ `rules[].input` to surowe indeksy stocków.
- ⏳ `sessionId` opiera się na `os.time()` i `math.random`. Jeśli `os` nie jest dostępne, a `random` nie jest seedowany, ID może się powtarzać między uruchomieniami.
