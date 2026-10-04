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
infra\publish.ps1
```

Skrypt buduje `middle_app\dist\Tf3Bridge.exe` (folder `dist\` jest w `.gitignore`) i tworzy skrót `Tf3Bridge` na pulpicie (`-NoShortcut` pomija skrót). Przed ponowną publikacją zamknij działającą aplikację.

Exe jest self-contained single-file (win-x64, ok. 55 MB): nie wymaga zainstalowanego .NET, a obok leży tylko `appsettings.json`. Dla kogoś innego wystarczy przekazać `Tf3Bridge.exe` + `appsettings.json`. Folder moda Bridge znajdzie sam (patrz `InputDir`), a poza repozytorium publikuje do `output\` obok exe.

Mod jest wbudowany w exe (`lua_mod\` z chwili budowania, `ModInstaller`). Przy starcie Bridge instaluje go albo aktualizuje w `Steam\userdata\<konto>\3493540\local\staging_area\estiv0_tf3export` (nie `mods\`: stamtąd gra pokazuje tylko mody z Workshopu): zapisuje nowe i zmienione pliki, a usuwa tylko te `.lua` w `content\`, których mod już nie ma. Nowy exe aktualizuje więc też moda. Jeśli gra jest uruchomiona, nowa wersja działa dopiero po ponownym wczytaniu zapisu. Kopii deweloperskiej (junction `staging_area\estiv0_tf3export` → repozytorium) Bridge nie rusza. Test `Embedded_mod_is_complete` pilnuje, żeby `_content.json` zgadzał się z plikami `.lua` (odśwież go przez `lua_mod\pack.ps1 -ContentOnly`).

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

## Ustawienia moda

Przycisk **Ustawienia moda…** w oknie zmienia działanie moda w grze bez edycji plików:

| Pole | Domyślnie | Klucz w modzie |
|---|---|---|
| Eksport danych co … dni gry | 2 (1–30) | `dynamicEveryDays` |
| …ale nie częściej niż co … s | 10 (2–300) | `minDynamicWallSec` |
| Czas pracy na klatkę | 3 ms (1–10) | `dynamicBudgetMs` |
| Pojazdy (lista, pozycje, liczba na liniach) | tak | `vehicles` |
| Eksport terenu | tak | `terrain` |
| Dokładność terenu | 20 m (5–100) | `terrainStepM` |

Mod nie ma dostępu do plików poza `app.loadUserdata`, więc Bridge zapisuje ustawienia jako plik userdata obok folderu eksportu: `local\tf3export_settings\settings.lua` (`ModSettingsStore`, zapis przez plik tymczasowy). Mod czyta go co 5 s, więc zmiany działają bez restartu gry; w logu gry pojawia się `[tf3export] settings: …`. Usunięcie pliku przywraca wartości domyślne.

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

## Twitch (logowanie)

Integracja z Twitchem ([../docs/twitch-channel-points.md](../docs/twitch-channel-points.md)): logowanie streamera, EventSub i nagrody za punkty kanału (opis niżej). Komend z czatu jeszcze nie ma. Grę zmieniają tylko punkty kanału; czat jest wyłącznie informacyjny.

- Aplikacja na dev.twitch.tv jest typu **Public**, bez client secret. `Twitch:ClientId` w `appsettings.json` jest jawny i wspólny dla wszystkich streamerów.
- **Twitch…** → **Zaloguj przez Twitch**: okno pokazuje kod i otwiera `twitch.tv/activate`, a streamer zatwierdza tam dostęp (Device Code Grant Flow). Uprawnienia: `channel:read:redemptions`, `channel:manage:redemptions`, `user:read:chat`, `user:write:chat` (odpowiedzi dla widzów po wykupieniu). Gdy aplikacja potrzebuje nowego uprawnienia, zapisane konto jest zapominane i trzeba zalogować się ponownie.
- Używany jest tylko user access token. App access token nie jest potrzebny: EventSub przez WebSocket i endpointy punktów kanału go nie przyjmują.
- Tokeny są w `%LOCALAPPDATA%\Tf3Bridge\twitch.json`, zaszyfrowane DPAPI. Login, ID i uprawnienia są jawne.
- `TwitchSession` sprawdza token przy starcie i co godzinę (`/oauth2/validate`, Twitch tego wymaga) i odświeża go przed wygaśnięciem. Refresh token public clienta jest jednorazowy i ważny 30 dni, więc po każdym odświeżeniu nowy jest od razu zapisywany. Gdy Twitch go odrzuci (dłuższa przerwa, zmiana hasła, odłączenie aplikacji), konto jest zapominane i trzeba zalogować się ponownie. Przy braku sieci konto zostaje.
- Okno pokazuje też typ kanału. Bez statusu Affiliate/Partner nie da się tworzyć nagród za punkty.

## Twitch EventSub (czat i punkty kanału)

`EventSubClient` łączy się z `wss://eventsub.wss.twitch.tv/ws` tokenem streamera (bez publicznego serwera i webhooków) i subskrybuje:

- `channel.chat.message` (czat kanału): działa na każdym kanale;
- `channel.channel_points_custom_reward_redemption.add` (wykupienia nagród): ma sens tylko na kanale Affiliate/Partner.

Jak działa:
- Subskrypcje są tworzone w Helix (`POST /eventsub/subscriptions`) po powitaniu `session_welcome`. Twitch daje na to 10 s.
- Gdy jedna subskrypcja się nie uda, połączenie zostaje z pozostałymi, a przyczyna jest w logu. Gdy nie uda się żadna, połączenie jest zrywane i ponawiane.
- Keepalive co 30 s. Brak wiadomości przez keepalive + 10 s oznacza zerwane połączenie. Wtedy klient łączy się ponownie z przerwami 1, 2, 5, 10, 30 i 60 s i zakłada subskrypcje od nowa.
- `session_reconnect`: przejście na nowy adres bez ponownych subskrypcji (Twitch je przenosi).
- Zdarzenia są odrzucane jako duplikaty po `message_id`. W logu pojawiają się wykupienia i wiadomości z czatu zaczynające się od `!`.
- Stan widać w oknie w linii „EventSub: …”.

**Test bez Twitcha (Twitch CLI):**

```
twitch event websocket start-server
Tf3Bridge.exe --Twitch:EventSubUrl=ws://127.0.0.1:8080/ws --Twitch:SubscriptionsUrl=http://127.0.0.1:8080/eventsub/subscriptions
twitch event trigger channel.channel_points_custom_reward_redemption.add --transport=websocket
```

W Visual Studio/Riderze jest do tego profil uruchomienia „Tf3Bridge (Twitch CLI)”. Sprawdzone z Twitch CLI 1.1.24:
- wykupienie dochodzi;
- `channel.chat.message` nie jest obsługiwane przez serwer testowy: subskrypcja kończy się błędem 400, a `trigger` odmawia. Czat trzeba testować na prawdziwym kanale.
- `trigger` nie ma opcji portu, więc serwer musi działać na domyślnym 8080.
- Konto Twitch musi być zalogowane, ale serwer testowy nie sprawdza tokenu.

## Nagrody za punkty kanału

Jedyna droga zmian w grze od widzów (`RewardService`). Pięć nagród, każdą da się wyłączyć i zbalansować w oknie **Nagrody…**: tytuł, opis, koszt, cooldown, limity na stream i na widza, czas w miesiącach gry i mnożnik. Czas jest w miesiącach, nie w dniach: dzień gry trwa tylko 2–3 s, więc efekt na kilka dni mijał, zanim było go widać.

| Nagroda | Cel (tekst od widza) | Komenda dla moda |
|---|---|---|
| Kupno miasta | miasto bez właściciela | `adopt`: „Nazwa \| widz”; miasto z właścicielem = zwrot punktów |
| Boost miasta | miasto | `effect townBoost` (miesiące, mnożnik) |
| Boost przemysłu | zakład | `effect industryBoost` (miesiące, mnożnik) |
| Stop linii (od razu) | linia | `effect lineStop` (miesiące) |
| Linia czeka na stacjach | linia | `effect lineHold` (miesiące) |

