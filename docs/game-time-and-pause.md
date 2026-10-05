# Czas gry: pauza, cofnięcie daty i eksport na pauzie

**Status:** analiza i plan (2026-10-05), bez zmian w kodzie. Do decyzji: pytania na końcu.

## Jak to działa dziś

Mod używa dwóch zegarów:
- **czas gry** (`gameTime`, `util.gameTime()`): eksport `dynamic` (co `dynamicEveryDays` dni gry), czas trwania efektów z nagród (`untilTime`, w miesiącach gry), statystyki miesięczne i roczne;
- **czas rzeczywisty** (`os.time`, `wallTime()` w `tf3export.script.lua`): odczyt kolejki komend (co 1 s), limity komend (`MAX_AGE_SEC` 300 s, sprawdzanie skutku 15–45 s), `minDynamicWallSec`, okresowe porównanie sieci, teren, ustawienia.

Tf3Bridge nie używa czasu gry do niczego poza wyświetlaniem dat. Nagrody wyłącza, gdy przez 2 min nie przyjdzie nowy eksport `dynamic` (`WhyInactive` w `RewardService.cs`).

## Pauza (data stoi)

Pauzę wykrywa `guiUpdate`: `gameTime` taki sam jak w poprzedniej klatce.

- **Nagrody działają dalej.** Komendy są czytane według czasu rzeczywistego. Boost, zatrzymanie linii czy budowa wykonują się. Efekt widać dopiero po wznowieniu, bo miasto nie rośnie, a zakład nie produkuje.
- **Efekty nie mijają.** `untilTime` jest w czasie gry, więc efekt na 3 miesiące trwa 3 miesiące gry niezależnie od pauzy. Tak to zaprojektowano (`effects.lua`). Data „do kiedy” na czacie się zgadza.
- **Strona się nie odświeża.** Na pauzie nie ma eksportu `dynamic` ani okresowego porównania sieci. Budowa nadal wywołuje eksport sieci przez `onPostBuildProposal`.
- **Po 2 min pauzy Bridge wyłącza nagrody na Twitchu**, bo nie dostaje danych.
- Komendy nie wygasają przez pauzę, bo ich limity liczą się w czasie rzeczywistym. Warunek: gra przyjmuje komendy na pauzie (budowanie na pauzie działa).

## Cofnięcie daty

Mod wykrywa `now < lastGameTime` i zaczyna nową sesję (`newSession`): pełny eksport z nowym `session`, efekty wczytane od nowa ze stanu save'a.

**Wczytanie starszego save'a** jest spójne:
- efekty, miasta i zakłady kupione po zapisie znikają razem z save'em, więc lista efektów zgadza się z grą;
- właściciele są w nazwach obiektów („Nazwa | widz”), więc zawsze pasują do wczytanego save'a, bez ryzyka pomylenia encji;
- minus: widz traci punkty za nagrodę, której już nie ma, a Bridge ich nie zwraca.

**Cofnięcie daty w trwającej grze** (jeśli się da, np. modem albo konsolą):
- `untilTime` to bezwzględny czas gry, więc efekty **wydłużają się o czas cofnięcia**: cofnięcie o rok zmienia efekt na 3 miesiące w około 15 miesięcy, a data podana na czacie przestaje się zgadzać;
- pasek postępu efektu na stronie spada do 0% (`format.js` przycina wartość do 0..1);
- statystyki „poprzedni rok” przeliczają się same.

**Drobne ryzyko:** w `guiUpdate` `commands.poll` (razem z `effects.update`) wykonuje się **przed** sprawdzeniem nowej sesji. Jeśli stan Lua wątku GUI przetrwa wczytanie innego save'a (niesprawdzone), przez jeden odczyt efekty z poprzedniej gry działają na nowej mapie. Na przykład `maintain` boosta zakładu ustawi produktywność według starej bazy. Poprawka: sprawdzanie `newSession` przed `commands.poll`.

## Opcja: efekty w czasie rzeczywistym (odłożone)

Rozważaliśmy liczenie efektów w minutach zamiast miesięcy gry. Warianty zegara:

1. **Zegar ścienny** (`os.time`): efekt kończy się N minut po zakupie, nawet przy zamkniętej grze. Najprostsze, ale po wczytaniu save'a następnego dnia wszystkie efekty od razu się kończą.
2. **Czas działania gry:** licznik `playSec` rośnie tylko wtedy, gdy gra jest uruchomiona, niezależnie od pauzy i prędkości. Między streamami zegar stoi.
3. **Czas gry bez pauzy:** jak 2, ale pauza też zatrzymuje licznik. Prędkość gry nie ma wpływu. To wariant polecany.

Wdrożenie (2 i 3): `playSec` w `guiUpdate` (przyrosty `os.time`, z pominięciem dużych skoków, np. po uśpieniu komputera) zapisywany do stanu save'a tym samym zdarzeniem co efekty. W efekcie `untilSec` zamiast `untilTime`, w eksporcie `remainingSec`. W Bridge'u `Months` zmienia się na `Minutes` (okno nagród, `{minutes}`, wiadomość na czacie). Efekty ze starych save'ów trzeba raz przeliczyć z miesięcy na minuty. Przy okazji cofnięcie daty przestaje mieć wpływ na efekty.

**Decyzja:** na razie nie. Ważniejsze jest odświeżanie strony na pauzie (niżej).

## Plan: eksport `dynamic` na pauzie

Cel: strona odświeża się także wtedy, gdy czas gry stoi, bo gracz dalej buduje i kupuje pojazdy, a widzowie wykupują nagrody.

1. **Nowe ustawienie `pausedDynamicWallSec`.** Na pauzie eksport co N sekund czasu rzeczywistego. Domyślnie 30, zakres 0–600, 0 = jak dziś (bez eksportu). `minDynamicWallSec` nadal obowiązuje.
   - mod: `cfg` i `SETTINGS` w `tf3export.script.lua`, nowa funkcja `dynamicDue(now, wall, paused)` zamiast obecnego warunku w `guiUpdate`;
   - Bridge: pole `PausedDynamicWallSec` w `ModSettings` (zakres, `Parse`, `ToLua`), wiersz „Na pauzie eksport co: N s (0 = bez eksportu)” w `ModSettingsDialog`, testy w `ModSettingsTests`.
2. **Pewniejsze wykrywanie pauzy:** pauza to `gameTime` bez zmian od co najmniej 2 s (`S.gameTimeWall`, `PAUSE_AFTER_SEC = 2`), a nie jedna klatka bez zmiany. Przy klatkach GUI częstszych niż kroki symulacji obecny warunek może błędnie zgłaszać pauzę w trakcie gry. Z tej flagi korzysta też okresowe porównanie sieci.
3. **Skutek uboczny:** dane płyną na pauzie, więc **nagrody na Twitchu zostają włączone**. Nagroda kupiona na pauzie wykona się i pojawi na stronie, a czas efektu ruszy po wznowieniu.
4. **Dokumentacja do poprawy:** `lua_mod/README.md` („Na pauzie mod nic nie eksportuje”), tabela ustawień w `middle_app/README.md`, punkt 5 w `twitch-channel-points.md`.

Koszt: każdy eksport `dynamic` jest wysyłany do Azure. W trakcie gry to około co 10 s, więc na pauzie przy 30 s wychodzi rzadziej niż zwykle.

### Pytania otwarte

- **Nagrody na pauzie:** zostawić włączone (punkt 3)? Alternatywa: flaga `paused` w eksporcie, a Bridge wyłącza nagrody po 2 min pauzy mimo napływu danych.
- **Szybsze odświeżenie po zdarzeniu:** eksport zaraz po wykonaniu nagrody lub budowie (po `minDynamicWallSec`), żeby nowe miasto czy zakład było na stronie po około 10 s zamiast do 30 s. Pokrywa się z punktem 2 w [reactive-dynamic-export.md](reactive-dynamic-export.md).
- **Pomijanie identycznych eksportów:** na pauzie, bez zmian, treść jest taka sama (poza `seq`). Można jej nie zapisywać i oszczędzić wysyłki, ale Bridge potrzebuje wtedy innego sygnału, że gra działa (np. mały plik heartbeat). Inaczej po 2 min wyłączy nagrody.
