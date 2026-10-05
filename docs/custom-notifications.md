# Własne powiadomienia w grze

Stan: zbadane w plikach gry 2026-10-04; powiadomienie po nagrodzie za punkty kanału
**zaimplementowane 2026-10-05, jeszcze nie sprawdzone w grze** (sekcja
[Projekt](#projekt-powiadomienie-po-nagrodzie-za-punkty-kanału) na końcu, lista „Do sprawdzenia w grze” niżej nadal aktualna).

Cel: pokazywać w grze powiadomienia takie jak „nowa fabryka” czy „subwencja”, np.
„xyz założył miasto Foo” po wykonaniu nagrody za punkty kanału.
Powiązane: [custom-subsidies.md](custom-subsidies.md).

## Jak to robi gra

Wzorce (ścieżki względem `E:/SteamLibrary/steamapps/common/Transport Fever 3`):

- `base/content/landmarks/landmarks_notification*.res.lua` i `*.script.tl`
- `mods/release/urbangames_campaign_mission_03/content/notifications.zip` (`truckdespawn.script.tl`)
  i wysyłka w `mission.zip` → `mission/tasks/fatal_maintenance/fatal_maintenance.tl`
- Implementacja systemu: `base/content/game_mechanics.zip` → `game_mechanics/notifications/`
- Typy: `base/tealdef/game_mechanics/notifications/notifications.d.tl`

### 1. Rejestracja typu: `xyz.res.lua`

```lua
function data()
	return {
		type = "notification",
		data = {
			guiType = "Info", -- Info | Caution | Problem | Opportunity | Achievement
			label = _("Twitch"), -- nazwa w filtrze dziennika powiadomień
			initiallyIgnoredType = false,
		},
	}
end
```

Gra szuka go po nazwie typu z zamienionym `.script` na `.res` (`api.res.genericRep.find`).
Bez tego pliku `guiType` będzie `"Unknown"`.

### 2. Wygląd: `xyz.script` z `useDataState`

```lua
local notification_util = ug_require "::/game_mechanics/notifications/notification_util.tl"

local data = {}
data.useDataState = function(params)
	return {
		title = "Twitch: " .. params.viewer,
		description = params.text,
		icon = "::/game_mechanics/notifications/gui/icons/building_town_arrow_up.tga",
		-- opcjonalnie:
		-- soundOnMount = { "<plik dźwięku>" },
		-- progress = { percentage = 0.5, text = "..." },
		wouldClick = params.entity and notification_util.makeDefaultWouldClick({ params.entity }) or nil,
		onClick = params.entity and notification_util.makeDefaultOnClick({ params.entity }) or nil,
	}
end
return data
```

`params.entity` to `EntityAndRevision` (patrz `entity_util.makeEntityAndRevision` w grze),
kliknięcie przenosi kamerę do encji.

### 3. Wysłanie

```lua
api.cmd.sendCommand(api.cmd.makeScriptingSendEventCmd("", "Notifications", "add", {
	type = "estiv0_tf3export::/tf3export/twitch_notification.script",
	params = { viewer = "nick", text = "...", entity = ... },
	autoDismissDuration = 10000, -- ms; to „ukrycie” okienka, wpis zostaje w dzienniku
}))
```

U nas pasuje to do `lua_mod/content/tf3export/commands.lua`, które już wysyła komendy
z `guiUpdate` – np. po zakończeniu `createTown` / `createIndustry` / `adopt`.

## Ikony gry do użycia

`::/game_mechanics/notifications/gui/icons/<nazwa>.tga`, m.in.:
`building_industry_plus`, `building_town_arrow_up`, `company_rank_up`, `marketing`,
`relation_cargo_plus`, `building_temple`. Można dodać własną `.tga` (np. logo Twitcha) –
kampania ma pary `nazwa@2x.tga` i `nazwa_32@2x.tga`.

## Do sprawdzenia w grze

- ~~Czy skrypt typu powiadomienia działa jako `.script.lua`~~ – sprawdzone 2026-10-05: działa, ale
  plik musi mieć **globalne `data()`** zwracające tabelę z `useDataState` (jak `tf3export.script.lua`).
  Z `return data` na końcu (styl `.tl`) gra rzuca „function data() not defined” w `NotificationPopups`,
  a cały interfejs gry pada („Script component root failed”). Typ (`.res.lua`) i komenda `notify`
  zadziałały – błąd był dopiero przy rysowaniu okienka.
- Czy `_()` i `ug_require` notification_util działają w naszym pliku Lua.
- Nowe pliki trzeba dopisać do `lua_mod/_content.json`.
- Powiadomienia zapisują się w save'ie – co się stanie po wczytaniu gry bez naszego moda
  (brak skryptu typu): ignorowane czy błąd?

---

## Projekt: powiadomienie po nagrodzie za punkty kanału

Cel: gdy widz wykupi nagrodę wpływającą na grę i zostanie ona **poprawnie wykonana i rozliczona**
(Twitch przyjął FULFILLED, czyli punkty zostały pobrane), w grze wyskakuje powiadomienie
z logo Twitcha i opisem, co się stało. Odmowy i zwroty punktów **nie** dają powiadomienia
(widz i tak dostaje odpowiedź na czacie).

### Przepływ

```
 Twitch: widz wykupuje nagrodę
   ▼
 RewardService.Process ── komenda (adopt / effect / createTown / ...) ──► queue.lua ──► mod
   ▲                                                                                   │
   └──── results.lua: status ok (+ entity dla create*) ◄──────────────────────────────┘
   │
 OnCommandCompleted → Complete → CloseAsync: SetRedemptionStatusAsync(FULFILLED)
   │  dopiero gdy Twitch odpowie sukcesem:
   ▼
 komenda notify { title, text, entity? } ──► queue.lua ──► mod: Notifications "add"
                                                            ▼
                                     okienko w grze (ikona Twitcha, kolor „Opportunity”)
```

Dlaczego osobna komenda `notify` wysyłana przez Tf3Bridge, a nie powiadomienie pokazywane przez
mod od razu po `ok`:

- „Punkty pobrane” wie tylko Tf3Bridge (odpowiedź Twitcha na FULFILLED). Gdy rozliczenie się nie
  uda (np. wygasły token), wykupienie zostaje w kolejce na Twitchu i streamer rozlicza je ręcznie;
  wtedy nie ogłaszamy w grze czegoś, co może jeszcze skończyć się zwrotem.
- Teksty są już po polsku w C# (`RewardMessages`); mod tylko wyświetla gotowy tytuł i opis,
  więc nie dublujemy tekstów w Lua i nowa nagroda nie wymaga zmian w modzie.
- `notify` to ogólna komenda: później można jej użyć do innych zdarzeń (koniec efektu, subwencja,
  wiadomość od streamera) bez nowego rodzaju komendy.

Koszt: powiadomienie pojawia się ~1–2 s po wykonaniu (jeden obieg kolejki co `commandsEveryWallSec`
plus odpowiedź Twitcha). Przy symulacji z okna Tf3Bridge (brak prawdziwego wykupienia) `notify`
idzie od razu po `ok`, z dopiskiem `[TEST]` w tytule.

### Komenda `notify` (Tf3Bridge → mod)

```lua
{ id = "...", kind = "notify", created = <unix s>,
  title = "Twitch: Ala",                       -- ≤ 100 znaków
  text = "Ala kupiła miasto Kraków.",          -- ≤ 300 znaków
  entity = 12345,                              -- opcjonalnie: kliknięcie przenosi kamerę
  dismissMs = 10000 }                          -- opcjonalnie: po ilu ms okienko się chowa
```

- C#: `ModCommand.Notify(title, text, entity?, dismissMs, source, user)`. Teksty przechodzą
  przez `ModCommandLua.Str` (już escapuje znaki od widzów); wcześniej obcinamy długość
  i zamieniamy nowe linie na spacje.
- `notify` **nie** trafia do `_pending` w `RewardService`, bo jego wynik niczego nie rozlicza.
  Błąd tylko logujemy (`Game notification not shown: ...`).
- Mod: `KINDS.notify` w `commands.lua` wysyła zdarzenie i zwraca `verify = function() return true end`
  (stanu powiadomień nie da się tanio sprawdzić z wątku GUI). Komendy starsze niż `MAX_AGE_SEC`
  są odrzucane jak inne – to dobrze, stare powiadomienie po restarcie gry nie ma sensu.

### Id nowej encji dla `createTown` / `createIndustry`

**Zrobione 2026-10-05** (niesprawdzone jeszcze w grze). Bridge nie znał id nowego miasta/zakładu,
a bez niego kliknięcie w powiadomienie nic nie robi. Wynik komendy ma teraz `entity`:

- `create.lua`: `verify` po znalezieniu encji zapisuje ją w `info.entity`
  (tak jak efekty oddają `until`); `commands.lua: finish` przepisuje `info.entity` do wyniku.
- C#: `ModCommandResult.Entity` (`long?`). Link w odpowiedzi na czacie dla `CreateTown` /
  `CreateIndustry` to już `#town=<id>` / `#industry=<id>`; pinezka `#pin=x,y&note=...` zostaje
  tylko jako zapas, gdy mod nie poda id (starsza wersja moda w grze).
- Strona (`web/js/main.js`): link do obiektu, którego nie ma jeszcze w eksporcie, czeka do
  6 kolejnych porcji danych, zanim zostanie porzucony (świeżo zbudowane miasto trafia do
  eksportu z opóźnieniem).
- Do zrobienia przy `notify`: dla pozostałych nagród `RewardService` bierze `target.Entity`,
  które ma już w `Process` (trzeba je dopisać do rekordu `Pending`).

### Teksty (`RewardMessages.Notification`)

Tytuł: `Twitch: <widz>`. Opis według rodzaju nagrody:

| Nagroda | Opis |
|---|---|
| AdoptTown | `<widz> kupił(a) miasto <cel>.` |
| BuyIndustry | `<widz> kupił(a) zakład <cel>.` |
| BoostTown | `<widz> przyspieszył(a) wzrost miasta <cel> o +X% do <data>.` |
| BoostIndustry | `<widz> zwiększył(a) produkcję zakładu <cel> o +X% do <data>.` |
| StopLine | `<widz> zatrzymał(a) linię <cel> do <data>.` |
| HoldLine | `<widz> kazał(a) pojazdom linii <cel> czekać na przystankach do <data>.` |
| CreateTown | `<widz> założył(a) miasto <cel>.` |
| CreateIndustry | `<widz> zbudował(a) zakład <cel>.` |

Formy „kupił(a)”, bo płci nie zgadujemy po nicku. `<data>` z `ModCommandResult.Until`
(`RewardMessages.Date`), a gdy jej brak: „na N mies.”. Bez linku do strony (w grze nie da się go kliknąć).

### Pliki moda

```
lua_mod/content/tf3export/
  twitch_notification.res.lua      -- rejestracja typu: guiType "Opportunity" (fioletowy akcent), label "Twitch"
  twitch_notification.script.lua   -- useDataState: title/description/icon/onClick z params
  icons/twitch@2x.tga              -- 48×48, 8-bit grayscale (maska)
  icons/twitch_32@2x.tga           -- 64×64, 8-bit grayscale (maska)
infra/twitch_logo.png              -- źródło ikony
infra/make_icon.ps1                -- logo -> TGA
```

`pack.ps1` wpisuje do `_content.json` także pliki `.tga` (jak mody z gry), a Tf3Bridge osadza je
w exe razem z resztą moda (`ModInstaller`).

`KINDS.notify` w `commands.lua` wysyła z wątku GUI (tak jak `effects.lua: persist`):

```lua
api.cmd.sendCommand(api.cmd.makeScriptingSendEventCmd("", "Notifications", "add", {
	type = "estiv0_tf3export::/tf3export/twitch_notification.script",
	params = { title = ..., text = ..., entity = entityAndRevision(c.entity) },
	autoDismissDuration = c.dismissMs or 10000,
}))
```

`entityAndRevision` odtwarza `entity_util.makeEntityAndRevision` z gry
(`{ entity, revision = { num = {…} } }` z `api.engine.getRevision`); gdy encji nie ma albo API
zawiedzie, powiadomienie idzie bez `entity` (bez kliknięcia). Teksty mod przycina jeszcze raz
w bajtach (400 / 1200), nie rozcinając znaków UTF-8; właściwe limity (100 / 300 znaków) stosuje Tf3Bridge.

### Ikona (logo Twitcha)

Ikony powiadomień w grze to **maski w skali szarości**, nie kolorowe obrazki: TGA typ 3
(8-bit grayscale, wiersze od dołu), biały glif na czarnym, `nazwa@2x.tga` 48×48 i
`nazwa_32@2x.tga` 64×64 (sprawdzone na `building_town_arrow_up*`). Kolor nadaje gra według `guiType`.

- `make_icon.ps1` bierze z logo piksele fioletu (dymek), oczy i wnętrze zostają dziurami;
  marginesy jak w ikonach gry. Po zmianie logo: `pwsh infra/make_icon.ps1`
  (skrypt sam przełącza się na Windows PowerShell 5.1, bo tam `System.Drawing` działa bez kłopotów).
- TGA są w repo (Tf3Bridge osadza mod przy budowaniu, bez `pack.ps1`).
- Do sprawdzenia: czy kolorowe RGBA TGA też się wyświetli (wtedy można dać fioletowe logo 1:1).

### Ustawienia i test (Tf3Bridge)

- `RewardSettings.GameNotifications` (domyślnie włączone): pole w oknie „Nagrody”.
- Okienko chowa się po 10 s (`RewardService.NotificationDismissMs`); wpis zostaje w dzienniku gry.
- Okno testu komend: przycisk „Testowe powiadomienie” (kliknięcie w nie pokazuje wybrany obiekt),
  a symulacja nagrody wysyła powiadomienie z `[TEST]`.

### Przypadki brzegowe

- **Kilka wykupień naraz**: każde dostaje własne powiadomienie; gra sama układa okienka w stos.
- **Powiadomienia gry wstrzymane** (`notificationsState.paused`): gra wtedy nie dodaje wpisu.
  Akceptujemy to; komenda kończy się `ok`, czat i tak działa.
- **Save wczytany bez moda**: powiadomienia są w save'ie, a typ bez skryptu może dać błąd przy
  wczytaniu. Do sprawdzenia w grze; gdyby był błąd, mod przy starcie usuwa („dismiss”) nasze
  wpisy albo rezygnujemy z wpisu w dzienniku.
- **Stara wersja moda w grze**: mod odpowie `unknown kind notify`; Tf3Bridge tylko loguje
  (`Game notification not shown`), bez zwrotu punktów – nagroda jest już rozliczona.

### Testy

- `ModCommandTests`: `Notify` → jedna linia, przycięcie długości, brak `entity`.
- `RewardTests`: `notify` dopiero po udanym FULFILLED; brak przy zwrocie, przy nieudanym
  rozliczeniu (403) i przy wyłączonym `GameNotifications`; `[TEST]` i `entity` z wyniku przy
  symulacji `createTown`; data końca efektu; tekst dla każdego `RewardKind`.
- W grze (jeszcze nie zrobione): wygląd ikony, kliknięcie → kamera, wpis w dzienniku i filtr
  „Twitch”, zapis i wczytanie save'a.
