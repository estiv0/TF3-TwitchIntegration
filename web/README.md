# TF3 Live: strona

Statyczna strona (HTML, CSS i moduły JS, bez kroku budowania), która pokazuje dane z gry: mapę sieci, miasta, zakłady, linie i firmę. Docelowo działa na Azure Static Web Apps. Nie zależy od Tf3Bridge, łączy je tylko format JSON i adres danych.

## Skąd bierze dane

`config.js` → `dataBaseUrl`:
- **lokalnie:** `data/latest/`. Tf3Bridge w trybie Development publikuje do `web/data/latest/` (folder w `.gitignore`);
- **na Azure:** URL kontenera Blob, np. `https://<konto>.blob.core.windows.net/live/latest/`.

Strona co `pollMs` (5 s) pobiera `meta.json` (w ukrytej karcie nie odpytuje). `dynamic.json` i `network.json` pobiera tylko wtedy, gdy zmieni się ich `seq`.

## Uruchomienie lokalnie

1. Uruchom Tf3Bridge (`middle_app`, profil Development), żeby dane trafiały do `web/data/latest/`.
2. Podaj folder `web/` serwerem statycznym:

   ```powershell
   .\web\serve.ps1
   ```

   Skrypt uruchamia narzędzie `dotnet-serve` (w razie potrzeby je instaluje) i ustawia mu `DOTNET_ROOT`, bo SDK jest w katalogu użytkownika. Zadziała też każdy inny serwer statyczny, np. rozszerzenie Live Server w VS Code.
3. Otwórz http://localhost:5080.

Sam plik `index.html` otwarty z dysku nie zadziała, bo przeglądarka blokuje `fetch` z `file://`.

## Struktura

```
web/
  index.html        układ: górny pasek, mapa, panel boczny
  config.js         adres danych i interwały
  css/style.css     ciemny motyw (zmienne CSS używa też mapa)
  js/main.js        spina dane, mapę i panel
  js/api.js         DataFeed: polling meta.json, pobieranie dynamic/network przy zmianie seq
  js/map.js         MapView: canvas, sieć jako krzywe Béziera (z tangensów TF), pan/zoom/hover/wybór
  js/sidebar.js     listy miast, przemysłu, stacji, linii i właścicieli, szczegóły zaznaczenia
  js/owners.js      majątek widzów: obiekty z "Nazwa | Widz" pogrupowane po widzu, główne miasto = największe
  js/pin.js         pinezka z linkiem (#pin=x,y,skala&note=opis)
  js/build.js       budowa za punkty kanału: kod do nagrody na Twitchu
  js/company.js     zakładka Firma (finanse wg lat, wykresy historii) i sparkline salda w górnym pasku
  js/format.js      formatowanie liczb i dat, polskie nazwy ładunków i środków transportu
```

## Mapa

- Krawędź TF to krzywa Hermite'a (pozycje + tangensy). Na canvasie rysuje się ją jako krzywą Béziera z punktami kontrolnymi `p0 + t0/3` i `p1 - t1/3`.
- Sieć jest budowana raz do `Path2D` w układzie świata. Pan i zoom zmieniają tylko transformację, więc ok. 9 tys. odcinków rysuje się płynnie.
- Oś y w grze rośnie na północ, a na ekranie w dół, dlatego mapa używa `(x, -y)`.
- Linii nie da się jeszcze narysować, bo mod nie eksportuje pozycji stacji (patrz `docs/reactive-dynamic-export.md`, sekcja „stacje”).
- Pojazdy: mod wysyła ich pozycję (`vehicles[].pos`). Pozycja przy trasie linii (do 40 m od odcinka dojazdu do `stop`) daje ruch wzdłuż trasy, inna (statki, samoloty) - po prostej. Bez `pos` (pozycje wyłączone w modzie albo starszy eksport) położenie jest zgadywane: `AT_TERMINAL` na przystanku, `EN_ROUTE` na trasie od poprzedniego przystanku do `stop`, kilka pojazdów na odcinku rozłożonych równo (na długim odcinku pojazd wtedy stoi).
- Ruch pojazdów jest interpolowany: po nowej paczce danych pojazd jedzie z miejsca, w którym jest na ekranie, do nowej pozycji, przez średni odstęp między paczkami. Obraz jest więc o jedną paczkę (ok. 10–15 s) opóźniony. Animacja ma 30 kl./s.
- Trasa linii: przystanek dociągany jest do węzła sieci w tym samym spójnym kawałku co sąsiedni przystanek, bo eksport ma odcięte fragmenty dróg (dojazdy do stacji).
- Zaznaczenie jest w adresie (`#line=<id>`, `#town=`, `#industry=`, `#station=`, a majątek widza `#owner=<nazwa małymi literami>`), obok pinezki: `#pin=...&note=...&line=<id>`. Zaznaczenie właściciela nie rusza mapy.
- Na dotyku: przesuwanie jednym palcem, przybliżanie szczypaniem.

