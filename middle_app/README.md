# Tf3Bridge: middle app

Aplikacja w .NET 9 (Worker Service), która odbiera dane z moda `estiv0_tf3export` i publikuje je jako gotowe pliki JSON dla strony.

```
TF3 (mod) ──app.saveUserdata──▶ ...\3493540\local\tf3export\*.lua
                                      │  Tf3Bridge (co 500 ms)
                                      ▼
                               output\latest\dynamic.json   najnowszy snapshot dynamiczny
                               output\latest\network.json   pełna sieć (zrzut + złożone różnice)
                               output\latest\meta.json      session, seq, data gry, liczba krawędzi
```

Na razie zapis idzie lokalnie (`LocalFolderPublisher`). Układ `latest/*.json` jest taki sam, jaki trafi do Azure Blob Storage, więc wysyłka do Azure będzie po prostu drugą implementacją `IPublisher`.

## Uruchomienie

```bash
cd middle_app/src/Tf3Bridge
dotnet run
```

Można też otworzyć `Tf3Bridge.sln` w Riderze. SDK leży w `C:\Users\kinek\.dotnet`. Jeśli `dotnet` nie jest w `PATH`, użyj `C:\Users\kinek\.dotnet\dotnet.exe`.

Testy:

```bash
cd middle_app
dotnet test
```

## Konfiguracja (`src/Tf3Bridge/appsettings.json`, sekcja `Bridge`)

| Klucz | Domyślnie | Opis |
|---|---|---|
| `InputDir` | `...\3493540\local\tf3export` | folder, do którego pisze mod |
| `OutputDir` | `output` (względem katalogu uruchomienia). W Development (`appsettings.Development.json`): `../../../web/data`, czyli tam, skąd czyta lokalna strona | gdzie publikować `latest/*.json` |
| `PollIntervalMs` | 500 | co ile sprawdzać folder |
| `IncompleteFileTimeoutSec` | 30 | plik, którego po tym czasie nadal nie da się odczytać, trafia do `InputDir\failed` |
| `KeepProcessedFiles` | false | zamiast kasować, przenosi pliki do `InputDir\processed` (debug) |
| `IndentOutput` | false | czytelny JSON na wyjściu (debug) |

Każdy klucz można nadpisać z linii poleceń, np. `dotnet run -- --Bridge:OutputDir=D:\tf3out`.

## Co robi jeden przebieg (`ExportProcessor`)

1. Wczytuje pliki `<kind>_<session>_<seq>.lua`. Inne pliki, np. `bench_*`, pomija.
2. **Sesja** to ta z najnowszym plikiem. Pliki starszych sesji kasuje, bo pochodzą z poprzedniego uruchomienia gry albo wczytanego save'a.
3. **Sieć:** stosuje `network` (pełny zrzut) i `networkDelta` w kolejności `seq`. Różnica bez zrzutu tej sesji jest pomijana z ostrzeżeniem. Różnicę po luce (brakujący `seq`) stosuje mimo to, bo to upsert i usunięcia, i loguje ostrzeżenie.
4. **Dynamic:** publikuje tylko najnowszy snapshot, starsze kasuje.
5. Publikuje zmienione pliki `latest/*.json` atomowo (plik tymczasowy, potem zmiana nazwy) i kasuje przetworzone wejście.

Plik, którego nie da się jeszcze odczytać (brak końcowego `end`, czyli gra wciąż go zapisuje), zostaje na następny przebieg. Kolejne różnice sieci czekają na niego, żeby zachować kolejność.

## Format plików z gry

`app.saveUserdata` zapisuje JSON jako literał stringa Lua:

```lua
function data()
return { 
		json = "{\"v\":5,\"kind\":\"dynamic\",...}",
	}
end
```

`LuaUserdataReader` wycina literał i odwraca escapowanie Lua (`\"`, `\\`, `\n`, `\ddd`, `\xXX`…). Pracuje na bajtach, więc UTF-8 (polskie nazwy) przechodzi bez zmian. Opis pól JSON jest w [../lua_mod/README.md](../lua_mod/README.md).

## Struktura

```
middle_app/
  Tf3Bridge.sln
  src/Tf3Bridge/
    Program.cs                    rejestracja usług
    BridgeOptions.cs              konfiguracja
    BridgeWorker.cs               pętla co PollIntervalMs
    Input/LuaUserdataReader.cs    wyciąganie JSON z plików .lua
    Input/ExportFile.cs           parsowanie nazw plików
    Processing/ExportProcessor.cs jeden przebieg: sesje, sieć, dynamic, publikacja
    Processing/NetworkState.cs    składanie sieci z zrzutu i różnic
    Processing/JsonRead.cs        bezpieczne odczyty liczb i stringów z JsonNode
    Output/IPublisher.cs          interfejs celu publikacji
    Output/LocalFolderPublisher.cs
  tests/Tf3Bridge.Tests/          xUnit: reader, składanie sieci, przebieg procesora
```

## Następne kroki

- `AzureBlobPublisher`: gzip, `Content-Encoding`, `Cache-Control`, token SAS (patrz [../docs/architecture.md](../docs/architecture.md)).
- Strona jest osobnym projektem w [../web/](../web/README.md). Lokalnie czyta to, co Tf3Bridge publikuje do `web/data/latest/`.
