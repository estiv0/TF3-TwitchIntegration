# TF3: ustalenia z dokumentacji i plików gry

Stan na 2026-10-02. Źródła: script-doc na wiki TF3, strony wiki o modach oraz pliki gry z `E:/SteamLibrary/steamapps/common/Transport Fever 3`.

**Wniosek:** `lua_mod` miał strukturę z TF2 i w TF3 by się nie załadował. Mod został już przebudowany na strukturę TF3, a status poszczególnych punktów jest w [mod-known-issues.md](mod-known-issues.md).

Lokalizacje na tym PC:
- staging area dla modów w trakcie tworzenia: `C:\Program Files (x86)\Steam\userdata\38287428\3493540\local\staging_area\`
- ręcznie instalowane mody: `...\3493540\local\mods\`
- log gry: prawdopodobnie `...\3493540\local\crash_dump\stdout.txt`

## 1. Struktura moda

TF3 nie używa `mod.lua` z `function data()`. Wymagany układ ([wiki: Mod Definition](https://wiki.transportfever3.com/doku.php?id=modding:general:moddefinition)):

```
mods/
  kinek_tf3export/              nazwa folderu bez znaczenia, zalecane = modId
    mod.json                    techniczna definicja (modId, revision, severity, skrypty)
    _metadata/
      modinfo.json              nazwa, opis, autorzy, tagi (["Script Mod"])
    content/                    zasoby moda, struktura jak w base/content
      ...
    _content.json               lista plików; w modach z gry jest obecny (do sprawdzenia, czy gra generuje go sama)
