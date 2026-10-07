# TF3 Data Export (`estiv0_tf3export`)

Mod do Transport Fever 3, który eksportuje dane gry jako JSON:
- **dane dynamiczne** (miasta, przemysł, linie, pojazdy z pozycjami, gracz, data): co 2 dni gry, ale najwyżej raz na 10 s czasu rzeczywistego. Zbieranie i kodowanie są rozłożone na klatki, najwyżej `cfg.dynamicBudgetMs` (3 ms) pracy na klatkę;
- **sieć dróg i torów**: pełny zrzut raz na sesję, a potem tylko **różnice** (`networkDelta`) po każdej budowie lub wyburzeniu, zarówno gracza, jak i miasta. Mod nasłuchuje zdarzeń `onPreBuildProposal`/`onPostBuildProposal`, ale reaguje tylko na te, które dotykają dróg, torów albo stacji z krawędziami. Czeka 2 s na koniec serii klików i przebudowuje sieć porcjami po 2 ms na klatkę. Dodatkowo co 5 min robi pełne porównanie na wypadek zmian bez zdarzenia.

Częstotliwość eksportu, pojazdy i teren ustawia się w oknie Tf3Bridge (**Ustawienia moda…**). Bridge zapisuje `local\tf3export_settings\settings.lua`, a mod czyta go przez `app.loadUserdata` co 5 s, bez restartu gry (klucze i zakresy: `SETTINGS` w `tf3export.script.lua`). Bez Bridge obowiązują domyślne wartości z `cfg`.

Na pauzie mod nic nie eksportuje. Szczegóły API i ustalenia z gry opisuje [../docs/tf3-api-findings.md](../docs/tf3-api-findings.md).

## Jak to działa w TF3 (potwierdzone w grze)

- `update()` game scriptu działa w wielu wątkach symulacji, a każdy ma osobny stan Lua. Nie ma tam `io` ani `app`, więc z `update()` da się pisać tylko do logu.
- `guiUpdate()` działa w **jednym** wątku GUI i ma `app.saveUserdata`. **Cały eksport odbywa się w `guiUpdate()`.**

## Struktura

```
lua_mod/                              → w grze: staging_area/estiv0_tf3export (junction)
  mod.json                            definicja techniczna (modId, revision, severity)
  _metadata/modinfo.json              nazwa, opis, autor, tagi w przeglądarce modów
  _content.json                       lista plików z content/ (wzór: mody z gry)
  content/tf3export/
    tf3export.gs.lua      deskryptor game scriptu (update / handleEvent / guiUpdate)
    tf3export.script.lua  główny skrypt: harmonogram, sesja, eksport (globalne data())
    collectors.lua        kolektory: player, towns, industries, stations, lines, vehicles, edges
    json.lua              enkoder JSON (escapowanie, table.concat, null dla NaN)
    util.lua              bezpieczny dostęp do API, enumy -> nazwy, czas gry, log
    writer.lua            zapis: app.saveUserdata albo log gry (zapas)
    terrain.lua           heightmapa (getBaseHeightAt porcjami) + jednorazowy benchmark metod odczytu terenu
    commands.lua          komendy od Tf3Bridge (akcje widzów): local\tf3export_cmd\queue.lua -> api.cmd -> results.lua
    effects.lua           efekty czasowe z nagród (boost miasta/przemysłu, zatrzymanie linii), cofane po X miesiącach gry
    twitch_notification.res.lua / .script.lua   typ powiadomienia w grze „Twitch” (komenda notify)
    icons/twitch*.tga     ikona powiadomienia (maska; generuje infra/make_icon.ps1 z infra/twitch_logo.png)
```

Moduły ładuje się względnie przez `ug_require "util.lua"`. Wszystkie leżą w jednym folderze.

## Instalacja (dev)

Folder repo jest podlinkowany do staging area gry, więc zmiany są widoczne po restarcie gry bez kopiowania:

```powershell
New-Item -ItemType Junction -Path "C:\Program Files (x86)\Steam\userdata\38287428\3493540\local\staging_area\estiv0_tf3export" -Target "C:\Users\kinek\Desktop\TransportFever3_Twitch\lua_mod"
```

Mod trzeba włączyć przy tworzeniu lub wczytywaniu gry.

## Paczka dla innych

```powershell
lua_mod\pack.ps1
```

