# TF3 Data Export (`estiv0_tf3export`)

Mod do Transport Fever 3, który eksportuje dane gry jako JSON:
- **dane dynamiczne** (miasta, przemysł, linie, pojazdy, gracz, data): co 2 dni gry, ale najwyżej raz na 10 s czasu rzeczywistego;
- **sieć dróg i torów**: pełny zrzut raz na sesję, a potem tylko **różnice** (`networkDelta`) po każdej budowie lub wyburzeniu, zarówno gracza, jak i miasta. Mod nasłuchuje zdarzeń `onPreBuildProposal`/`onPostBuildProposal`, ale reaguje tylko na te, które dotykają dróg, torów albo stacji z krawędziami. Czeka 2 s na koniec serii klików i przebudowuje sieć porcjami po 2 ms na klatkę. Dodatkowo co 5 min robi pełne porównanie na wypadek zmian bez zdarzenia.

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
    collectors.lua        kolektory: player, towns, industries, lines, vehicles, edges
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
- **`dynamic`**: `{ v, kind, session, seq, gameTime, date{year,month,day}, player{id,name,balance}, towns[], industries[], lines[], vehicles[] }`
  - ładunki są podawane jako nazwy zasobów, np. `"::/cargos/logs/logs.cargo"`;
  - `carrier` i `state` pojazdów to nazwy enumów (`RAIL`, `EN_ROUTE`…).
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
- `dynamic #N: collect … ms, encode … ms, write … ms, … B`;
- `network #N (full): … edges (initial, …)` oraz `networkDelta #N: … changed, … removed (build event, …)`;
- `network: no change (periodic|build event, …)`: przebudowa nie znalazła zmian;
- `saveUserdata failed: …`: błąd zapisu, po którym mod przełącza się na log.

## Znane ograniczenia

- Snapshot sieci nie jest atomowy. Budowa w trakcie zbierania może dać mieszany stan.
- Eksport dynamiczny trwa ok. 12 ms w jednej klatce GUI. To jedna zgubiona klatka co 10 s.
- Nie ma eksportu pozycji pojazdów na żywo. To osobny, szybszy strumień do zrobienia później.
