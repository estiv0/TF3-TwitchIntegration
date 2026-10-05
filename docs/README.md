# Dokumentacja projektu TF3 → Twitch

| Plik | Zawartość |
|---|---|
| [tf3-api-findings.md](tf3-api-findings.md) | Czym TF3 różni się od TF2 (struktura moda, game script, API). Na tej podstawie trzeba przebudować `lua_mod`. |
| [mod-known-issues.md](mod-known-issues.md) | Błędy i ryzyka w obecnym szkielecie moda (do naprawy później). |
| [reactive-dynamic-export.md](reactive-dynamic-export.md) | Odłożona propozycja: różnicowy i reaktywny eksport danych dynamicznych (zdarzenia gry, `dynamicDelta`, strumień zdarzeń, stacje). |
| [game-time-and-pause.md](game-time-and-pause.md) | Czas gry: co się dzieje na pauzie i po cofnięciu daty, opcja efektów w czasie rzeczywistym, plan eksportu `dynamic` na pauzie i pytania otwarte. |
| [export-format-performance.md](export-format-performance.md) | Format eksportu a wydajność: gdzie jest koszt, propozycje (tabele Lua zamiast JSON, format kolumnowy, mniejsza precyzja) i pomiar `bench.lua`. |
| [architecture.md](architecture.md) | Architektura całości: mod → middle app → Azure → strona, plus koszty i opcje rozszerzenia Twitcha. |
| [api-ideas.md](api-ideas.md) | Przegląd API gry pod kątem nowych nagród i danych: zdarzenia gry (fajerwerki, marketing), haki symulacji (ceny biletów), kamera, dźwięk, statystyki. Nic nie przetestowane. |
| [owners-tab.md](owners-tab.md) | Zakładka „Właściciele”: cały majątek widza (miasta, zakłady, stacje, linie) w jednym miejscu, główne miasto, link `#owner=`; dalej mapa i komenda `!tf_majatek`. |
| [twitch-channel-points.md](twitch-channel-points.md) | Analiza integracji punktów kanału Twitcha: API (Helix, EventSub), przepływ Tf3Bridge → mod, pomysły na nagrody i komendy gry, ryzyka. |
| [own-points-economy.md](own-points-economy.md) | Pomysł na później: własna waluta zamiast punktów kanału, pula na miesiąc gry dzielona według majątku widzów, sabotaże i sojusze, ryzyka. |

Kod:
- [../lua_mod/](../lua_mod/README.md): mod do gry;
- [../middle_app/](../middle_app/README.md): Tf3Bridge, .NET 9, działa lokalnie obok gry;
- [../web/](../web/README.md): strona, statyczna, docelowo na Azure Static Web Apps.

Źródła:
- Dokumentacja API: https://wiki.transportfever3.com/script-doc/api/cmd.html (oraz `engine`, `engine/util`, `engine/system`, `type`)
- Wiki moddingu: https://wiki.transportfever3.com/doku.php?id=modding
- Definicje Teal w instalacji gry: `E:/SteamLibrary/steamapps/common/Transport Fever 3/api/tealdef/` i `base/tealdef/`
- Przykładowe game scripty z gry: `base/content/terrain/reforestation.gs.lua` + `reforestation.script.tl`
