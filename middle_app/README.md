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

Integracja z Twitchem ([../docs/twitch-channel-points.md](../docs/twitch-channel-points.md)): logowanie streamera, EventSub, nagrody za punkty kanału i komendy z czatu (opis niżej). Grę zmieniają tylko punkty kanału; czat jest wyłącznie informacyjny.

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

## Komendy z czatu

Nasze komendy zaczynają się od `!` (bez prefiksu). Tylko czytają ostatni `dynamic.json` i odpowiadają na czacie jako streamer, nigdy nie wysyłają nic do gry (`Chat/ChatCommandService.cs`: lista komend i cooldowny, `Chat/ChatAnswers.cs`: treść odpowiedzi). Linki tylko przy ustawionym `Twitch:SiteUrl`. Nazwy obiektów są dopasowywane tak samo jak w nagrodach (`TargetResolver`): bez wielkości liter i polskich znaków, najpierw dokładnie, potem po początku, potem po fragmencie; przy kilku pasujących odpowiedź je wymienia.

| Komenda | Aliasy | Odpowiedź | Cooldown (cały czat) |
|---|---|---|---|
| `!pomoc` | `!komendy`, `!help` | lista komend | 30 s |
| `!mapa` | | link do strony | 30 s |
| `!majatek [widz]` | `!majątek` | majątek widza (bez argumentu: pytającego), np. „@ala estiv0: 2 miasta (Szczecin – główne, Barcin), 1 zakład (Browar), 2 linie, 16 pojazdów, zysk linii (12 mies.) $ 833 881. Mapa: https://…/#owner=estiv0”. Grupowanie jak zakładka „Właściciele” (`Commands/OwnerAssets.cs`): bez rozróżniania wielkości liter, główne miasto = największe, stacje nazwane od kupionego miasta nie są widza | 30 s |
| `!miasto <nazwa>` | | właściciel, pojemność, wzrost od startu gry, zaspokojenie potrzeb, aktywne efekty z nagród (z datą końca i kupującym), link | 30 s |
| `!zaklad <nazwa>` | `!zakład` | właściciel, poziom, produktywność, produkcja z ostatniego roku, efekty, link | 30 s |
| `!linia <nazwa>` | | właściciel, pojazdy (i ile zatrzymanych), zysk z 12 mies., zapełnienie, przewiezione w roku, efekty, link | 30 s |
| `!ranking` | `!top` | 5 widzów z największym majątkiem jak na stronie: miasto 100 pkt + wielkość/25 + wzrost od startu gry ×100, zakład 50, stacja i linia 1, pojazd na liniach: pociąg/samolot 10, statek 2, drogowy 1; przy remisie zysk linii | 60 s |
| `!wolne [miasta\|zaklady]` | | obiekty bez właściciela do kupienia za punkty: 5 największych miast i zakładów o najwyższym poziomie, z liczbą wszystkich | 60 s |
| `!data` | | data w grze; gdy eksport jest starszy niż 2 min, dopisek „pauza albo gra wyłączona” | 30 s |
| `!firma` | | firma streamera: saldo, wartość firmy, zysk w tym i poprzednim roku (tabela Finanse), linie, pojazdy | 60 s |
| `!najlepsza [linia\|firma]` | `!najlepszy` | linia z najwyższym zyskiem z 12 mies. albo (`firma`) widz z najwyższym łącznym zyskiem swoich linii | 60 s |
| `!najgorsza [linia\|firma]` | `!najgorszy` | jak wyżej, najniższy zysk | 60 s |

- Cooldowny są dwa, oba ciche (bez odpowiedzi): jeden widz dostaje najwyżej jedną odpowiedź na 10 s (dowolna komenda), a ta sama komenda z tym samym argumentem jest powtarzana na czacie najwyżej raz na czas z tabeli. Argument porównujemy bez wielkości liter i polskich znaków, więc `!miasto Łódź` i `!miasto lodz` to ta sama odpowiedź. `!majatek` bez argumentu liczy się osobno dla każdego widza.
- Z serwerem testowym Twitch CLI odpowiedź trafia tylko do logu (serwer nie ma czatu).

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

