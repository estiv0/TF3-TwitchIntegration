# Punkty kanału Twitcha (channel points): analiza integracji

**Stan (2026-10-04):** w Tf3Bridge działa logowanie streamera ([opis](../middle_app/README.md#twitch-logowanie)), kanał Tf3Bridge → mod działa w grze ([opis](../middle_app/README.md#komendy-dla-moda-akcje-widzów)), a EventSub odbiera czat i wykupienia ([opis](../middle_app/README.md#twitch-eventsub-czat-i-punkty-kanału)). Kanał autora nie ma statusu Affiliate, więc zmiany w grze sprawdzamy na razie przyciskiem **Test akcji…** i Twitch CLI.

**Zasada (2026-10-04): grę zmieniają tylko punkty kanału.** Kupno miast, boosty fabryk i podobne akcje są wyłącznie nagrodami za punkty, bo nad nimi streamer ma kontrolę: koszt, cooldown, limity, pauza. Komendy z czatu są tylko informacyjne (np. kto jest właścicielem miasta, stan linii) i nigdy nie trafiają do kolejki komend gry. Streamer nie kontroluje, kto i jak często ich używa, a mogłyby popsuć balans. W kodzie pilnuje tego `CommandSource`: komenda dla moda może pochodzić tylko z punktów kanału albo z ręcznego testu w oknie.

W skrócie: integrację da się zrobić bez nowej infrastruktury w Azure. Tf3Bridge odbiera wykupienia z Twitcha i przekazuje je do moda tą samą drogą, którą dziś wysyła ustawienia moda. Gra ma gotowe komendy na większość sensownych nagród.

## 1. API Twitcha

### A. Helix REST: zarządzanie nagrodami

Bazowy URL: `https://api.twitch.tv/helix/channel_points/...`

| Endpoint | Do czego |
|---|---|
| `POST /custom_rewards` | Tworzy nagrodę: tytuł do 45 znaków, koszt, opcjonalne pole tekstowe dla widza (`is_user_input_required`, prompt do 200 znaków), cooldown (`global_cooldown_seconds`), limity na stream i na widza (`max_per_stream`, `max_per_user_per_stream`), kolor |
| `PATCH /custom_rewards` | Zmienia nagrodę, np. pauzuje ją (`is_paused`) albo wyłącza, gdy gra nie działa |
| `GET` / `DELETE /custom_rewards` | Listuje i usuwa nagrody |
| `GET /custom_rewards/redemptions` | Pobiera wykupienia w kolejce (`UNFULFILLED`) |
| `PATCH /custom_rewards/redemptions` | Oznacza wykupienie jako `FULFILLED` albo `CANCELED`. `CANCELED` zwraca widzowi punkty |

Wymagania i ograniczenia:
- Streamer musi być Affiliate albo Partner.
- Zarządzanie wymaga scope'a `channel:manage:redemptions`, a sam odczyt `channel:read:redemptions`.
- Kanał może mieć najwyżej 50 nagród, licząc też wyłączone.
- **Nagrodę może zmieniać i rozliczać tylko ta aplikacja (Client ID), która ją utworzyła.** Nagrody trzeba więc zakładać z poziomu Tf3Bridge, a nie ręcznie w panelu Twitcha. Inaczej nie zrobimy automatycznego zwrotu punktów, gdy komenda się nie uda. Nagrody dodanej ręcznie nie da się przez API nawet włączyć ani wyłączyć (403). Tf3Bridge próbuje to zrobić z nagrodami z prefiksem „TF3” i zgłasza w statusie te, których Twitch nie pozwala zmieniać ([opis](../middle_app/README.md#nagrody-za-punkty-kanału)).
- `should_redemptions_skip_request_queue = true` od razu oznacza wykupienie jako zrealizowane. U nas powinno być `false`, żeby móc zwracać punkty.

### B. EventSub: zdarzenia w czasie rzeczywistym

| Typ subskrypcji | Wersja | Scope |
|---|---|---|
| `channel.channel_points_custom_reward_redemption.add` | 1 | `channel:read:redemptions` |
| `channel.channel_points_custom_reward_redemption.update` | 1 | `channel:read:redemptions` |
| `channel.channel_points_custom_reward.add` / `.update` / `.remove` | 1 | `channel:manage:redemptions` |
| `channel.channel_points_automatic_reward_redemption.add` | 1, 2 | `channel:read:redemptions` |

- Zdarzenie `redemption.add` przychodzi, gdy widz wykupi nagrodę. Zawiera login i nazwę widza, `reward.id` oraz tekst `user_input`.
- Automatyczne nagrody Twitcha (np. wyróżniona wiadomość) da się tylko odczytać, nie zarządzać nimi. Nadają się najwyżej do feedu.
- **Wszystkie te zdarzenia działają przez transport WebSocket** (`wss://eventsub.wss.twitch.tv/ws`). Nie potrzeba publicznego serwera ani webhooka, bo aplikacja desktopowa sama otwiera połączenie.
- Stary Twitch PubSub został wyłączony, więc liczy się tylko EventSub.

### C. Logowanie z aplikacji desktopowej

- Najlepszy będzie **Device Code Grant Flow**. Tf3Bridge pokazuje kod, a streamer wpisuje go na `twitch.tv/activate`.
- Aplikacja jest rejestrowana na dev.twitch.tv jako „public client”, więc nie trzeba trzymać client secret.
- Client ID aplikacji (`Twitch:ClientId` w `appsettings.json`): `8cp6n72q1r4gdodyxkonpe4r19ume7`.
- Token odświeżania przechowujemy zaszyfrowany DPAPI w `%LOCALAPPDATA%\Tf3Bridge`, tak jak dziś SAS (`azure.json`).

## 2. Co mamy po naszej stronie

- **Kanał Tf3Bridge → mod już działa.** Ustawienia moda są zapisywane do `local\tf3export_settings\settings.lua`, a mod czyta je przez `app.loadUserdata` co `settingsEveryWallSec` (5 s). Zobacz `lua_mod/content/tf3export/tf3export.script.lua` i `middle_app/src/Tf3Bridge/ModSettings.cs`. Kolejkę komend można zrobić tak samo, np. jako `local\tf3export_cmd\queue.lua`. Uwaga: `loadUserdata` na brakującym pliku pokazuje okno błędu gry, więc najpierw trzeba sprawdzić `app.getAllUserdata` (jak w `settingsFileExists`).
- **Kanał mod → Tf3Bridge też działa** przez `app.saveUserdata`. Mod może w ten sam sposób odsyłać wynik komendy („ok” albo „błąd”).
- **Gra ma bogate API komend:** `api/tealdef/api/cmd.d.tl` w instalacji gry, wysyłane przez `api.cmd.sendCommand`.
- **Strona już rozumie konwencję `Nazwa | Właściciel`** dla miast, przemysłu, stacji i linii (`splitOwners` w `web/js/format.js`). To naturalna podstawa pod „adopcję” obiektów przez widzów.

## 3. Proponowany przepływ

```
Widz wykupuje nagrodę
  → EventSub WebSocket → Tf3Bridge
      walidacja: whitelist typów, dopasowanie celu po nazwie/ID z ostatniego dynamic.json,
      filtr tekstu, limity
  → queue.lua (format app.saveUserdata, z unikalnym redemptionId)
  → mod (loadUserdata co kilka s) → api.cmd.sendCommand(...)
  → mod zapisuje wynik (redemptionId, ok/błąd)
  → Tf3Bridge: PATCH redemption → FULFILLED, albo CANCELED (zwrot punktów)
  → opcjonalnie: wpis w meta/dynamic → strona pokazuje feed „X zrobił Y”
```

Opóźnienie wyniesie kilka sekund. Koszt w Azure się nie zmienia, bo wszystko dzieje się na PC streamera.

## 4. Pomysły na nagrody i komendy gry

| Nagroda | Komenda gry | Uwagi |
|---|---|---|
| **Adoptuj miasto, linię albo stację** (widz staje się „właścicielem”) | `makeEntitySetNameCmd` → `"Nazwa \| login"` | Działa od razu z obecną stroną: pokaże się jako właściciel. Najlepszy kandydat na start |
| **Nazwij pociąg lub pojazd** | `makeEntitySetNameCmd` | Tekst od widza wymaga filtra wulgaryzmów i limitu długości |
| Zmień kolor pojazdu | `makeEntitySetColorCmd` | Kosmetyczne i bezpieczne |
| Pogoda: zachmurzenie, pora dnia, wiatr | `makeGameSetCloudCoverageCmd`, `makeGameSetTimeOfDayCmd`, `makeWorldChangeWindCmd` | Dobrze wygląda na streamie, nie psuje gry |
| Wypuść zwierzę | `makeAnimalSpawnAtCmd` | Ciekawostka |
| Boost miasta | `makeTownUpdateSizeCmd` / `makeTownDevelopAtCmd` | Wpływa na rozgrywkę, wymaga drogiej nagrody i cooldownu |
| Boost produkcji przemysłu | `makeStockListSetModifiersCmd` | Jak wyżej |
| „Dotacja” albo „sabotaż” finansowy | `makeJournalBookAssetCmd` | Dodaje lub odejmuje pieniądze firmie |
| Troll: zatrzymaj losowy pociąg na 30 s | `makeVehicleSetStoppedByUserCmd` (+ odblokowanie po czasie) | Zabawne, ale mod musi sam pamiętać o zdjęciu blokady |
| Zmiana prędkości gry | `makeGameSetSpeedCmd` | Raczej nie: łatwo zirytować streamera |

Rzeczy poza samymi nagrodami:
- **Predictions API** (scope `channel:manage:predictions`) pozwala obstawiać punktami, np. „Czy linia X zarobi > 1 mln w tym roku?”. Wynik rozstrzyga się automatycznie z danych eksportu.
- **Polls API** (scope `channel:manage:polls`) nadaje się do głosowań w stylu „Gdzie zbudować następną linię?”.
- **Strona i przyszłe rozszerzenie:** ranking adoptujących, feed wykupień, oznaczenie obiektów widza na mapie.

## 5. Ryzyka i rzeczy do sprawdzenia

1. **Nic z tego nie było testowane w TF3.** Trzeba sprawdzić w konsoli, czy `api.cmd.sendCommand` działa z wątku GUI, gdzie mamy `loadUserdata`. Jeśli nie, mod musi przekazać komendę do wątku symulacji przez `game.interface.sendScriptEvent`. Najpierw warto zrobić prosty test z `makeEntitySetNameCmd`.
2. **Wskazanie celu.** Widz wpisuje wolny tekst, więc Tf3Bridge musi dopasować go do nazwy z ostatniego eksportu i przy niejednoznaczności zwrócić punkty. Strona mogłaby pokazywać przy obiekcie krótki „kod do nagrody” do skopiowania.
3. **Bezpieczeństwo.** Tekst widza trafia do pliku Lua wyłącznie jako escapowany string. Trzeba mieć whitelistę typów komend i limity długości. Treść widza nigdy nie może stać się kodem.
4. **Idempotencja.** Każda komenda niesie `redemptionId`, a mod pamięta wykonane identyfikatory, żeby po ponownym odczycie pliku nie wykonać ich dwa razy.
5. **Gra wyłączona albo pauza.** Tf3Bridge wyłącza nagrody (`is_enabled: false`), gdy most jest zatrzymany, nie ma świeżych eksportów z wczytanego zapisu albo aplikacja się zamyka; włącza je, gdy dane z gry znów przychodzą. Wykupienia, których nie dało się wykonać, zwraca przez `CANCELED`.
6. **Multiplayer.** Komendy z naszego moda mogą działać inaczej w grze sieciowej. Do sprawdzenia, jeśli będzie potrzebne.

## 6. Proponowana kolejność

1. ✅ Zarejestrować aplikację na dev.twitch.tv jako public client i dodać logowanie Device Code w Tf3Bridge.
2. ✅ Kanał `queue.lua` → mod → `results.lua` z komendą `setName` i przyciskiem **Test akcji…** w Tf3Bridge (adopcja obiektu bez Twitcha). Sprawdzone w grze: `api.cmd.sendCommand` działa z wątku GUI.
3. ✅ EventSub przez WebSocket. Wykupienia sprawdzone na Twitch CLI 1.1.24; czatu Twitch CLI nie obsługuje, więc testujemy go na prawdziwym kanale.
4. ✅ Informacyjne komendy z czatu, bez zmian w grze (`channel.chat.message`, uprawnienie `user:read:chat`; odpowiedzi na czacie wymagają `user:write:chat`). Działa bez statusu Affiliate. Od 2026-10-05: `!tf_pomoc`, `!tf_mapa`, `!tf_majatek`, `!tf_miasto`, `!tf_zaklad`, `!tf_linia`, `!tf_ranking`, `!tf_wolne`, `!tf_data`, `!tf_firma`, `!tf_najlepsza`/`!tf_najgorsza [linia|firma]`, z cooldownem na widza i na komendę ([opis](../middle_app/README.md#komendy-z-czatu)). Na prawdziwym czacie jeszcze niesprawdzone.
5. ✅ Punkty kanału: jedyne źródło zmian w grze. Kupno miasta (wcześniej „adopcja”), boost miasta, boost przemysłu, stop linii (od razu) i linia czeka na stacjach ([opis](../middle_app/README.md#nagrody-za-punkty-kanału)), z tworzeniem nagród, rozliczaniem wykupień (FULFILLED/CANCELED) i auto-pauzą. Sprawdzone w grze przez „Symuluj nagrodę” (2026-10-04); czas efektów w miesiącach gry, bo dzień gry trwa 2–3 s. Na żywo na Twitchu jeszcze nie (kanał bez Affiliate). Koszty, czasy i mnożniki są startowe, do zbalansowania na streamie.
6. Nowe miasto i nowy zakład za punkty kanału: miejsce i nazwa z mapy na stronie (przycisk 🏗 → kod do wklejenia w nagrodę, bo Twitch nie ma linku, który wypełnia tekst nagrody), Tf3Bridge sprawdza miejsce, mod buduje jak edytor mapy gry (`create.lua`). Jeszcze niesprawdzone w grze.
7. ✅ Efekty na stronie (znaczki na liście, szczegóły z datą końca i kupującym) i odpowiedź dla widza na czacie po każdym wykupieniu (efekt z datą końca w grze i linkiem do obiektu na gin.estfosoft.com albo powód zwrotu punktów).
8. Sabotaże i paliwo (2026-10-05): strajk w zakładzie, nalot skarbówki (wszystkie zakłady widza po nicku), paliwo premium, chrzczone paliwo, sabotaż hamulców (pojazdy znikają, kwota ze sprzedaży zabrana w księgach). Sabotować można tylko obiekty innych widzów. Produktywność i prędkość pilnuje wspólny sterownik, więc efekty na jednym obiekcie się mnożą. Jeszcze niesprawdzone w grze ([opis](../lua_mod/README.md#efekty-czasowe-effectslua)).
9. Nagrody wykonywane przez streamera (2026-10-05): bomba, linia, natura, pojazd drogowy, pojazd specjalny. Bez efektu w modzie; Tf3Bridge je tworzy i włącza/wyłącza z grą, wykupienia zostają w kolejce Twitcha dla streamera ([opis](../middle_app/README.md#nagrody-za-punkty-kanału)).
10. Kolejne akcje, feed i ranking na stronie.

## Źródła

- Twitch API reference, Channel Points: https://dev.twitch.tv/docs/api/reference/#create-custom-rewards
- EventSub subscription types: https://dev.twitch.tv/docs/eventsub/eventsub-subscription-types/
- Komendy gry: `E:/SteamLibrary/steamapps/common/Transport Fever 3/api/tealdef/api/cmd.d.tl`