Skrypt generuje `_content.json` z plików `.lua` w `content\` (po dodaniu nowego modułu wystarczy go uruchomić; `-ContentOnly` tylko odświeża listę) i buduje `lua_mod\dist\estiv0_tf3export.zip`. Zip zawiera folder `estiv0_tf3export` z `mod.json`, `_content.json`, `_metadata` i `content`. Rozpakowuje się go do `Steam\userdata\<konto>\3493540\local\staging_area\` i włącza mod w grze (w `mods\` gra go nie pokaże, stamtąd bierze tylko mody z Workshopu).

Zipa potrzeba tylko bez Tf3Bridge: Bridge ma moda wbudowanego i sam instaluje go w `staging_area\` przy starcie (patrz `middle_app/README.md`).

## Pliki wyjściowe

Folder: `C:\Program Files (x86)\Steam\userdata\38287428\3493540\local\tf3export\`
Nazwa: `<kind>_<session>_<seq>.lua`, np. `dynamic_6abfcd61d091_000001.lua`

Gra zapisuje plik w formacie Lua, a JSON jest w środku jako literał stringa:

```lua
function data()
return { 
		json = "{\"v\":4,\"kind\":\"dynamic\",...}",
	}
end
```

Middle app wyciąga `json = "..."`, odwraca escapowanie Lua (`\"`, `\\`, `\n`, `\r`, `\t`, `\ddd`), wysyła dane i usuwa plik. Nie wiadomo, czy zapis jest atomowy, więc gdy plik jest niekompletny (brak końcowego `end`), middle app ponawia odczyt.

- **`session`**: losowe ID wątku GUI. Nowe powstaje przy starcie gry i przy cofnięciu czasu gry, czyli po wczytaniu starszego save'a. `seq` liczy się od 1 w każdej sesji, osobno dla `dynamic` i `network`.
- **`dynamic`**: `{ v, kind, session, seq, gameTime, date{year,month,day}, player{id,name,balance}, company, models, towns[], industries[], stations[], lines[], vehicles[] }`
  - `company` (liczone raz na miesiąc gry): `finance` = tabela z okna Finanse (`computeFinanceTable`, `cfg.financeYears` lat): `{ header[], rows[{ carrier, type, maintenance, construction, values[] }], total[], balance[], interest[], loan[] }`, gdzie `type` itd. to nazwy enumów `JournalEntry` (`INCOME`, `MAINTENANCE`/`VEHICLE`…), a `values[i]` to kwota w roku `header[i]`; `history` = serie z logbooka świata `companyBalance`, `companyTotalValue`, `townResidentialCount`, `townCommercialCount`, `townIndustrialCount` (pojemność dzielnic wszystkich miast) jako `[[rok z ułamkiem, wartość], …]`, najwyżej `cfg.historyMaxPoints` punktów;
  - `towns[].supply`: zaspokojenie potrzeb `[{ cargo, supply, demand }]` z `townBuildingSystem.getCargoSupplyAndLimit` (`demand` -1 = bez limitu);
  - `industries[]`: `stocks[]` mają `amount` (stan magazynu), `input` i `consumedYear` (zużycie na rok); `producedYear` = ładunek -> produkcja na rok; `year` = `{ produced, consumed, destroyed }` z logbooka zakładu za ostatni rok;
  - ładunki są podawane jako nazwy zasobów, np. `"::/cargos/logs/logs.cargo"`;
  - `carrier` i `state` pojazdów to nazwy enumów (`RAIL`, `EN_ROUTE`…);
  - szczegóły pojazdu (`collectors.vehicleDetails`, wyłącznik „Szczegóły pojazdów” w ustawieniach moda): `model` = id modelu czoła składu (w `models`: `{ "id": { name, icon } }`; `name` z `api.res.modelRep` albo nazwa zespołu trakcyjnego z `muFileNames`, `icon` = nazwa pliku `.mdl` bez rozszerzenia, obrazek strony `web/img/vehicles/<icon>.png` z `tools/extract_vehicle_icons.py`), `parts` = liczba części składu (tylko gdy > 1), `age` = wiek najstarszej części w latach kalendarza gry, `wear` = wiek / żywotność tej części (≥ 1 = po żywotności, jak procent w oknie linii), `maint` = średni stan techniczny części 0..1 (jak `vehicle_util.getAvgMaintenanceState`), `cap` = pojemność `{ [ładunek] = liczba }` z `config.capacities` (indeks - 1 = CargoTypeId, jak `gui/main/cargo_util.tl`), `load` = ile pojazd teraz wiezie, te same klucze (`simEntityAtVehicleSystem.getVehicleSimEntitiesCountForCargoType`, jedno wywołanie na ładunek z pojemnością). Bez szczegółów nie ma klucza `models` ani tych pól. **Jeszcze niesprawdzone w grze** (zwłaszcza nazwy modeli);
  - `stations[]`: jeden wiersz na konstrukcję stacji `{ id, kind, industry, carriers[], name, file, pos, owner, passengers, cargo, groups[], stations[] }`, gdzie `kind` to `RAIL`, `ROAD`, `WATER`, `AIR` (rzadko `TRAM`, `OTHER` = nierozpoznana stacja przemysłu), a `industry` to id zakładu, gdy stacja jest jego częścią, wyznaczany ze ścieżki konstrukcji, a dla stacji z modów z `carriers` (nośniki całej grupy stacji). Przystanki (bez konstrukcji, `small_stops`, `underground_station`) są pomijane;
  - przystanek linii (`lines[].stops[]`) ma `entity` = encja stacji, którą można dopasować do `stations[].stations`;
  - `carrier` linii bierze się z jej pojazdów, a gdy linia nie ma pojazdów, z rodzaju jej stacji.