## Budowa za punkty kanału

Przycisk 🏗 na mapie (`build.js`): widz klika miejsce, wybiera miasto albo zakład (rodzaje: te, które są w `dynamic.json`), wpisuje nazwę i kopiuje kod `[x, y] Nazwa` albo `[x, y, coal_mine] Nazwa`. Kod wkleja w nagrodę „TF3: załóż miasto” / „TF3: zbuduj zakład” na Twitchu (Twitch nie ma linku, który wypełnia tekst nagrody). Tf3Bridge buduje to w grze ([../middle_app/README.md](../middle_app/README.md#nagrody-za-punkty-kanału)).

- Strona od razu ostrzega o tym, co Tf3Bridge odrzuci: za blisko miasta albo zakładu, przy krawędzi mapy, zajęta nazwa. Liczby są te same co w `BuildCode.cs`; na mapie przerywany okrąg pokazuje minimalną odległość od miast.
- Wody i kolizji z torami strona nie sprawdza; robi to gra.
- `twitchUrl` w `config.js` (adres kanału) dodaje w okienku link „Otwórz Twitcha”.

## Efekty od widzów

`dynamic.effects` (z moda, `effects.lua`) to aktywne efekty z nagród za punkty kanału: `{ type, entity, user, factor, startTime, untilTime, until: {year, month, day} }`.

- `attachEffects` (`format.js`) dokleja je do miast (`townBoost`), zakładów (`industryBoost`) i linii (`lineStop`, `lineHold`) jako `x.effects`. Dolicza też `progress`, czyli ile czasu już minęło, liczone z `gameTime` eksportu.
- Na liście: znaczek przy nazwie (⚡ boost, ⛔ linia zatrzymana, ⏸ linia czeka) z opisem w podpowiedzi.
- W szczegółach: sekcja „Efekty od widzów” z siłą, datą końca w kalendarzu gry, kupującym widzem i paskiem pozostałego czasu.
- Starszy eksport bez `effects` działa jak dawniej.

## Wdrożenie na Azure

Strona działa na **Azure Static Web Apps** (plan Free: własna domena i HTTPS za darmo). Dane czyta z kontenera `live` konta `tf3liveblob` (`AZURE_DATA_URL` w `config.js`). Wgrywanie: [../infra/deploy-web.ps1](../infra/deploy-web.ps1), wymaga Node.js (`winget install OpenJS.NodeJS.LTS`).

1. Portal → Static Web App → **Overview → Manage deployment token**, skopiuj token.
2. Pierwszy raz: `.\infra\deploy-web.ps1 -DeployToken "<token>"` (token zostaje zapamiętany zaszyfrowany w `%LOCALAPPDATA%\Tf3Bridge`).
3. Kolejne razy: `.\infra\deploy-web.ps1`. `-DryRun` tylko pokazuje listę plików.

Skrypt wysyła całą stronę bez `data\`, `img_res\`, `serve.ps1` i `README.md`, przez Static Web Apps CLI (`npx @azure/static-web-apps-cli`).

Zapasowo strona może też działać jako „Static website” konta Storage (kontener `$web`, adres `https://tf3liveblob.z36.web.core.windows.net/`, bez HTTPS dla własnej domeny): `.\infra\deploy-web.ps1 -Target storage -SasUrl "<Blob SAS URL kontenera $web>"` (SAS z prawami Create + Write). Ten wariant wysyła tylko zmienione pliki (`-Force` wszystkie) i sam ustawia typy plików i `Cache-Control`.

## Debug

W konsoli przeglądarki: `tf3.map` (widok, dane) i `tf3.sidebar`.
