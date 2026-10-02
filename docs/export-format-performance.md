# Format eksportu a wydajność

**Status:** pomiar wykonany 2026-10-02. **Decyzja: zostaje JSON w Lua** (szczegóły w sekcji „Wyniki”). `cfg.benchmark = false`, a `bench.lua` zostaje do ewentualnych powtórek.

## Gdzie jest koszt (pomiary z 2026-10-02)

| Etap | Sieć (~8,7 tys. krawędzi) | Dynamic |
|---|---|---|
| zbieranie danych z API | ~20 ms | ~5 ms |
| **kodowanie JSON w Lua** | **150–200 ms** | ~6 ms |
| zapis | kilka–kilkanaście ms | ~1 ms |
| rozmiar | ~1 MB | ~120 KB |

Dodatkowo `app.saveUserdata` serializuje nasz JSON jako literał stringa Lua. Każdy `"` zamienia się w `\"`, więc plik rośnie o ~15–20%, a my płacimy za kodowanie dwa razy.

## Propozycje (od największego spodziewanego zysku)

1. **Tabele Lua zamiast JSON-a w modzie.** Mod przekazuje do `app.saveUserdata` zwykłe tabele, a gra serializuje je w C++. Middle app wczytuje plik `.lua` (np. biblioteką MoonSharp dla .NET albo prostym parserem literałów) i zamienia go na JSON. Pliki są zawsze w formie `function data() return {...} end`.
   - Zysk: znika kodowanie w Lua.
   - Do zmierzenia: szybkość serializera gry, rozmiar pliku i formatowanie liczb.
   - Konsekwencja: różnice sieci (`networkDelta`) trzeba liczyć na liczbach, a nie na zakodowanych wierszach. To proste porównanie pól.
2. **Format kolumnowy także dla danych dynamicznych.** Tak jak `fields` + `edges[][]` w sieci. Usuwa powtarzane nazwy kluczy, więc plik jest ok. 2× mniejszy i szybciej się koduje.
3. **Mniejsza precyzja liczb.** Pozycje do 0,1 m (albo całe metry), tangensy i współczynniki do 0,01. Daje mniej znaków i mniej fałszywych różnic.

### Czego nie robić
- **Binarny format (MessagePack/CBOR) w modzie.** Lua 5.2 nie ma `string.pack`, więc składanie bajtów w Lua jest wolne. Do tego `saveUserdata` zapisze bajty jako tekst `\ddd`, czyli plik wyjdzie większy.

## Middle app → strona (osobna decyzja)

Format dla strony nie musi być taki jak format z moda:
- upload do Azure skompresowany (gzip/brotli), bo JSON krawędzi kompresuje się kilkukrotnie;
- geometrię sieci middle app może przepisać do formatu binarnego (np. tablice `Float32`), który przeglądarka czyta bez parsowania. Opłaca się przy dużych mapach.

## Pomiar (`bench.lua`)

Uruchamia się raz na sesję, zaraz po pierwszym pełnym zrzucie sieci. Ta jedna klatka będzie długa. Porównuje na tych samych krawędziach:

| Wariant | Co robi | Plik |
|---|---|---|
| A | `json.encode` w Lua + `saveUserdata({ json = string })` (obecnie) | `local\tf3export\bench_A.lua` |
| B | `saveUserdata({ fields, edges = tabele })` | `bench_B.lua` |
| B2 | jak B, liczby zaokrąglone do 0,1 | `bench_B2.lua` |

Wyniki są w logu, w liniach `[tf3export] bench ...`. Po pomiarze trzeba:
- porównać czasy i rozmiary plików;
- sprawdzić, jak gra formatuje liczby i zagnieżdżone tablice w B;
- usunąć pliki `bench_*`;
- wyłączyć `cfg.benchmark`.

## Wyniki (2026-10-02, 8698 krawędzi, jedna klatka GUI)

| Wariant | Czas | Rozmiar pliku |
|---|---|---|
| zbieranie wierszy z API (wspólne) | 80 ms | – |
| **A** JSON w Lua + `saveUserdata(string)` | 80 ms kodowania + 5 ms zapisu = **85 ms** | **1,08 MB** |
| **B** `saveUserdata(tabela)` | **74 ms** | 1,26 MB |
| **B2** jak B, zaokrąglone do 0,1 | 15 ms zaokrąglania + 70 ms = 85 ms | 1,16 MB |

**Wnioski:**
- Serializer gry **nie jest szybszy**: 74 ms wobec 85 ms, czyli w granicach szumu. Za to plik jest o 16% większy.
- Serializer gry **obcina precyzję** do ok. 6 cyfr znaczących. Przykładowo `11412.62` zapisał jako `11412.6`, więc współrzędne powyżej 10 km tracą dokładność.
- Zbieranie danych z API kosztuje tyle samo co kodowanie (~80 ms). Przy przebudowie rozłożonej na klatki całość zajmuje ok. 280 ms pracy w ok. 3 s.
- **Decyzja: zostajemy przy JSON-ie w Lua** (wariant A). Propozycje 2 (format kolumnowy dla dynamic) i 3 (mniejsza precyzja) zostają jako ewentualne optymalizacje rozmiaru, a nie czasu.

**Uboczne odkrycie:** `onPostBuildProposal` przychodzi co ~3 s także bez zmian w sieci, np. przy budynkach miast. Bez filtra sieć była przebudowywana prawie ciągle: 2 ms na każdą klatkę GUI. Poprawka: `touchesNetwork()` w `tf3export.script.lua` podbija `netVersion` tylko wtedy, gdy propozycja:
- ma `proposal.addedSegments`/`removedSegments`, albo
- usuwa (`onPreBuildProposal`, `toRemove`) lub dodaje (`onPostBuildProposal`, encje wynikowe `param[3]`) konstrukcję z `frozenEdges`.

`networkDelta` działa: po budowie lub wyburzeniu powstają pliki o rozmiarze 260–600 B. Filtr nie był jeszcze testowany w grze.
