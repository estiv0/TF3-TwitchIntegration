# Dokumentacja projektu TF3 → Twitch

| Plik | Zawartość |
|---|---|
| [tf3-api-findings.md](tf3-api-findings.md) | Czym TF3 różni się od TF2 (struktura moda, game script, API). Na tej podstawie trzeba przebudować `lua_mod`. |
| [mod-known-issues.md](mod-known-issues.md) | Błędy i ryzyka w obecnym szkielecie moda (do naprawy później). |
| [reactive-dynamic-export.md](reactive-dynamic-export.md) | Odłożona propozycja: różnicowy i reaktywny eksport danych dynamicznych (zdarzenia gry, `dynamicDelta`, strumień zdarzeń, stacje). |
| [export-format-performance.md](export-format-performance.md) | Format eksportu a wydajność: gdzie jest koszt, propozycje (tabele Lua zamiast JSON, format kolumnowy, mniejsza precyzja) i pomiar `bench.lua`. |
| [architecture.md](architecture.md) | Architektura całości: mod → middle app → Azure → strona, plus koszty i opcje rozszerzenia Twitcha. |
| [twitch-channel-points.md](twitch-channel-points.md) | Analiza integracji punktów kanału Twitcha: API (Helix, EventSub), przepływ Tf3Bridge → mod, pomysły na nagrody i komendy gry, ryzyka. |

Kod:
- [../lua_mod/](../lua_mod/README.md): mod do gry;
- [../middle_app/](../middle_app/README.md): Tf3Bridge, .NET 9, działa lokalnie obok gry;
- [../web/](../web/README.md): strona, statyczna, docelowo na Azure Static Web Apps.

Źródła:
- Dokumentacja API: https://wiki.transportfever3.com/script-doc/api/cmd.html (oraz `engine`, `engine/util`, `engine/system`, `type`)
- Wiki moddingu: https://wiki.transportfever3.com/doku.php?id=modding
- Definicje Teal w instalacji gry: `E:/SteamLibrary/steamapps/common/Transport Fever 3/api/tealdef/` i `base/tealdef/`
- Przykładowe game scripty z gry: `base/content/terrain/reforestation.gs.lua` + `reforestation.script.tl`
