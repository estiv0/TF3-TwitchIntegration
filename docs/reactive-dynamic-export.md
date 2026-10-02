# Propozycja: reaktywny eksport danych dynamicznych

**Status:** pomysł odłożony na później (2026-10-02). Na razie zostaje obecny eksport: pełny snapshot `dynamic` co 2 dni gry, nie częściej niż co 10 s.

Cel: tak jak przy sieci (pełny zrzut raz, potem `networkDelta` po zdarzeniach budowy), ograniczyć wysyłane dane do tego, co się faktycznie zmieniło, i szybciej reagować na ważne zmiany.

## Jakie zdarzenia daje gra

Lista pochodzi z `state:subscribeToEvent(...)` w skryptach gry (`base/content`). Zdarzenia trafiają do `handleEvent` w wątkach symulacji, więc zapis do pliku musi i tak iść przez wspólny stan i `guiUpdate`, tak samo jak `netVersion` przy sieci.

| Zdarzenie | Co mówi | Zastosowanie |
|---|---|---|
| `onPostBuildProposal` | zastosowano budowę lub wyburzenie (gracz albo miasto, param `[4] = playerInitiated`) | sieć (już używane), nowe stacje i zajezdnie |
| `ConstructionCompleted` | konstrukcja ukończona | natychmiastowe odświeżenie stacji i przemysłu |
| `OnArriveAtStop` | pojazd dojechał na przystanek (`vehicleEntity`, `lineEntity`, `stopIndex`, `lastStopIndex`) | strumień zdarzeń, ruch na żywo |
| `OnCargoLoaded` / `OnCargoUnloaded` | załadunek i rozładunek | strumień zdarzeń („pociąg X przywiózł 120 t węgla”) |
| `OnLevelUp` | awans miasta (`townEntity`, `newLevel`) | odświeżenie miasta |
| `DeliveryRatingChanged`, `NewCargoTypeDemand`, `OnHappyChange` | zmiany ocen i popytu miast | odświeżenie miasta |

**Zdarzeń nie znaleziono** dla tworzenia i edycji linii, kupna i sprzedaży pojazdów, produkcji fabryk, stanu magazynów, salda ani zanieczyszczenia. Część z tych wartości zmienia się ciągle, więc zdarzenie i tak niewiele by dało.

## Proponowane podejście: hybryda

### 1. Odpytywanie z różnicami (`dynamicDelta`)
- Mod nadal zbiera dane co ~10 s, bo to tanie: zbieranie ok. 5 ms, kodowanie ok. 6 ms.
- Każda encja (miasto, przemysł, linia, pojazd) jest kodowana osobno do mapy `id → JSON`, jak wiersze krawędzi w sieci.
- Pełny `dynamic` powstaje raz na sesję. Kolejne pliki to `dynamicDelta`, a wspólny licznik `seq` daje `base = seq - 1`:
  ```
  { v, kind:"dynamicDelta", session, seq, base, gameTime, date, player,
    changed: { towns:[...], industries:[...], lines:[...], vehicles:[...] },
    removed: { towns:[ids], industries:[ids], lines:[ids], vehicles:[ids] } }
  ```
- Działa dla wszystkiego, także dla linii i pojazdów, które nie mają zdarzeń.
- Pliki zrobią się małe, bo większość encji między odczytami się nie zmienia. Żeby różnice nie powstawały przez szum, wartości ciągłe (np. `pollution`, `noise`, `upgrade`) warto mocniej zaokrąglać.

### 2. Zdarzenia przyspieszają odświeżenie
- `handleEvent` podbija we wspólnym stanie licznik, np. `dynVersion`, po `ConstructionCompleted`, `OnLevelUp`, `DeliveryRatingChanged` i `onPostBuildProposal`.
- `guiUpdate` po zmianie licznika robi odczyt od razu (z krótkim odczekaniem), zamiast czekać do następnego cyklu 10 s.

### 3. Strumień zdarzeń (osobna funkcja, później)
- `OnArriveAtStop` i `OnCargoLoaded`/`OnCargoUnloaded` trafiałyby do bufora we wspólnym stanie. `guiUpdate` co kilka sekund opróżniałby bufor do pliku `events_<session>_<seq>`.
- Uwaga na wydajność: przyjazdów jest dużo, więc bufor we wspólnym stanie musi być ograniczony (np. ostatnie N zdarzeń), a `state:set` w każdym zdarzeniu może być kosztowne. Trzeba to zmierzyć.

### Przy okazji: stacje
Stacje nie są jeszcze eksportowane. Do dodania:
- nazwa i pozycja grupy stacji;
- obsługiwane linie, z `api.engine.system.lineSystem.getLineStops(stationGroup)` / `getLinesForStationGroup`;
- miasto, z `stationSystem.getTown`.

## Kolejność wdrożenia (gdy wrócimy do tematu)
1. Potwierdzić w grze, że `onPostBuildProposal` i `networkDelta` działają, bo to ten sam mechanizm.
2. Dodać stacje do snapshotu `dynamic`.
3. Wdrożyć `dynamicDelta` (punkt 1), a potem przyspieszanie zdarzeniami (punkt 2).
4. Strumień zdarzeń (punkt 3), jeśli będzie potrzebny na stronie lub streamie.

Po stronie odbiorcy (middle app lub serwer) trzeba dodać składanie stanu z pełnego zrzutu i różnic. Przy brakującym `seq` odbiorca czeka na kolejny pełny zrzut.