```

- `modId` może zawierać tylko `a-z0-9_`. **Nie ma już sufiksu `_1`** (w modach z gry nadal występuje, np. `urbangames_sandbox_1`, ale wiki mówi, że nie jest wymagany).
- Ręcznie instalowane mody: katalog `mods/` (do ustalenia, czy w instalacji gry, czy w profilu użytkownika). W grze jest `mods/release/` z modami Urban Games.

Przykład z gry (`mods/release/urbangames_sandbox/mod.json`):

```json
{
    "modId": "urbangames_sandbox_1",
    "revision": 1,
    "preRunScript": { "fileName": "urbangames_sandbox_1::/mod.script@preRunFn" },
    "runScript": { "fileName": "" },
    "postRunScript": { "fileName": "" },
    "severityAdd": "None",
    "severityRemove": "None"
}
```

## 2. Game script: zupełnie inny mechanizm

TF2 miał `res/config/game_script/*.lua` z `data()` zwracającym `{ save, load, update, guiUpdate }`.

TF3 używa **pary plików** w `content/` (wzór: `base/content/terrain/`):

`reforestation.gs.lua` to deskryptor:
```lua
function data()
	return {
		updateScript = { fileName = "reforestation.script@update" },
		handleEventScript = { fileName = "reforestation.script@handleEvent" },
	}
end
```

`reforestation.script.tl` to implementacja (Teal; zwykła Lua też powinna działać):
```lua
local ret : GameScript<State> = {
	update = function(_userParams, state : GameScriptState<State>, dt : number) : any
		if dt == 0 then return end           -- pauza
		local s = state:get()
		-- ...
		state:set(s)                          -- stan trwały, zapisywany w save
	end,
	handleEvent = function(_userParams, state, src, id, name, param) : any
		if id == "" and (name == "initNewGame" or name == "handleLegacy") then ... end
	end,
}
return ret
```

Najważniejsze różnice:
- **Nie ma `save()` ani `load()`.** Stan trwały jest w `state:get()` / `state:set()` (komponent `GAME_SCRIPT`), więc nasz `state` (seq, lastDynamicTime) trzeba tam przenieść. Gra sama zapisuje go w save.
- `update(userParams, state, dt)`: `dt` to krok symulacji w sekundach, `dt == 0` oznacza pauzę.
- `handleEvent` dostaje m.in. `initNewGame` i `handleLegacy`. Przez `state:subscribeToEvent(name)` można subskrybować inne zdarzenia (np. `ArriveAtStop`, patrz `content.scripts.gamescript`).
- Istnieje wariant `GameScriptWithGui` z `guiUpdate` / `guiHandleEvent` (wątek GUI, stan read-only).
- **Do sprawdzenia:** jak gra wykrywa `*.gs.lua` z moda. W grze nikt ich nie rejestruje jawnie, więc prawdopodobnie wystarczy, że plik leży w `content/`. Inne skrypty odwołują się do nich jako `::/landmarks/landmarks.gs`.

## 3. Require

- `require` zastępuje `ug_require "ścieżka"` ([wiki: syntax](https://wiki.transportfever3.com/doku.php?id=modding:general:syntax)).
  - `ug_require "::/scripts/x.tl"`: od korzenia `content/`
  - `ug_require "x.tl"`: względnie do bieżącego pliku
  - `ug_require "modid::/scripts/x.tl"`: plik z konkretnego moda
- Biblioteki moda trzymamy więc np. w `content/scripts/tf3export/*.lua` i ładujemy przez `ug_require "::/scripts/tf3export/json.lua"`.

## 4. Środowisko Lua

- Silnik to **Lua 5.2** (w `TransportFever3.exe` jest napis "Lua 5.2.2"), nie LuaJIT. `coroutine`, `table.concat` i `string.format` działają standardowo. W 5.2 `pcall` pozwala na yield, ale **nie można robić yield z callbacku** przekazanego do funkcji C (np. `forEachEntityWithComponent`).
- **`io` i `os`: NIEZNANE.** Żaden skrypt gry ich nie używa. To najważniejszy test w konsoli, bo bez `io.open` cały plan z plikami JSON odpada:
  ```lua
  print(io, os, io and io.open, os and os.rename, os and os.clock)
  ```
  Plan B, jeśli `io` jest zablokowane: pisać dane przez `print`/`log.message` do logu gry z prefiksem (`[tf3export]{json...}`), a middle app śledzi plik logu (tail) i parsuje linie z tym prefiksem. W modach do TF2 ten trik działał.
- Logowanie: `debugPrint(x)` (wypisuje też tabele) i `log.message/warning/error`.

## 4a. Potwierdzone w grze (2026-10-02)

- **Plik `.script.lua` musi definiować globalne `function data() return { update = ..., handleEvent = ... } end`.** Styl `return {...}` działa tylko w `.tl`. Biblioteki ładowane przez `ug_require "x.lua"` mogą zwracać moduł przez `return`, bo są cache'owane w `_ug_loadedModules` (`base/init.lua`).
- **Korutyny odpadają.** Funkcje C gry, na pewno `log.message`, wywołane z korutyny rzucają `Value is not a string, but a thread`. Zadania rozłożone na wiele ticków trzeba pisać jako obiekty z kursorem.
- **`getEntitiesWithComponent(LINE)` i `(BASE_EDGE)` zwracają `Cannot loop over this component type`.** Działa dla TOWN, INDUSTRY i TRANSPORT_VEHICLE, bo skrypty gry iterują po nich przez `forEachEntityWithComponent`. Zamienniki:
  - linie: `api.engine.system.lineSystem.getLinesForPlayer(player)`;
  - krawędzie: `api.engine.system.streetSystem.getNode2SegmentMap()` (węzeł → segmenty). Są też warianty `getNode2StreetEdgeMap` i `getNode2TrackEdgeMap`.
- **`update()` game scriptu jest wołane z wielu wątków symulacji (`Sim Pool 1..N`), a każdy ma osobny stan Lua.** Zmienne modułu (`local` na poziomie pliku) nie są współdzielone: w jednym uruchomieniu było 13 niezależnych kopii. Jedyny wspólny stan to `state:get()` / `state:set()`. Każdy eksport musi więc kończyć się w jednym wywołaniu `update()`, a harmonogram trzeba trzymać w `state`.
- Log gry: `C:\Program Files (x86)\Steam\userdata\38287428\3493540\local\crash_dump\stdout.txt` (potwierdzone). `debugPrint` w `base/init.lua` używa `log.message`.
- **Wyniki sondy środowiska** (konsola w TF3 się nie otwiera, więc sprawdzał to mod):
  - wątki symulacji (`update`): `io=false`, `os.rename=false`, `os.remove=false`, `os.time=true`, `os.clock=true`, `app=false`;
  - wątek GUI (`guiUpdate`, „Main Thread”): to samo, ale `app=true` i **`app.saveUserdata` działa**. Zapisuje `local\<dir>\<name>.lua` w postaci `function data()\r\nreturn { \r\n\t\tjson = "...",\r\n\t}\r\nend`, czyli stringi jako escapowane literały Lua, z zachowanym UTF-8.
  - Wniosek: eksport robi `guiUpdate` i zapisuje przez `app.saveUserdata`. Mod używa tego od formatu v4.
- **Rewizje krawędzi (`getRevision`) zmieniają się ciągle**, nawet gdy sieć się nie zmienia. Sygnatura oparta na nich nie wykrywa zmian, więc mod porównuje zamiast tego treść zakodowanej sieci.
- **Pomiar sieci:** 8698 krawędzi daje ok. 1 MB JSON. Skan trwa ok. 20 ms, kodowanie 150–200 ms, więc całości nie można robić w jednym ticku. Mod rozkłada to na klatki GUI po 2 ms.
- **(Historyczne) Zapis plików przez `io` jest niedostępny.** Pierwsze uruchomienie zgłosiło `writer mode 'log'`, czyli brakuje `io.open` albo `os.rename`. `os.clock` działa, bo czasy w logu są niezerowe. Do potwierdzenia w konsoli, czego dokładnie brakuje: `print(io, io and io.open, os.rename, os.remove, os.time)`. Na razie dane idą przez log, w liniach `[tf3x] ...` po ≤8000 bajtów, cięte bez przecinania znaków UTF-8.
- Wyniki pierwszego eksportu dynamicznego (mapa z ok. 75 miastami, ok. 130 zakładami przemysłowymi i ok. 400 pojazdami): zbieranie 5 ms, kodowanie 4 ms, wynik 102 KB w 13 kawałkach logu. Działają: nazwy encji, `bbox` (`min`/`max`), `getCalendarDate`, `getPlayersBalance`, nazwy enumów (`RAIL`, `EN_ROUTE`…), nazwy ładunków oraz `cargoNeeds`.

## 5. API silnika: co pasuje do naszego kodu

Ze strony [api.engine](https://wiki.transportfever3.com/script-doc/api/engine.html):

| Używamy w kodzie | Stan w TF3 |
|---|---|
| `api.engine.getEntitiesWithComponent(type, filters)` | ✅ istnieje, zwraca `{Entity}`. Jest też `forEachEntityWithComponent(fn, type)`. Filtry: `requireOwnedByPlayer`, `filterFn`. |
| `api.engine.getComponent(entity, type)` | ✅ |
| `api.engine.ComponentType.X` | ✅ w dokumentacji, ale **skrypty gry używają `api.type.ComponentType.X`**. Bezpieczniej przejść na `api.type`. |
| `api.engine.util.getWorld()`, `getPlayer()` | ✅. Jest też `getEntityName(e)`, które może zastąpić komponent NAME. |
| `GAME_TIME.gameTime` | ✅ (int). Są też `tickCount` i `dates`. |
| `BASE_EDGE.position0/1, tangent0/1, node0/1` | ✅ **są w TF3** (w TF2 ich nie było). Do tego `distance`, `roadType`, `roadTemplate`. |
| `BASE_EDGE.type` | ⚠️ typ `BaseEdgeType`, prawdopodobnie zwykła krawędź / most / tunel, a nie droga / tor. Komponent `BASE_EDGE_TRACK` **nie istnieje**, jest tylko `BASE_EDGE_STREET`. Drogi rozpoznamy po obecności `BASE_EDGE_STREET`, tory po braku albo po `roadType`. Wymaga sprawdzenia. |
| `BOUNDING_VOLUME.bbox` | ✅ typ `Box3f`, nazwy pól (`min`/`max`?) do sprawdzenia przez `debugPrint`. |
| `TOWN`: noise, pollution, developmentActive, initialLandUseCapacities, cargoNeeds | ✅ wszystkie istnieją |
| `INDUSTRY`: level, maxLevel, upgradeProgress, manualDevelopment, construction, stockList | ✅ |
| `STOCK_LIST`: rules (input, output, capacity, booster), stocks (cargoType, capacity), modifiers.productivity | ✅ |
| `TRANSPORT_VEHICLE`: line, stopIndex, userStopped, carrier | ✅. Do tego state, loadState, closestTown, sectionTimes… |
| `LINE.stops[].stationGroup/station/terminal` | ✅ |
| `COLOR.color`, `PLAYER_OWNED.player`, `NAME.name` | ✅ |

### Przydatne rzeczy, których jeszcze nie używamy

- **Czas:** `GAME_SPEED.millisPerDay` (komponent na świecie) i `api.util.getDefaultDayDuration()`. Długość dnia **zależy od ustawień prędkości kalendarza**, więc `cfg.gameDay` nie powinno być stałą, tylko wartością odczytywaną z gry.
- **Wykrywanie zmian:** `api.engine.getRevision(entity)` zwraca numer rewizji encji. To lepsze niż liczenie krawędzi (patrz known issues #7).
- **Data w grze:** `api.engine.util.getCalendarDate(gameTime)` i `getYear()`, przydatne do wyświetlania na stronie.
- **Statystyki gotowe w grze** (`api.engine.util.*`):
  - `finance.getPlayersBalance(player)`, `finance.calculateEarnings(player)`
  - `stock.getCargoShippedPerYear(stockList)`, `getCargoDeliveredPerYear(...)`
  - `line.getLineCapacityUsages(line)`, `line.getLineProblems()`
  - `industry.getIndustryProductivityInfo(e)`
  - `headquarters.getTransportedData()`, `getCompaniesValue()`
  - `logbook.getLogValueMostRecent(e, name)`
- **Systemy** (`api.engine.system.*`): `getLineVehicles(line)`, `getLinesForPlayer(player)`, `getStations(town)`, `getNodeTrackSegments(node)` / `getNodeStreetSegments(node)` (to rozstrzyga, czy krawędź jest torem, czy drogą).
- **Nazwy ładunków:** `api.res.cargoTypeRep.get(id).name` (używane w `landmarks_notification.script.tl`).

## 6. Lista kontrolna do konsoli (zastępuje tę z `lua_mod/README.md`)

```lua
print(io, os, io and io.open, os and os.rename, os and os.clock)                  -- 1. czy mamy I/O
local f = io.open("C:/TF3Export/test.txt", "w"); print(f); if f then f:write("x"); f:close() end
print(#api.engine.getEntitiesWithComponent(api.type.ComponentType.TOWN, {}))        -- 2. lista encji
local t = api.engine.getEntitiesWithComponent(api.type.ComponentType.TOWN, {})[1]
debugPrint(api.engine.getComponent(t, api.type.ComponentType.BOUNDING_VOLUME))       -- 3. bbox
debugPrint(api.engine.getComponent(api.engine.util.getWorld(), api.type.ComponentType.GAME_SPEED))  -- 4. millisPerDay
debugPrint(api.engine.getComponent(api.engine.util.getWorld(), api.type.ComponentType.GAME_TIME))
local e = api.engine.getEntitiesWithComponent(api.type.ComponentType.BASE_EDGE, {})[1]
debugPrint(api.engine.getComponent(e, api.type.ComponentType.BASE_EDGE))             -- 5. type / roadType
print(api.engine.getRevision(e))                                                    -- 6. rewizje
```

Pozostaje też sprawdzić, czy `#lista` i indeksowanie od 1 działają na wynikach (pkt 4 starej listy). Dokumentacja typuje je jako zwykłe tablice `{Entity}` i osobne `*_native: Vector<...>`, więc **prawdopodobnie** dostajemy zwykłe tabele Lua.
