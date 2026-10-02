# Architektura (propozycja)

Cel: jak najtaniej, docelowo prawie 0 zł miesięcznie, na Azure.

## Przepływ

```
 TF3 + mod Lua
   │  app.saveUserdata → ...\3493540\local\tf3export\*.lua (JSON w literale Lua)
   │  zapas: linie [tf3x] w crash_dump\stdout.txt
   ▼
 Middle app (C# / .NET, na PC streamera)
   │  gzip, upload HTTPS bezpośrednio do Blob Storage (token SAS z prawem tylko do zapisu)
   ▼
 Azure Blob Storage (kontener "live", publiczny odczyt)
   │  latest/dynamic.json.gz     ← nadpisywany co snapshot
   │  latest/network.json.gz     ← nadpisywany przy zmianie sieci
   │  latest/meta.json           ← sessionId, seq, czas: mały plik do pollingu
   │  history/<session>/<seq>.json.gz   (opcjonalnie, do wykresów)
   ▼
 Strona (Azure Static Web Apps, plan Free)
     polluje meta.json co ~5–10 s; gdy seq się zmieni, pobiera dynamic.json
     network.json pobiera tylko wtedy, gdy zmieni się jego wersja
```

### Dlaczego bez własnego serwera i bazy na start

- Dane mają **jednego pisarza** (mod streamera) i **wielu czytelników**. To przypadek „statyczny plik + CDN”, a nie baza danych.
- Blob Storage kosztuje grosze: kilka MB danych, a zapisów co ~30 s jest ok. 90 tys. miesięcznie. Static Web Apps Free hostuje stronę za darmo.
- Odpada kod serwera, cold starty Functions i zarządzanie połączeniami.
- Pliki są gzipowane, `Content-Encoding: gzip` ustawia middle app przy uploadzie, a przeglądarka rozpakowuje je sama.
- CORS ustawiamy w Storage Account na domenę strony.

> Ceny i limity planów Free sprawdzić w aktualnym cenniku Azure przed wdrożeniem.

### Kiedy dołożyć coś więcej

| Potrzeba | Dodatek | Koszt |
|---|---|---|
| Historia, wykresy w czasie | Azure Table Storage (NoSQL, klucz `session/seq`) albo Cosmos DB z darmowym tierem (jeden na subskrypcję) | grosze / 0 |
| Ukrycie tokenu SAS, walidacja danych | Azure Functions (plan Consumption, darmowa pula wywołań) jako endpoint `POST /ingest` | ~0 |
| Prawdziwy push zamiast pollingu | Azure Web PubSub (plan Free ma bardzo niski limit połączeń, więc nie skaluje się na widzów) albo SignalR | płatne przy większym ruchu |

Na MVP wystarczy polling co 5–10 s. Dane i tak zmieniają się co 2 dni gry, czyli co kilkadziesiąt sekund.

## Middle app (C#)

**Stan (2026-10-02):** wersja lokalna jest gotowa w `middle_app/` (`Tf3Bridge`) i przetestowana na prawdziwych plikach z gry. Publikuje `latest/dynamic.json`, `latest/network.json` i `latest/meta.json` do lokalnego folderu. Szczegóły w [../middle_app/README.md](../middle_app/README.md). Wysyłka do Azure jeszcze nie jest zrobiona.

- `FileSystemWatcher` na `local\tf3export\*.lua` plus okresowy skan katalogu jako zabezpieczenie, bo watcher gubi zdarzenia przy buforze.
- Z pliku wyciąga literał `json = "..."` i odwraca escapowanie Lua (`\"`, `\\`, `\n`, `\r`, `\t`, `\ddd`). Plik bez końcowego `end` jest jeszcze zapisywany, więc odczyt trzeba ponowić.
- Pliki obsługuje kolejno według `(session, seq)`. Wysyła z ponawianiem prób (backoff), a plik usuwa **dopiero po sukcesie** uploadu. Przy zaległościach wystarczy wysłać najnowszy `dynamic` i najnowszy `network`.
- Zapas (gdy `app.saveUserdata` zawiedzie): tail `crash_dump\stdout.txt` i sklejanie linii `[tf3x] <kind> <session> <seq> <part>/<total> ...`.
- Konfiguracja (URL kontenera, token SAS, katalog) w `appsettings.json`, poza repo.
- Najlepiej jako aplikacja z ikoną w trayu, z podglądem statusu: ostatni seq i błędy.

## Rozszerzenie Twitcha (po MVP)

Rozszerzenie to statyczny frontend (HTML/JS) hostowany przez Twitcha i wyświetlany w jednym z trzech miejsc:

| Typ | Gdzie | Pomysł dla nas |
|---|---|---|
| **Panel** | pod streamem, w zakładce opisu | Mini-dashboard: firma, saldo, liczba linii i pojazdów, top miasta |
| **Video overlay** | przezroczysta warstwa na całym streamie (desktop) | Widz najeżdża na mapę lub ikonę i widzi szczegóły linii albo miasta. Mapa sieci jako przełączana nakładka. |
| **Component** | mały, przesuwalny element na wideo | Licznik lub ticker zdarzeń („nowa linia”, „miasto X urosło”) |

Ograniczenia, które trzeba znać:
- Pliki frontendu wgrywa się do Twitcha jako paczkę. Zewnętrzne zapytania (do naszego Bloba) muszą być na liście dozwolonych URL-i rozszerzenia.
- Do Twitcha nadaje się mały push przez Extension PubSub z backendu (małe wiadomości, limit częstotliwości), np. sygnał „jest nowy seq”. Duże dane widz i tak pobiera z Bloba.
- Publiczne wydanie wymaga przeglądu (review) przez Twitcha. Do testów na własnym kanale review nie jest potrzebne.
- Interakcja widzów z grą (głosowania na nową linię, nazwy pociągów od subów) to osobny, większy temat: wymaga kierunku strona → mod, który mod mógłby czytać z pliku.

**Rekomendacja:** robić MVP jako zwykłą stronę, ale tak, żeby komponenty (mapa, dashboard) dało się potem wpiąć do rozszerzenia bez przepisywania. Wystarczy statyczny build bez SSR i jedno źródło danych (Blob).