Jedyna droga zmian w grze od widzów (`RewardService`). Trzynaście nagród, każdą da się wyłączyć i zbalansować w oknie **Nagrody…**: tytuł, opis, koszt, cooldown, limity na stream i na widza, czas w miesiącach gry i mnożnik. Czas jest w miesiącach, nie w dniach: dzień gry trwa tylko 2–3 s, więc efekt na kilka dni mijał, zanim było go widać.

| Nagroda | Cel (tekst od widza) | Komenda dla moda |
|---|---|---|
| Kupno miasta | miasto bez właściciela | `adopt`: „Nazwa \| widz”; miasto z właścicielem = zwrot punktów |
| Kupno zakładu | istniejący zakład bez właściciela | `adopt`: „Nazwa \| widz”; zakład z właścicielem = zwrot punktów |
| Boost miasta | miasto | `effect townBoost` (miesiące, mnożnik) |
| Boost przemysłu | zakład | `effect industryBoost` (miesiące, mnożnik) |
| Stop linii (od razu) | linia | `effect lineStop` (miesiące) |
| Linia czeka na stacjach | linia | `effect lineHold` (miesiące) |
| Nowe miasto | kod z mapy: `[x, y] Nazwa` | `createTown`: miasto „Nazwa \| widz” w punkcie (x, y) |
| Nowy zakład | kod z mapy: `[x, y, rodzaj] Nazwa` | `createIndustry`: zakład „Nazwa \| widz” (rodzaj = `coal_mine` itp.) |
| Strajk w zakładzie | zakład innego widza | `effect industryStrike` (miesiące, mnożnik 0,01 = −99%) |
| Nalot skarbówki | nick innego widza | `effect industryTaxRaid`: wszystkie jego zakłady naraz (`entities`, klucz `target` = widz), mnożnik 0,5 |
| Paliwo premium | dowolna linia | `effect lineFuelPremium`: prędkość maks. pojazdów × 1,2 |
| Chrzczone paliwo | linia innego widza | `effect lineFuelBad`: prędkość maks. pojazdów × 0,8 |
| Sabotaż hamulców | linia innego widza | `wreck`: od 1 do „mnożnik” (domyślnie 50%) pojazdów znika, nie do cofnięcia |

- **Sabotaże** (strajk, skarbówka, chrzczone paliwo, hamulce) działają tylko na obiekty innych widzów: cel bez właściciela albo własny kupującego = zwrot punktów. Nalot skarbówki dopasowuje nick jak nazwy (wielkość liter, polskie znaki i `@` bez znaczenia) do właścicieli z eksportu; widz bez zakładów = zwrot. Mnożnik ma zakres zależny od nagrody (`RewardKinds.FactorRange`): boosty i paliwo premium powyżej 1, sabotaże poniżej 1, przy hamulcach to największa część pojazdów linii. W opisie `{percent}` to zmiana bez znaku („spada o {percent}%”).
- **Sabotaż hamulców:** mod podaje w wyniku, ile pojazdów zniknęło (`count` z `total`). Odpowiedź na czacie: „@widz sabotaż hamulców na linii X: zniszczone pojazdy: 2 z 5.”, powiadomienie w grze: „Problemy techniczne na linii X: zniszczone pojazdy: 2 z 5 (sabotaż: widz).”; kliknięcie pokazuje linię.