- **Na Twitchu:** Tf3Bridge sam tworzy nagrody (tylko te może potem zmieniać i rozliczać), zapamiętuje ich ID w `%LOCALAPPDATA%\Tf3Bridge\rewards.json` i aktualizuje je po zapisaniu ustawień. Gdy przez 2 minuty nie ma nowego eksportu z gry, nagrody są pauzowane. Wymaga kanału Affiliate/Partner; bez tego nic nie trafia na Twitcha.
- **Wykupienie:** tekst od widza jest dopasowywany do nazw z ostatniego `dynamic.json` (`TargetResolver`). Wielkość liter, polskie znaki, spacje i „\| właściciel” nie mają znaczenia. Wygrywa dokładne dopasowanie, potem jednoznaczny początek nazwy, potem jednoznaczny fragment.
- **Rozliczenie:** gdy gra potwierdzi, wykupienie dostaje FULFILLED. Gdy celu nie ma, cel jest niejednoznaczny, nagroda jest wyłączona albo gra odmówi lub nie zdąży, wykupienie dostaje CANCELED i Twitch zwraca punkty.
- **Wykupienia z czasu, gdy Tf3Bridge był wyłączony:** świeższe niż 5 minut są wykonywane, starsze zwracane.
- **Odpowiedź na czacie** (`RewardMessages`, `ChatApi`, Helix `POST /chat/messages` jako streamer). Wyłącznik jest w **Nagrody…**. Przykłady:
  - „@widz boost miasta Szczecin (+50%) działa do 12 maja 1922 (data w grze). https://gin.estfosoft.com/#town=225799” Datę końca w kalendarzu gry podaje mod w wyniku komendy (`until`).
  - „@widz miasto Kraków jest teraz Twoje! https://gin.estfosoft.com/#town=…”
  - Link prowadzi do obiektu na stronie (`#town=`, `#industry=`, `#line=`). Adres strony: `Twitch:SiteUrl` w `appsettings.json`; pusty = bez linków.
  - Przy zwrocie: „@widz boost miasta: nie znaleziono „gdańsk”. Punkty wróciły do Ciebie.”
