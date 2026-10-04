# Własne powiadomienia w grze (pomysł na później)

Stan: zbadane w plikach gry 2026-10-04, **nic nie zaimplementowane, nic nie przetestowane w grze**.

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

- Czy skrypt typu powiadomienia działa jako `.script.lua` (gra i kampania używają `.script.tl`;
  ładuje go `util.useFn(type .. "@useDataState")`). Nasz `tf3export.script.lua` się ładuje,
  więc powinno działać.
- Czy `_()` i `ug_require` notification_util działają w naszym pliku Lua.
- Nowe pliki trzeba dopisać do `lua_mod/_content.json`.
- Powiadomienia zapisują się w save'ie – co się stanie po wczytaniu gry bez naszego moda
  (brak skryptu typu): ignorowane czy błąd?