- **`network`**: `{ v, kind, session, seq, gameTime, fields[], edges[][] }`. Każda krawędź to tablica wartości w kolejności z `fields`:
  - `id`, `node0`, `node1`
  - pozycje i tangenty
  - `road`: `STREET` lub `TRACK`
  - `type`: `NORMAL`, `BRIDGE` lub `TUNNEL`
  - `owner`: 0 oznacza miasto lub świat
- **`networkDelta`**: `{ v, kind, session, seq, base, gameTime, fields[], changed[][], removed[] }`
  - `changed` to dodane lub zmienione krawędzie, w formacie wierszy jak w `edges`;
  - `removed` to ID usuniętych krawędzi;
  - `base` to `seq` wersji, do której stosuje się różnicę. `network` i `networkDelta` mają wspólny licznik `seq`, więc `base = seq - 1`.

  Odbiorca stosuje różnice po kolei. Jeśli brakuje któregoś `seq`, czeka na nowy pełny zrzut, który powstaje przy następnej sesji.

- **`terrain`**: heightmapa do tła mapy (`terrain.lua`). `{ v, kind, session, seq, gameTime, method, nx, ny, step, x0, y0, zMin, zMax, zScale, waterLevel, encoding, data }`
  - próbka `(i, j)` leży w świecie w `(x0 + i*step, y0 + j*step)`; wiersze od `j = 0` (południe, najmniejsze y), w wierszu `i` rosnąco;
  - `encoding = "b64x3"`: każda próbka to 3 znaki base64 (18 bitów, najstarsze pierwsze), `h = zMin + q * zScale` (`zScale = 0.1` m);
  - `waterLevel` z komponentu `TERRAIN` świata, `null`, jeśli nie udało się go odczytać.

  Powstaje raz na sesję (po pierwszym zrzucie sieci) i ponownie, gdy zmieni się podpis `getTerrainEntityRevisions()` (sprawdzany co `terrainCheckEveryWallSec`). Domyślnie krok 20 m, najwyżej 2048 próbek na bok: mapa 20×41 km daje krok 21 m i 976×1951 próbek (ok. 5,7 MB, ok. 4 s CPU w porcjach po 4 ms, czyli ~18 s w tle). Middle app robi z tego `latest/terrain.png` (cieniowana rzeźba) i `latest/terrain.json` (zakres w świecie).

Typowe rozmiary to ok. 120 KB (`dynamic`) i ok. 1 MB (`network`, ok. 8700 krawędzi).

### Zapas: log gry

Gdy `app.saveUserdata` jest niedostępne, `writer.lua` pisze do `...\3493540\local\crash_dump\stdout.txt` w kawałkach do 8000 bajtów, cięte bez przecinania znaków UTF-8:

```
[tf3x] <kind> <session> <seq> <part>/<total> <fragment JSON-a>
```

## Komendy od Tf3Bridge (akcje widzów)

`commands.lua` co sekundę (`commandsEveryWallSec`) czyta w wątku GUI `local\tf3export_cmd\queue.lua`, który pisze Tf3Bridge:

