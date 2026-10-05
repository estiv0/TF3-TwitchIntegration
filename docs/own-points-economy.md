# Własna waluta zamiast punktów kanału (pomysł)

**Status:** analiza (2026-10-05), bez zmian w kodzie. Wracamy do tematu później. Do decyzji: pytania na końcu.

Cel: odejść od punktów kanału Twitcha, bo nie da się ustawić, ile punktów dostają widzowie. Zamiast tego własna waluta, której przychód zależy od majątku widza w grze. Ma to dać widzom powód do rywalizacji, sabotaży i sojuszy przy rozwijaniu swoich miast.

## Co mamy dziś

| Warstwa | Stan |
|---|---|
| Mod | Eksport `dynamic` / `network` / `terrain`. Kolejka komend (`adopt`, `effect`, `wreck`, `createTown`, `createIndustry`, `notify`). Efekty czasowe ze wspólnym sterownikiem mnożników, zapisywane w save'ie. |
| Tf3Bridge | EventSub (czat i wykupienia). 13 nagród z akcją w grze i 5 wykonywanych przez streamera. `TargetResolver`, zwroty przez CANCELED, auto-pauza nagród, odpowiedzi na czacie, `!tf_majatek`. |
| Własność | Tylko w nazwie obiektu w grze (`Nazwa \| widz`). Bez rejestru i bez `user_id`. |
| Strona | Mapa, zakładka „Właściciele”, efekty przy obiektach, kody budowy. |
| Twitch | Kanał nie ma statusu Affiliate, więc punkty kanału i tak nie działają na żywo. |

Ostatni wiersz przemawia za pomysłem: własna waluta działa bez Affiliate.

## Problem: kto właściwie zarabia

W TF3 pieniądze ma tylko firma streamera. Miasta i zakłady nie mają zysku. Z tego, co eksportujemy:

- **linie:** prawdziwy zysk (`profit12m`, `profitPrevYear` z `calculateBalance`), ale widzowie nie mogą dziś kupić linii;
- **zakłady:** produkcja (`producedYear`, `year.produced`), bez pieniędzy;
- **miasta:** wielkość i zaspokojenie potrzeb (`supply`), bez pieniędzy.

„Zarobki właściciela” trzeba więc zdefiniować samemu jako wynik gospodarczy jego obiektów.

**Konflikt interesów:** zarobek zakładu zależy prawie w całości od tego, czy streamer podłączył go linią. Streamer wybiera więc, kto zarabia. Złagodzenie: kontrakty na linie (niżej).

## Propozycja: pula na miesiąc gry zamiast kursu wymiany

Kurs typu „$1000 w grze = 1 punkt” odpada. Pieniądze w TF rosną wykładniczo, więc w późnej grze byłaby hiperinflacja.

Zamiast tego **co miesiąc gry rozdajemy stałą pulę P** (np. 1000 pkt, ustawiana w oknie jak dziś koszty nagród):
- sama się normalizuje, bo liczy się udział w gospodarce, a nie kwota;
- streamer ma jedno pokrętło do tempa, w jakim punkty trafiają do gospodarki;
- to gra o sumie zerowej: strata rywala to mój zysk, więc sabotaże i sojusze mają sens same z siebie.

### Podział puli (wartości startowe)

1. **30% dla obecnych**, po równo. Obecny = pisał na czacie w ostatnich ~30 min albo jest na liście Get Chatters (scope `moderator:read:chatters`). Nowy widz od razu coś dostaje i może kupić pierwsze miasto.
2. **70% gospodarczej puli**, osobno dla każdej kategorii, np. miasta 25%, zakłady 25%, linie 20%. Dzięki osobnym pulom nie trzeba przeliczać mieszkańców, ton i dolarów na jedną jednostkę. W kategorii liczy się tylko udział widza.

### Wynik obiektu w miesiącu (przyrosty, nie poziom)

| Obiekt | Wynik | Dlaczego |
|---|---|---|
| Miasto | przyrost mieszkańców (≥ 0) + mały składnik `mieszkańcy × średnie zaspokojenie` | Sam poziom nagradza tego, kto kupił duże miasto na starcie |
| Zakład | jednostki wyprodukowane w miesiącu × waga szczebla towaru (surowiec 1, półprodukt 2, wyrób 3) | Nagradza obsłużone łańcuchy, nie samo posiadanie |
| Linia | zysk z miesiąca (różnica `profit12m` albo `calculateBalance` za miesiąc), strata = 0 | Jedyny prawdziwy pieniądz |

Dzięki przyrostom sabotaż działa od razu (strajk zeruje produkcję w tym miesiącu), a nie dopiero po latach.