- **Nowe miasto i zakład:** Twitch nie ma linku, który otwiera nagrodę z wpisanym tekstem, więc widz bierze kod ze strony: przycisk 🏗 na mapie, kliknięcie miejsca, rodzaj i nazwa, „Kopiuj kod” ([../web/README.md](../web/README.md#budowa-za-punkty-kanału)). `BuildCode` sprawdza kod jeszcze raz na ostatnim eksporcie: nazwa 2–40 znaków i niezajęta, punkt co najmniej 300 m od krawędzi mapy (`terrain.json`), miasto ≥ 1500 m od miast i ≥ 600 m od zakładów, zakład ≥ 800 m od miast i ≥ 600 m od zakładów, rodzaj zakładu tylko spośród tych, które są na mapie (plik `.con` bierzemy z eksportu). Miasto nie może stać na wodzie ani w górach: `TownSites` (liczone przy każdym nowym eksporcie terenu, publikowane też jako `latest/terrain-site.png` dla strony) mówi, jaka część obszaru ±250 m wokół punktu to woda i zbocza > 15%; powyżej 30% wody albo 50% zboczy kod jest odrzucany (gra zakładała tam miasto bez ani jednej ulicy). Zakłady mają regiony (`TerrainZones`, `latest/terrain-zone.png`, okno ±1 km): **góry** = średnie nachylenie ≥ 8% i zarazem najwyżej 15% najbardziej pofałdowanego lądu mapy, **przy wodzie** = woda w oknie, **nizina** = średnie nachylenie ≤ 3%, **z dala od wody** = brak wody w oknie. Domyślnie kopalnie węgla i rudy oraz kamieniołom tylko w górach, piaskownia, koparka piasku i glinianka przy wodzie, farmy na nizinie, las z dala od wody; streamer zmienia to w oknie „Nagrody” (zapis w `rewards.json`, pole `Zones`), a Tf3Bridge publikuje to stronie jako `latest/build-rules.json`. Region, którego na mapie nie ma, niczego nie ogranicza. Ile lądu zajmuje każdy region, Tf3Bridge pisze w logu po eksporcie terenu. Mod wysyła teren tylko po wczytaniu zapisu albo po terraformowaniu, więc Tf3Bridge trzyma kopię ostatniego eksportu (`%LOCALAPPDATA%\Tf3Bridge\terrain-cache.json`, `Bridge:TerrainCacheFile`) i po restarcie bierze teren stamtąd, jeśli to ta sama sesja gry. Bez żadnego eksportu terenu tych sprawdzeń nie ma. Kolizje z torami, drogami i budynkami sprawdza dopiero gra; wtedy punkty wracają. W opisie nagrody `{site}` zamienia się na adres mapy (`Twitch:SiteUrl`), a odpowiedź na czacie linkuje nowy obiekt po id, które mod podaje w wyniku komendy (`#town=<id>`, `#industry=<id>`); strona czeka chwilę, aż obiekt pojawi się w eksporcie. Gdy w grze jest starsza wersja moda bez id, link prowadzi do pinezki w miejscu budowy (`#pin=x,y&note=Nazwa`).

- **Na Twitchu:** Tf3Bridge sam tworzy nagrody (tylko te może potem zmieniać i rozliczać), zapamiętuje ich ID w `%LOCALAPPDATA%\Tf3Bridge\rewards.json` i aktualizuje je po zapisaniu ustawień. Nagrody są włączone tylko wtedy, gdy most działa i dostał od startu dane z gry (zapis jest wczytany); przy „Zatrzymaj”, bez nowego eksportu przez 2 minuty (np. pauza w grze) i przy zamykaniu Tf3Bridge są wyłączane (widzowie ich nie widzą). Wykupienia, które mimo to przyjdą, gdy gra nie działa, są od razu zwracane. Wymaga kanału Affiliate/Partner; bez tego nic nie trafia na Twitcha.
- **Nagrody wykonywane przez streamera** (`RewardKinds.Manual`): „TF3: bomba”, „TF3: linia”, „TF3: natura”, „TF3: pojazd drogowy”, „TF3: pojazd specjalny”. Nie mają żadnego efektu w modzie. Tf3Bridge tworzy je jak pozostałe, żeby móc je włączać i wyłączać razem z grą (nagrody dodanej ręcznie w panelu Twitcha nie mógłby zmieniać). Ich wykupienia zostają w kolejce Twitcha, a streamer sam je wykonuje i akceptuje albo zwraca punkty. Tf3Bridge ich nie rozlicza, także wtedy, gdy gra nie działa, i nie zwraca starych przy starcie. Opis, koszt i limity streamer ustawia w oknie **Nagrody…**; tam też wybiera, czy widz wpisuje tekst (kolumna „Widz wpisuje tekst”, domyślnie nie). Nie ma ich w „Symuluj nagrodę”.
- **Własne nagrody z prefiksem TF3** (`ExtraReward`): nagrody streamera, których tytuł zaczyna się od „TF3” i które nie są żadną z akcji (np. „TF3: wybierz następną linię”). Tf3Bridge nie robi z nimi nic w grze, a ich wykupienia zostają w kolejce Twitcha dla streamera. Włącza je i wyłącza (`is_enabled`, bez usuwania) dokładnie wtedy, co nasze akcje. Listę odczytuje raz, przy połączeniu z Twitchem (start Tf3Bridge, zalogowanie), i po zapisaniu okna **Nagrody…**; tam każdą można odznaczyć, wtedy zostaje wyłączona na stałe. Uwaga: Twitch pozwala zmieniać nagrodę tylko aplikacji (Client ID), która ją utworzyła. Na nagrodę dodaną ręcznie w panelu odpowiada 403. Tf3Bridge pokazuje ją wtedy w statusie („Twitch nie pozwala Tf3Bridge włączać i wyłączać: …”) i do następnego startu już jej nie rusza. Ręczna nagroda z tytułem jednej z naszych akcji blokuje założenie naszej (Twitch odrzuca powtórzony tytuł) i też trafia do statusu.
- **Wykupienie:** tekst od widza jest dopasowywany do nazw z ostatniego `dynamic.json` (`TargetResolver`). Wielkość liter, polskie znaki, spacje i „\| właściciel” nie mają znaczenia. Wygrywa dokładne dopasowanie, potem jednoznaczny początek nazwy, potem jednoznaczny fragment.
- **Rozliczenie:** gdy gra potwierdzi, wykupienie dostaje FULFILLED. Gdy celu nie ma, cel jest niejednoznaczny, nagroda jest wyłączona albo gra odmówi lub nie zdąży, wykupienie dostaje CANCELED i Twitch zwraca punkty.
- **Wykupienia z czasu, gdy Tf3Bridge był wyłączony:** świeższe niż 5 minut są wykonywane, starsze zwracane.
- **Odpowiedź na czacie** (`RewardMessages`, `ChatApi`, Helix `POST /chat/messages` jako streamer). Wyłącznik jest w **Nagrody…**. Przykłady:
  - „@widz boost miasta Szczecin (+50%) działa do 12 maja 1922 (data w grze). https://gin.estfosoft.com/#town=225799” Datę końca w kalendarzu gry podaje mod w wyniku komendy (`until`).
  - „@widz miasto Kraków jest teraz Twoje! https://gin.estfosoft.com/#town=…”
  - Link prowadzi do obiektu na stronie (`#town=`, `#industry=`, `#line=`). Adres strony: `Twitch:SiteUrl` w `appsettings.json`; pusty = bez linków.
  - Przy zwrocie: „@widz boost miasta: nie znaleziono „gdańsk”. Punkty wróciły do Ciebie.”
- **Powiadomienie w grze:** po wykonanej nagrodzie, dopiero gdy Twitch przyjmie FULFILLED (punkty pobrane), Tf3Bridge wysyła do moda komendę `notify`: okienko z logo Twitcha, „Twitch: widz” i opisem („widz kupił(a) miasto Kraków.”), kliknięcie pokazuje obiekt. Przy zwrocie punktów go nie ma. Wyłącznik i czas wyświetlania (3–300 s, domyślnie 30 s; potem okienko znika, a wpis zostaje w dzienniku powiadomień gry) w **Nagrody…**; w symulacji z dopiskiem `[TEST]`, a w **Test akcji…** jest też przycisk „Testowe powiadomienie”. Szczegóły: [../docs/custom-notifications.md](../docs/custom-notifications.md).
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
    Commands/OwnerAssets.cs       majątek widzów z dynamic.json (jak zakładka Właściciele na stronie)
    Chat/ChatCommandService.cs    komendy z czatu (!...): lista, aliasy, cooldowny; bez zmian w grze
    Chat/ChatAnswers.cs           treść odpowiedzi na komendy z czatu (z dynamic.json)
    Ui/CommandTestDialog.cs       test bez Twitcha: symulacja nagrody i ręczna zmiana nazwy
    Rewards/RewardSettings.cs     nagrody (rodzaje, koszty, miesiące, mnożniki), zapis rewards.json
    Rewards/TargetResolver.cs     dopasowanie tekstu od widza do miasta, zakładu albo linii
    Rewards/RewardService.cs      nagrody na Twitchu, wykupienia -> komendy, FULFILLED/CANCELED, wyłączanie bez gry
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