```lua
function data()
return {
	commands = {
		{ id = "3f2a9c1d7b4e", kind = "setName", created = 1759590000, entity = 1234, name = "Kraków | widz" },
	},
}
end
```

- Rodzaje komend:
  - `setName { entity, name }`: zmiana nazwy (`makeEntitySetNameCmd`);
  - `adopt { entity, owner }`: „Nazwa” → „Nazwa | owner”, tylko gdy obiekt nie ma właściciela (sprawdzane w chwili wykonania, inaczej `invalid`, a Tf3Bridge zwraca punkty);
  - `effect { effect, entity, months, factor }`: efekt czasowy z `effects.lua`, opis niżej;
  - `createTown { x, y, name, owner }` i `createIndustry { x, y, file, name, owner }`: nowe miasto albo zakład „name | owner” (`create.lua`, opis niżej); w wyniku `entity` = id nowego obiektu;
  - `notify { title, text, entity?, dismissMs? }`: powiadomienie w grze z ikoną Twitcha (zdarzenie `Notifications` → `add`, typ `twitch_notification`); kliknięcie przenosi kamerę do `entity` ([../docs/custom-notifications.md](../docs/custom-notifications.md)).
- Wynik trafia do `local\tf3export_cmd\results.lua` (`app.saveUserdata`): `json` z listą `{ id, status, error }` (status `ok`, `failed`, `invalid`, `expired`) i `done` z identyfikatorami, z których mod po restarcie gry odtwarza, co już wykonał.
- Komendy starsze niż 300 s są pomijane (`expired`).
- Gra wysyła komendy ze swoich skryptów w wątkach symulacji; z wątku GUI to **jeszcze niesprawdzone**. Dlatego mod nie ufa tylko callbackowi `sendCommand`: po wysłaniu przez 15 s sprawdza, czy efekt jest widoczny (dla `setName`: czy encja ma nową nazwę). Log: `command <id>: sent setName`, potem `ok` albo `failed (...)`.

### Nowe miasta i zakłady (`create.lua`)

Tak samo jak edytor mapy gry (`gui/map_editor/map_editor.tl` w `gui.zip`):

- miasto: `api.type.Map.Town` (pozycja, nazwa, `sizeFactors = {1, 1, 1}`, potrzeby ładunkowe najbliższego miasta z `api.engine.terrain.makeMapFromGame`) → `api.engine.mapgen.createTowns` → `makeTownCreateCmd(toAdd)`; gotowe, gdy jest miasto o nowej nazwie;
- zakład: `api.type.Map.Industry` (plik `.con`, losowy kąt, `onWater` dla platformy wiertniczej, łowiska i pogłębiarki) → `api.engine.mapgen.createIndustries` → `api.engine.util.proposal.createIndustryProposal` → `makeWorldBuildProposalCmd(proposal, context, false, false, true)`. Bez `ignoreErrors`: kolizja z torami, drogą albo stacją kończy się błędem (zwrot punktów), a nie zburzeniem infrastruktury streamera. Gra nazywa nowy zakład sama, więc po jego pojawieniu się (nowa encja `INDUSTRY` do 1,5 km od punktu) mod zmienia nazwę na „name | owner”.
- `toRemove` z `mapgen` jest pomijane: budowa nigdy nie usuwa istniejących obiektów.
- Przed wysłaniem: `isValidCoordinate`, woda (`getHeightAt` poniżej `waterLevel`), zajęta nazwa miasta. Błędy (`on water`, `needs water`, `no room ...`) Tf3Bridge tłumaczy na czacie.
- Na wynik czekamy do 45 s (zamiast 15 s).
- **Jeszcze niesprawdzone w grze:** pierwszy test przez **Test akcji…** → symulacja nagrody „Nowe miasto” / „Nowy zakład” z kodem skopiowanym ze strony.

### Efekty czasowe (`effects.lua`)

