# Zakładka „Właściciele”: majątek widzów (2026-10-05)

Stan: **etap 1 zrobiony bez mapy** (lista, szczegóły widza, link `#owner=`). Do zrobienia: podświetlenie
majątku na mapie, potem ewentualnie komenda `!tf_majatek`.

## Problem

Widz może mieć kilka miast, zakładów, stacji i linii. Nie muszą leżeć w jednym miejscu ani być
powiązane z jego „głównym” miastem. Wcześniej własność było widać tylko w szczegółach pojedynczego
obiektu, a szukanie po nazwie widza działało na każdej zakładce osobno.

## Skąd dane

Źródłem prawdy jest nazwa obiektu w grze: `Nazwa | Właściciel` (`adopt`, `createTown`, `createIndustry`),
rozdzielana na stronie w `splitAllOwners` (`web/js/format.js`). Zestawienie liczy się w przeglądarce
z `dynamic.json` (`web/js/owners.js`, `ownersIndex`). **Mod i Tf3Bridge bez zmian.**

| Aktywo | Skąd |
|---|---|
| Miasta | `towns[].owner` |
| Zakłady | `industries[].owner` |
| Stacje | `stations[].owner`; stacja zakładu tego samego widza jest liczona przy zakładzie. Gra nazywa stacje od miasta, także kupionego („Dworzec Szczecin \| estiv0 Zachód”), więc `splitAllOwners` najpierw zamienia w nazwach stacji i przystanków pełne nazwy miast i zakładów z właścicielem na same nazwy („Dworzec Szczecin Zachód”, bez właściciela) |
| Linie | `lines[].owner` |

Decyzje:
- **Główne miasto = największe miasto widza** (`townSize`). Może się zmienić, gdy inne jego miasto urośnie.
- **Tożsamość:** grupujemy bez rozróżniania wielkości liter (`Kinek` = `kinek`); na liście jest najczęstszy
  zapis. Tf3Bridge zapisuje nazwę wyświetlaną widza, więc po zmianie nazwy na Twitchu stare obiekty
  zostają pod starą nazwą. To znane ograniczenie, świadomie go nie rozwiązujemy.

## Etap 1: strona

Zrobione:
- zakładka „Właściciele” z licznikiem widzów; wiersz to nazwa i liczniki obiektów (miasta, zakłady,
  stacje, linie, tylko niezerowe);
- sortowanie: majątek (miasto i zakład ×3, stacja i linia ×1), zarobek linii (suma `stats.profit12m`), nazwa;
- szukanie po nazwie widza i po nazwach jego obiektów („Szczecin” pokaże właściciela Szczecina);
- szczegóły widza w pływającym okienku: główne miasto, podsumowanie, klikalne listy obiektów (★ przy
  głównym mieście); najechanie na obiekt podświetla go na mapie;
- pojazdy na liniach widza: liczba (też na liście), podział według nośnika, w drodze / na przystanku /
  zatrzymane (z `vehicles[]`; bez nich tylko suma `lines[].vehicleCount`); statystyki jego linii razem:
  zysk z 12 miesięcy i z poprzedniego roku, przewiezione, przepustowość (sumy), zapełnienie (średnia ważona
  liczbą pojazdów) i ładunek w pojazdach teraz (suma per ładunek);
- nazwa właściciela w szczegółach każdego obiektu jest linkiem do jego majątku;
- adres `#owner=<klucz>` (nazwa małymi literami, `encodeURIComponent`), działa też otwarty od zera;
- pasek zakładek przewija się w poziomie, bo sześć zakładek nie mieści się w panelu 360 px ani na telefonie.

Do zrobienia: **mapa.** Po zaznaczeniu widza mapa ma podświetlić wszystkie jego obiekty naraz i dopasować
widok do ich obrysu. Dziś `map.js` zna jedno zaznaczenie `{ kind, id }`, więc wybór widza tylko czyści
zaznaczenie mapy (`main.js`, `onPick`). Potrzebne:
- `selected = { kind: "owner", items: [{ kind, id }] }` i obwódki dla każdego elementu (linie cieńsze niż
  przy jednej zaznaczonej linii, żeby przy wielu liniach mapa była czytelna);
- `fit()` do obrysu obiektów (jest już `fit(bbox, maxScale)`).

## Etap 2: komenda na czacie (Tf3Bridge, opcjonalnie)

`!tf_majatek [widz]` odpowiada np. „kinek: 2 miasta (Gdańsk, Sopot), 1 zakład, 3 linie, zysk linii
$1,2 mln, mapa: <link #owner=kinek>”. Bez argumentu pokazuje majątek pytającego. Komenda tylko czyta
ostatni `dynamic.json` (`OwnerName.Split` w `GameObjects.cs`, grupowanie jak w `owners.js`) i **nic nie
wysyła do gry**, zgodnie z zasadą, że grę zmieniają tylko punkty kanału (`CommandSource`). Wymaga czytania
czatu (`user:read:chat`, `user:write:chat`), patrz `twitch-channel-points.md`, punkt 4. Nasze komendy mają
prefiks `tf_`.

## Odłożone: rejestr właścicieli w Tf3Bridge

Na razie niepotrzebny. Wróćmy do niego, jeśli będzie potrzebna historia zakupów (kiedy i za jaką nagrodę),
„główne miasto” wybierane przez widza albo scalanie obiektów po zmianie nazwy widza (`user_id` z EventSub).
Pomysł: Tf3Bridge prowadzi `owners.json` dopisywany po udanym `RewardOutcome`, a źródłem prawdy o tym,
kto ma co teraz, zostaje nazwa w grze.
