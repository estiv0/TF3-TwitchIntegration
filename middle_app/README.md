# Tf3Bridge: middle app

Aplikacja okienkowa w .NET 9 (WinForms), która odbiera dane z moda `estiv0_tf3export` i publikuje je jako gotowe pliki JSON dla strony.

```
TF3 (mod) ──app.saveUserdata──▶ ...\3493540\local\tf3export\*.lua
                                      │  Tf3Bridge (co 500 ms)
                                      ▼
                               output\latest\dynamic.json   najnowszy snapshot dynamiczny
                               output\latest\network.json   pełna sieć (zrzut + złożone różnice)
                               output\latest\meta.json      session, seq, data gry, liczba krawędzi
```

Zapis idzie zawsze lokalnie (`LocalFolderPublisher`), a opcjonalnie także do Azure Blob Storage w tym samym układzie `latest/*` (patrz „Wysyłka do Azure”).

## Uruchomienie

Tf3Bridge to mała aplikacja okienkowa (WinForms). Okno pokazuje stan (działa / zatrzymany), czas ostatniej publikacji, foldery wejścia i wyjścia oraz log. Most rusza od razu po starcie.

- **Zatrzymaj / Uruchom** wstrzymuje i wznawia przetwarzanie bez zamykania aplikacji.
- **Minimalizacja** chowa okno do zasobnika (zielona kropka = działa). Dwuklik na ikonie przywraca okno, menu ikony ma „Zamknij”.
- **Zamknięcie okna** kończy bieżący przebieg i wyłącza aplikację.
- Może działać tylko jedna instancja; druga pokaże komunikat i się zakończy.

Gotowy exe ze skrótem na pulpicie:

```powershell
middle_app\publish.ps1
```