| Efekt | Co robi | Cofnięcie po `months` miesiącach gry |
|---|---|---|
| `townBoost` | `sizeFactors` miasta × `factor` (`makeTownUpdateSizeCmd`, budynki dorastają stopniowo) | stare `sizeFactors` |
| `industryBoost` | produktywność zakładu × `factor` (`makeStockListSetModifiersCmd`) | produktywność sprzed efektów |
| `industryStrike` | jak wyżej, `factor` 0,01–1 (strajk) | jak wyżej |
| `industryTaxRaid` | jak wyżej na wszystkich zakładach z `entities` („1,2,3”); klucz efektu to `target` (widz-ofiara) | jak wyżej |
| `lineFuelPremium` / `lineFuelBad` | `topSpeedScale` pojazdów linii × `factor` (`makeVehicleSetModifiersCmd`, pozostałe skale bez zmian), także pojazdów dokupionych w trakcie | skala sprzed efektów; pojazd, który opuścił linię, wraca od razu |
| `lineStop` | pojazdy linii zatrzymane jak przyciskiem w grze (`makeVehicleSetStoppedByUserCmd`), także na szlaku | start tych, które zatrzymał efekt |
| `lineHold` | ręczny odjazd (`makeVehicleSetManualDepartureCmd`): pojazdy czekają na przystankach | ręczny odjazd wyłączony + `makeVehicleTryToDepartCmd` |

- `factor` w zakresie rodzaju efektu (boosty 1–5, paliwo premium 1–3, strajk i skarbówka 0,01–1, chrzczone paliwo 0,1–1), `months` 1–60 (miesiąc = 365/12 dni gry). Czas liczy się w czasie gry, więc na pauzie efekt stoi. Miesiące, bo dzień gry trwa tylko 2–3 s.
- Drugi efekt tego samego rodzaju na tym samym celu przedłuża czas (nalot na tego samego widza dokłada też jego nowe zakłady). Siła rośnie tylko wtedy, gdy nowe wykupienie ma mocniejszy mnożnik (dalej od 1, np. po zmianie ustawień).
- **Paliwo to kolejka na linię** (`queue = "fuel"`): paliwo premium i chrzczone na tej samej linii nie działają naraz (nie mnożą się). Inne paliwo kupione w trakcie czeka i zaczyna działać, gdy obecne się skończy (do czatu idzie data końca, log: `effect … queued …`). To samo paliwo przedłuża obecne, a czekające w kolejce przesuwa się o tyle samo.
- **Sterownik wartości** (`Controller` w `effects.lua`): produktywność zakładu i prędkość pojazdu ustawia jeden sterownik na obiekt, z bazą (wartość sprzed efektów) × iloczyn mnożników wszystkich aktywnych efektów. Boost ×2 i strajk ×0,01 na tym samym zakładzie dają ×0,02, a po końcu strajku wraca ×2; bez tego efekty przejmowałyby nawzajem swoje wartości jako nową bazę. Bazy są zapisywane w save'ie (`state.effectCtl`); boosty ze starszych save'ów przenoszą swoją bazę do sterownika przy wczytaniu.
- Skrypt gry `industry_workers` też ustawia produktywność (boost od pracowników). Dlatego co sekundę pilnujemy wartości. Po naszym ustawieniu mod czeka kilka sekund, aż gra je przyjmie, i zapamiętuje faktyczną wartość (gra może mieć własny limit; log: `productivity …: game keeps …`). Dopiero zmiana względem tej wartości oznacza ingerencję innego skryptu: wtedy nowa wartość staje się bazą (log: `productivity …: changed by the game …`). Tak samo dla prędkości (`speed …`). Efekt uznajemy za wykonany, gdy wartość osiągnie cel albo przesunie się od bazy w jego stronę (gra może mieć limit). Cel przyjęty przez grę z opóźnieniem nie staje się nową bazą (inaczej mnożnik naliczałby się drugi raz).
- **Do sprawdzenia w grze:** czy silnik sam nie nadpisuje `topSpeedScale` (premie miast od landmarków) i czy +20% widać na liniach, które ogranicza prędkość toru, a nie pojazdu.
- `lineStop` nie rusza pojazdów, które gracz zatrzymał wcześniej.
- `lineHold`: API nie pokazuje trybu ręcznego odjazdu, więc efekt uznajemy za wykonany po wysłaniu komend.
- Efekt niepotwierdzony w 15 s jest cofany, a Tf3Bridge zwraca punkty.

### Sabotaż hamulców (`wreck` w `commands.lua`)

