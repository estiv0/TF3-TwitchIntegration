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
- **`dynamic`**: `{ v, kind, session, seq, gameTime, date{year,month,day}, player{id,name,balance}, company, towns[], industries[], stations[], lines[], vehicles[] }`
  - `company` (liczone raz na miesiąc gry): `finance` = tabela z okna Finanse (`computeFinanceTable`, `cfg.financeYears` lat): `{ header[], rows[{ carrier, type, maintenance, construction, values[] }], total[], balance[], interest[], loan[] }`, gdzie `type` itd. to nazwy enumów `JournalEntry` (`INCOME`, `MAINTENANCE`/`VEHICLE`…), a `values[i]` to kwota w roku `header[i]`; `history` = serie z logbooka świata `companyBalance`, `companyTotalValue`, `townResidentialCount` jako `[[rok z ułamkiem, wartość], …]`, najwyżej `cfg.historyMaxPoints` punktów;
  - `towns[].supply`: zaspokojenie potrzeb `[{ cargo, supply, demand }]` z `townBuildingSystem.getCargoSupplyAndLimit` (`demand` -1 = bez limitu);
  - `industries[]`: `stocks[]` mają `amount` (stan magazynu), `input` i `consumedYear` (zużycie na rok); `producedYear` = ładunek -> produkcja na rok; `year` = `{ produced, consumed, destroyed }` z logbooka zakładu za ostatni rok;
  - ładunki są podawane jako nazwy zasobów, np. `"::/cargos/logs/logs.cargo"`;
  - `carrier` i `state` pojazdów to nazwy enumów (`RAIL`, `EN_ROUTE`…);
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

## Diagnostyka

W logu gry (`crash_dump\stdout.txt`) szukaj linii `[tf3export]`:
- `new session … (writer mode 'userdata'|'log')`: początek sesji;
- `dynamic #N: work … ms in … frames (max … ms/frame), … s wall; vehicles …, positions … (… vehicles), stations …, lines …, line stats … (… lines), towns …, industries …, player …, encode …, write … ms; … B`: czas pracy łącznie i w najgorszej klatce oraz czasy kroków. Krok, który sam przekracza budżet (np. `towns`), to kandydat do podziału na porcje;
- `vehicle positions: … ms for … vehicles (max … ms) - disabled for this session`: pozycje pojazdów wyłączone automatycznie, bo łącznie trwały za długo;
- `network #N (full): … edges (initial, …)` oraz `networkDelta #N: … changed, … removed (build event, …)`;
- `network: no change (periodic|build event, …)`: przebudowa nie znalazła zmian;
- `saveUserdata failed: …`: błąd zapisu, po którym mod przełącza się na log;
- `settings: klucz=wartość, …`: wczytane ustawienia z Tf3Bridge, które różnią się od poprzednich.

## Znane ograniczenia

- Snapshot sieci nie jest atomowy. Budowa w trakcie zbierania może dać mieszany stan.
- Eksport dynamiczny jest rozłożony na klatki (budżet `cfg.dynamicBudgetMs`), więc snapshot powstaje przez kilka–kilkanaście klatek, a nie w jednej. Pętle (pozycje pojazdów, statystyki linii, kodowanie JSON) są dzielone w dowolnym miejscu. Pojedynczy kolektor (pojazdy, stacje, linie, miasta, przemysł) idzie w całości, więc najdłuższy z nich wyznacza najgorszą klatkę. Przed tą zmianą cały eksport zajmował 30–45 ms w jednej klatce.
- Pozycje pojazdów (`api.engine.util.vehicle.getPosition` / `getSpeed`) są w każdym eksporcie dynamicznym, nie na żywo. Wyłącznik: „Pojazdy” w ustawieniach moda w Tf3Bridge; wyłącza całe zbieranie pojazdów, a JSON nie ma wtedy klucza `vehicles` i `lines[].vehicleCount` (strona pokazuje „–”). Mod sam je wyłącza do końca sesji, gdy średnio z 3 eksportów trwają łącznie dłużej niż `cfg.vehiclePositionsMaxMs` (30 ms). Strona wtedy zgaduje położenie z numeru przystanku.