### Żeby było fair (bez kuli śnieżnej)

- **Malejące przychody:** wypłata ∝ wynik^0,6. Dwa razy większe imperium daje około 1,5 raza więcej.
- **Limit:** jeden widz najwyżej ~25% puli gospodarczej.
- **Utrzymanie:** każdy obiekt kosztuje co miesiąc kilka punktów. Gdy widza nie stać, obiekt wraca na rynek. Bez tego nieobecni latami trzymają najlepsze miasta. To też główny odpływ punktów z gospodarki.
- **Ochrona nowych:** obiekt kupiony w ostatnich ~3 miesiącach gry nie może być celem sabotażu.

## Rywalizacja: sabotaże i sojusze

- **Koszt sabotażu zależy od celu:** atak na lidera (duży udział) tańszy, na słabego droższy. To mechanizm typu „niebieska skorupa”, który hamuje lidera.
- **Odporność po ataku:** ten sam sabotaż na ten sam cel nie działa przez X miesięcy, żeby nikogo nie dało się zgnębić na stałe.
- **Sojusz** (`!tf_sojusz @x`, za zgodą obu stron): członek dostaje np. 10% dywidendy sojuszników, sabotaż sojusznika jest zablokowany. Zerwanie ma cooldown, ale zdrada jest możliwa.
- **Przelewy** (`!tf_przelej`) z podatkiem ~20%, żeby farmienie na multikontach się nie opłacało.
- **Kontrakty na linie:** widz płaci za „zamówienie linii” do swojego zakładu (dziś ręczna nagroda „TF3: linia”). Podłączenie staje się usługą, a nie przysługą streamera.

## Ryzyka

1. **Tożsamość po nazwie wyświetlanej.** Dziś zmiana nicka na Twitchu = utrata majątku. Przy własnej walucie potrzebny jest rejestr po `user_id` (w [owners-tab.md](owners-tab.md) „Odłożone: rejestr właścicieli”).
2. **Wczytanie starszego save'a.** Portfel jest poza grą, majątek w save'ie. Wypłaty muszą pamiętać ostatni opłacony miesiąc gry dla danego zapisu. Po cofnięciu daty nie płacimy ponownie, dopóki gra nie dojdzie do poprzedniego maksimum. Punkty wydane na rzeczy, które zniknęły razem z save'em, przepadają, tak jak dziś ([game-time-and-pause.md](game-time-and-pause.md)).
3. **Zasada „grę zmieniają tylko punkty kanału”** (`CommandSource`). Jej sens, czyli kontrola streamera nad kosztami i limitami, zostaje. Zmienia się źródło: nowy `CommandSource.Wallet`, a komendy czatu typu `!tf_kup` trafiają do kolejki dopiero po obciążeniu portfela. Cooldowny i limity musi pilnować Tf3Bridge, bo dziś robi to Twitch.
4. **Multikonta** zbierające pulę za obecność: pulę za obecność dostają tylko konta z minimalną aktywnością na czacie, ewentualnie z minimalnym stażem obserwowania kanału.
5. **Co tracimy:** natywne okno nagród Twitcha. Widz musi znać komendy, więc potrzebna jest ściąga na stronie i komenda `!tf_sklep`.

## Technicznie (bez nowej infrastruktury w Azure)

- **Tf3Bridge:** `Economy/Ledger` (lokalny SQLite albo JSON, tylko dopisywane transakcje; zwrot = transakcja odwrotna). `PayoutService` uruchamiany zmianą miesiąca w `dynamic.json`. Śledzenie obecności z czatu i Get Chatters. Komendy wydające punkty przechodzą tę samą ścieżkę co dziś `RewardService` (resolver, kolejka, wynik).
- **Mod:** prawie bez zmian. Ewentualnie miesięczne statystyki per obiekt, żeby Tf3Bridge nie liczył różnic sam.
- **Strona:** `latest/economy.json` z rankingiem, portfelami, sojuszami i historią wypłat.
- **Opcjonalnie, z Affiliate:** jedna nagroda za punkty kanału „TF3: kantor”, która zamienia punkty kanału na naszą walutę. Punkty kanału zostają wtedy wejściem do gry, a nie całą ekonomią.

## Pytania otwarte

1. Pula tylko dla obecnych na streamie, czy majątek „pracuje” też pod nieobecność (z utrzymaniem jako hamulcem)?
2. Dodać kupno linii? To jedyny prawdziwy zysk, ale linie buduje i prowadzi streamer.
3. Znieść punkty kanału całkiem, czy zostawić je jako kantor na później?