Gra nie ma wypadków (brak komendy, stanu ani zdarzenia kolizji), więc „wypadek” to zniknięcie pojazdu: losowo od 1 do `share` (domyślnie 50%) pojazdów linii, zawsze co najmniej jeden, jest sprzedawanych (`makeVehicleSellCmd`). Kwotę ze sprzedaży (suma `vehicle.getDepreciatedValue` sprzed sprzedaży) mod zabiera wpisem w księgach (`makeJournalBookAssetCmd`, kategoria `OTHER`, kwota ujemna), żeby streamer nic na tym nie zarobił. Wpis idzie dopiero po potwierdzeniu sprzedaży (callback `true` albo żaden z wybranych pojazdów nie jeździ już na linii), więc odrzucona sprzedaż nic nie kosztuje. Pojazd sprawdzamy po komponencie `TRANSPORT_VEHICLE` i linii, nie samym `entityExists`, bo gra może oddać id sprzedanego pojazdu nowej encji. 15 s na potwierdzenie liczy się tylko wtedy, gdy czas gry płynie, bo na pauzie sprzedaż może wejść dopiero po wznowieniu. Przy `failed` log podaje, ile sprzedanych pojazdów wciąż jest na linii. Wynik: `count` (zniszczone) i `total` (pojazdy linii przed sabotażem). Nie do cofnięcia. **Jeszcze niesprawdzone w grze.**
- **Kto i do kiedy:** komenda `effect` niesie `user` (widz z Twitcha). Wynik komendy w `results.lua` ma `until = { year, month, day }`, czyli koniec efektu w kalendarzu gry; Tf3Bridge podaje tę datę widzowi na czacie. Aktywne efekty trafiają do każdego eksportu dynamicznego jako `effects` (`effects.list()`), a strona pokazuje je przy obiektach.
- **Zapis w save'ie:** wątek GUI nie może pisać stanu game scriptu. Lista efektów idzie więc zdarzeniem `tf3x_effects` (`makeScriptingSendEventCmd`) do `handleEvent` w wątku symulacji, który zapisuje ją w `state.effects`. Po wczytaniu gry mod odtwarza efekty z tego stanu (log: `effects: N active effect(s) restored from the save`).

## Diagnostyka

W logu gry (`crash_dump\stdout.txt`) szukaj linii `[tf3export]`:
- `new session … (writer mode 'userdata'|'log')`: początek sesji;
- `dynamic #N: work … ms in … frames (max … ms/frame), … s wall; vehicles …, positions …, details … (… vehicles), stations …, lines …, line stats … (… lines), towns …, industries …, player …, encode …, write … ms; … B`: czas pracy łącznie i w najgorszej klatce oraz czasy kroków. Krok, który sam przekracza budżet (np. `towns`), to kandydat do podziału na porcje;
- `vehicle positions: … ms for … vehicles (max … ms) - disabled for this session`: pozycje pojazdów wyłączone automatycznie, bo łącznie trwały za długo;
- `network #N (full): … edges (initial, …)` oraz `networkDelta #N: … changed, … removed (build event, …)`;
- `network: no change (periodic|build event, …)`: przebudowa nie znalazła zmian;
- `saveUserdata failed: …`: błąd zapisu, po którym mod przełącza się na log;
- `settings: klucz=wartość, …`: wczytane ustawienia z Tf3Bridge, które różnią się od poprzednich.

## Znane ograniczenia

- Snapshot sieci nie jest atomowy. Budowa w trakcie zbierania może dać mieszany stan.
- Eksport dynamiczny jest rozłożony na klatki (budżet `cfg.dynamicBudgetMs`), więc snapshot powstaje przez kilka–kilkanaście klatek, a nie w jednej. Pętle (pozycje pojazdów, statystyki linii, kodowanie JSON) są dzielone w dowolnym miejscu. Pojedynczy kolektor (pojazdy, stacje, linie, miasta, przemysł) idzie w całości, więc najdłuższy z nich wyznacza najgorszą klatkę. Przed tą zmianą cały eksport zajmował 30–45 ms w jednej klatce.
- Pozycje pojazdów (`api.engine.util.vehicle.getPosition` / `getSpeed`) są w każdym eksporcie dynamicznym, nie na żywo. Wyłącznik: „Pojazdy” w ustawieniach moda w Tf3Bridge; wyłącza całe zbieranie pojazdów, a JSON nie ma wtedy klucza `vehicles` i `lines[].vehicleCount` (strona pokazuje „–”). Mod sam je wyłącza do końca sesji, gdy średnio z 3 eksportów trwają łącznie dłużej niż `cfg.vehiclePositionsMaxMs` (30 ms). Strona wtedy zgaduje położenie z numeru przystanku.