Skrypt buduje `middle_app\dist\Tf3Bridge.exe` (folder `dist\` jest w `.gitignore`) i tworzy skrót `Tf3Bridge` na pulpicie (`-NoShortcut` pomija skrót). Przed ponowną publikacją zamknij działającą aplikację.

Exe jest self-contained single-file (win-x64, ok. 55 MB): nie wymaga zainstalowanego .NET, a obok leży tylko `appsettings.json`. Dla kogoś innego wystarczy przekazać `Tf3Bridge.exe` + `appsettings.json`. Folder moda Bridge znajdzie sam (patrz `InputDir`), a poza repozytorium publikuje do `output\` obok exe.

Z kodu: `dotnet run` w `middle_app/src/Tf3Bridge` albo `Tf3Bridge.sln` w Riderze. SDK leży w `C:\Users\kinek\.dotnet`. Jeśli `dotnet` nie jest w `PATH`, użyj `C:\Users\kinek\.dotnet\dotnet.exe`.

Testy:

```bash
cd middle_app
dotnet test
```

## Konfiguracja (`src/Tf3Bridge/appsettings.json`, sekcja `Bridge`)

| Klucz | Domyślnie | Opis |
|---|---|---|
| `InputDir` | puste: wykrywane ze Steam (`SteamLocator`): folder Steam z rejestru, a w nim `userdata\<konto>\3493540\local\tf3export` zalogowanego konta; bez zalogowanego Steam konto, które ostatnio grało w TF3 z modem | folder, do którego pisze mod |
| `OutputDir` | puste: `web\data` repozytorium, w którym leży exe (szukane w górę od folderu exe), czyli tam, skąd czyta lokalna strona; poza repozytorium `output\` obok exe. Ścieżka względna liczy się od folderu exe | gdzie publikować `latest/*.json` |
| `PollIntervalMs` | 500 | co ile sprawdzać folder |
| `IncompleteFileTimeoutSec` | 30 | plik, którego po tym czasie nadal nie da się odczytać, trafia do `InputDir\failed` |
| `KeepProcessedFiles` | false | zamiast kasować, przenosi pliki do `InputDir\processed` (debug) |
| `IndentOutput` | false | czytelny JSON na wyjściu (debug) |

Każdy klucz można nadpisać z linii poleceń, np. `Tf3Bridge.exe --Bridge:OutputDir=D:\tf3out` (w polu „Element docelowy” skrótu) albo `dotnet run -- --Bridge:OutputDir=D:\tf3out`.

## Wysyłka do Azure

Zapis zawsze idzie lokalnie (`web\data`), a jeśli w oknie włączono Azure, `AzureMirror` dosyła zmienione pliki do kontenera Blob Storage, z którego czyta strona.

**Pierwsza konfiguracja:**

1. Zainstaluj Azure CLI: `winget install Microsoft.AzureCLI` (potem nowe okno PowerShell).
2. Załóż zasoby: `.\infra\azure-setup.ps1 -Name <nazwa>`. Nazwa konta: 3–24 małe litery i cyfry, unikalna w Azure, bo jest w adresie (`https://<nazwa>.blob.core.windows.net`). Skrypt zakłada grupę `tf3-live` w `polandcentral`, konto Storage (Standard_LRS), publiczny kontener `live`, CORS i wypisuje adres kontenera oraz token SAS (ważny rok, prawa tylko create + write).
3. W Tf3Bridge kliknij **Azure…**, wklej adres i token, **Testuj połączenie**, **Zapisz**.
4. Na stronie ustaw `dataBaseUrl` w `web/config.js` na `https://<nazwa>.blob.core.windows.net/live/latest/`. Do testu bez zmiany pliku: `http://localhost:5080/?data=https://<nazwa>.blob.core.windows.net/live/latest/`.

Nowy token (np. gdy stary wygasa): `.\infra\azure-setup.ps1 -Name <nazwa> -SasOnly`.

**Jak działa wysyłka:**

- Token SAS jest w `%LOCALAPPDATA%\Tf3Bridge\azure.json`, zaszyfrowany DPAPI dla bieżącego konta Windows. Nie trafia do repo ani do `appsettings.json`.
- Po każdym przebiegu wysyłane są pliki zmienione lokalnie. Po włączeniu Azure (albo starcie aplikacji) idzie wszystko, co już jest w `latest\`.
- JSON jest gzipowany (`Content-Encoding: gzip`), wszystko ma `Cache-Control: no-cache`.
- `meta.json` idzie zawsze na końcu i tylko gdy reszta się udała, bo strona po zmianie seq w meta od razu pobiera pozostałe pliki.
- Błąd (brak sieci, wygasły token) nie zatrzymuje lokalnego zapisu. Pliki zostają w kolejce i są dosyłane co 10 s; stan widać w oknie („Azure: błąd: …”).

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
    Program.cs                    rejestracja usług, start okna
    BridgeOptions.cs              konfiguracja
    BridgeRunner.cs               pętla co PollIntervalMs (start/stop z okna): przebieg + wysyłka do Azure
    Ui/MainForm.cs                okno: stan, log, zasobnik
    Ui/AzureDialog.cs             ustawienia Azure z testem połączenia
    Ui/UiLogProvider.cs           logi do okna
    Cloud/AzureSettings.cs        adres kontenera + SAS, zapis zaszyfrowany (DPAPI)
    Cloud/AzureMirror.cs          kolejka zmienionych plików i upload do Blob Storage
    Cloud/MirroringPublisher.cs   zapis lokalny + oznaczenie pliku do wysyłki
    Input/LuaUserdataReader.cs    wyciąganie JSON z plików .lua
    Input/ExportFile.cs           parsowanie nazw plików
    Processing/ExportProcessor.cs jeden przebieg: sesje, sieć, dynamic, publikacja
    Processing/NetworkState.cs    składanie sieci z zrzutu i różnic
    Processing/JsonRead.cs        bezpieczne odczyty liczb i stringów z JsonNode
    Output/IPublisher.cs          interfejs celu publikacji
    Output/LocalFolderPublisher.cs
  tests/Tf3Bridge.Tests/          xUnit: reader, składanie sieci, przebieg procesora, ustawienia Azure
```

Skrypt zakładający zasoby w Azure: [../infra/azure-setup.ps1](../infra/azure-setup.ps1).

## Następne kroki

- Hosting strony w Azure Static Web Apps (plan Free) i ustawienie tam `dataBaseUrl`.
- Strona jest osobnym projektem w [../web/](../web/README.md). Lokalnie czyta to, co Tf3Bridge publikuje do `web/data/latest/`.