- **Efekty na stronie:** mod eksportuje aktywne efekty w `dynamic.json` (`effects`), a strona pokazuje je przy mieście, zakładzie i linii ([../web/README.md](../web/README.md#efekty-od-widzów)).
- **Test bez Twitcha:** **Test akcji…** → „Nagroda za punkty kanału (symulacja)”. Ta sama droga co prawdziwe wykupienie, z wynikiem, informacją, czy byłby zwrot, i treścią odpowiedzi na czacie. Po zaznaczeniu „Wyślij też odpowiedź na czat” odpowiedź naprawdę trafia na czat kanału, więc działa to także bez statusu Affiliate. W trybie Twitch CLI `twitch event trigger channel.channel_points_custom_reward_redemption.add --transport=websocket -i AdoptTown` wskazuje nagrodę po nazwie rodzaju, ale CLI zawsze wysyła tekst „Test Input From CLI”, więc kończy się zwrotem.

## Komendy dla moda (akcje widzów)

Droga zmian w grze od widzów. Źródła (`CommandSource`): punkty kanału i ręczny test w oknie. Czat celowo nie może tu nic wysłać: komendy z czatu są tylko informacyjne, bo streamer nie ma nad nimi kontroli i mogłyby popsuć balans.

- `ModCommandQueue` pisze kolejkę do `local\tf3export_cmd\queue.lua` (format `app.saveUserdata`, zapis przez plik tymczasowy). Mod czyta ją co sekundę i zapisuje wyniki do `results.lua` obok. Szczegóły formatu: [../lua_mod/README.md](../lua_mod/README.md#komendy-od-tf3bridge-akcje-widzów).
- Komenda zostaje w kolejce, dopóki mod nie zgłosi wyniku. Mod pomija identyfikatory, które już obsłużył, więc przepisywanie kolejki niczego nie wykona dwa razy. Po 320 s bez odpowiedzi (gra wyłączona albo bez moda) komenda jest oznaczana jako `expired`.
- Tekst od widzów trafia do pliku Lua tylko jako escapowany literał (`ModCommandLua.Str`), a nazwy argumentów muszą być identyfikatorami.
- Nazwy obiektów mają postać `Nazwa | Właściciel`, tak samo jak na stronie (`OwnerName`).
- **Test akcji…** w oknie: wybór miasta, zakładu, stacji albo linii z ostatniego `dynamic.json`, widza i akcji (ustaw właściciela, usuń właściciela, nowa nazwa), a potem wysyłka do gry i wynik. Do testu TF3 musi działać z włączonym modem.

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
    Ui/ModSettingsDialog.cs       ustawienia moda (częstotliwość eksportu, pojazdy, teren)
    ModSettings.cs                ustawienia moda i ich zapis jako settings.lua dla app.loadUserdata
    Ui/UiLogProvider.cs           logi do okna
    Cloud/AzureSettings.cs        adres kontenera + SAS, zapis zaszyfrowany (DPAPI)
    Cloud/AzureMirror.cs          kolejka zmienionych plików i upload do Blob Storage
    Cloud/MirroringPublisher.cs   zapis lokalny + oznaczenie pliku do wysyłki
    Twitch/TwitchAuthClient.cs    OAuth Twitcha: device code, refresh, validate, revoke, typ kanału
    Twitch/TwitchAccount.cs       konto streamera + tokeny, zapis zaszyfrowany (DPAPI), TwitchOptions
    Twitch/TwitchSession.cs       walidacja co godzinę i odświeżanie tokenu, stan dla okna
    Twitch/EventSubClient.cs      EventSub przez WebSocket: subskrypcje, keepalive, reconnect, zdarzenia czatu i punktów
    Ui/TwitchDialog.cs            logowanie kodem z twitch.tv/activate, wylogowanie
    Commands/ModCommand.cs        komenda dla moda i jej zapis jako Lua (escapowanie tekstu widzów)
    Commands/ModCommandQueue.cs   kolejka queue.lua, odczyt wyników z results.lua, wygasanie
    Commands/GameObjects.cs       cele akcji z dynamic.json, nazwy „Nazwa | Właściciel”
    Ui/CommandTestDialog.cs       test bez Twitcha: symulacja nagrody i ręczna zmiana nazwy
    Rewards/RewardSettings.cs     nagrody (rodzaje, koszty, miesiące, mnożniki), zapis rewards.json
    Rewards/TargetResolver.cs     dopasowanie tekstu od widza do miasta, zakładu albo linii
    Rewards/RewardService.cs      nagrody na Twitchu, wykupienia -> komendy, FULFILLED/CANCELED, pauza bez gry
    Twitch/ChannelPointsApi.cs    Helix: nagrody i status wykupień
    Twitch/ChatApi.cs             Helix: wiadomość na czacie jako streamer
    Rewards/RewardMessages.cs     treść odpowiedzi dla widzów (data końca efektu w grze, powód zwrotu)
    Ui/RewardsDialog.cs           ustawienia nagród (balans)
    Input/LuaUserdataReader.cs    wyciąganie JSON z plików .lua
    Input/ExportFile.cs           parsowanie nazw plików
    Processing/ExportProcessor.cs jeden przebieg: sesje, sieć, dynamic, publikacja
    Processing/NetworkState.cs    składanie sieci z zrzutu i różnic
    Processing/JsonRead.cs        bezpieczne odczyty liczb i stringów z JsonNode
    Output/IPublisher.cs          interfejs celu publikacji
    Output/LocalFolderPublisher.cs
  tests/Tf3Bridge.Tests/          xUnit: reader, składanie sieci, przebieg procesora, ustawienia Azure i moda, logowanie Twitcha, EventSub, kolejka komend, nagrody
```

Skrypt zakładający zasoby w Azure: [../infra/azure-setup.ps1](../infra/azure-setup.ps1).

## Następne kroki

- Hosting strony w Azure Static Web Apps (plan Free) i ustawienie tam `dataBaseUrl`.
- Strona jest osobnym projektem w [../web/](../web/README.md). Lokalnie czyta to, co Tf3Bridge publikuje do `web/data/latest/`.
