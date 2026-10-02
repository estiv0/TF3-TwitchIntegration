# TF3 Live: strona

Statyczna strona (HTML, CSS i moduły JS, bez kroku budowania), która pokazuje dane z gry: mapę sieci, miasta, zakłady, linie i firmę. Docelowo działa na Azure Static Web Apps. Nie zależy od Tf3Bridge, łączy je tylko format JSON i adres danych.

## Skąd bierze dane

`config.js` → `dataBaseUrl`:
- **lokalnie:** `data/latest/`. Tf3Bridge w trybie Development publikuje do `web/data/latest/` (folder w `.gitignore`);
- **na Azure:** URL kontenera Blob, np. `https://<konto>.blob.core.windows.net/live/latest/`.

Strona co `pollMs` (5 s) pobiera `meta.json`. `dynamic.json` i `network.json` pobiera tylko wtedy, gdy zmieni się ich `seq`.

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
  js/sidebar.js     listy miast, przemysłu i linii, szczegóły zaznaczenia
  js/format.js      formatowanie liczb i dat, polskie nazwy ładunków i środków transportu
```

## Mapa

- Krawędź TF to krzywa Hermite'a (pozycje + tangensy). Na canvasie rysuje się ją jako krzywą Béziera z punktami kontrolnymi `p0 + t0/3` i `p1 - t1/3`.
- Sieć jest budowana raz do `Path2D` w układzie świata. Pan i zoom zmieniają tylko transformację, więc ok. 9 tys. odcinków rysuje się płynnie.
- Oś y w grze rośnie na północ, a na ekranie w dół, dlatego mapa używa `(x, -y)`.
- Linii nie da się jeszcze narysować, bo mod nie eksportuje pozycji stacji (patrz `docs/reactive-dynamic-export.md`, sekcja „stacje”).

## Debug

W konsoli przeglądarki: `tf3.map` (widok, dane) i `tf3.sidebar`.
