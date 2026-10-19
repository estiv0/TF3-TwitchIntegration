# Subwencje (subsidies) od widzów (pomysł na później)

Stan: zbadane w plikach gry 2026-10-04, **nic nie zaimplementowane, nic nie przetestowane w grze**.

Cel: widz za punkty kanału proponuje subwencję (np. „dostarcz towar X do miasta Y”),
streamer przyjmuje ją albo odrzuca w normalnym oknie subwencji gry.
Powiązane: [custom-notifications.md](custom-notifications.md).

## Źródła w grze

Ścieżki względem `<gra>`:

- `base/content/game_mechanics.zip` → `game_mechanics/subventions/`
  - `subventions.script.tl` – game script (zdarzenia `Subvention`, stan, limity)
  - `subvention_util.tl` – nagrody/kary, karty, powiadomienia
  - `deliver_cargo/`, `deliver_cargo_town/`, `deliver_passengers/`, `deliver_workers/` – typy wbudowane
- Typy: `base/tealdef/game_mechanics/subventions/*.d.tl`
- Własny typ z kampanii: `mods/release/urbangames_campaign_mission_06/content/subventions.zip`
  (`deliver_visitors`, ~300 linii)

## Tworzenie subwencji – gotowe zdarzenie gry

Komentarz w `subventions.script.tl` opisuje je jako „Spawn a specific subvention for missions”:

```lua
api.cmd.sendCommand(api.cmd.makeScriptingSendEventCmd("", "Subvention", "subventionSpawn", {
	name = "::/game_mechanics/subventions/deliver_cargo_town/deliver_cargo_town.res",
	specific = { target = townEntity, cargoTypeId = cargoId, uid = UNIKALNY_NUMER },
}))
```

Gra wywołuje `spawnSpecific(specific)` skryptu danego typu i dodaje wynik do
`proposedSubventions` – gracz widzi propozycję i może ją przyjąć (`onAccept`) albo odrzucić (`onDecline`).

U nas pasuje to do `lua_mod/content/tf3export/commands.lua` jako nowy rodzaj komendy
(obok `createTown`, `createIndustry`, `adopt`), który już wysyła komendy z `guiUpdate`.

### Typy wbudowane (`specific`)

| Typ (`name` = `::/game_mechanics/subventions/<typ>/<typ>.res`) | Zadanie | Parametry |
|---|---|---|
| `deliver_cargo_town` | dostarczyć towar do miasta | `target` (miasto), `cargoTypeId` |
| `deliver_cargo` | zaopatrzyć fabrykę | `target` (fabryka) |
| `deliver_passengers` | połączyć dwa miasta linią pasażerską | `source`, `target` (miasta) |
| `deliver_workers` | dowieźć pracowników do fabryki | `target` (fabryka) |

Wszystkie przyjmują też opcjonalne `uid` i `expireDurationProposed` (jak długo propozycja czeka
na decyzję; `-1` = nie wygasa).

Ilość, czas i nagrody gra losuje sama (`createDeliverCargoState` itd.) z uwzględnieniem
`advancedOptions.subventionRisk` i `subventionFinancialRewardMultiplier`. Nagrody/kary to
`Money`, `Reputation` (miasta) i `TownExperience` (`SubventionBonusMalus`).

Przydatne komendy do konsoli (z komentarzy w grze), np. do testów:

```lua
api.cmd.sendCommand(api.cmd.makeScriptingSendEventCmd("", "Subvention", "_debugSpawn", { name = "::/game_mechanics/subventions/deliver_cargo/deliver_cargo.res" }))
api.cmd.sendCommand(api.cmd.makeScriptingSendEventCmd("", "Subvention", "_debugComplete", { index = 1 }))
api.cmd.sendCommand(api.cmd.makeScriptingSendEventCmd("", "Subvention", "_debugFail", { index = 1 }))
```

## Pułapki

- **`uid` musi być unikalny na cały save.** `spawnSubvention` zapisuje każde użyte `uid` w
  `state.usedUids[typ]` i odrzuca powtórki (w logu „Could not spawn …”). Domyślne `uid` w
  `deliver_cargo_town` to `miasto * 10000 + towar`, więc bez własnego `uid` każda para
  miasto + towar zadziała raz na grę. Trzeba podawać własny numer, który nie zderzy się z
  subwencjami gry (np. ujemny albo bardzo duży, z licznikiem trzymanym po naszej stronie).
- **Brak limitu.** Losowe subwencje gry mają `MaxAvailable = 5`, ale ścieżka `subventionSpawn`
  wygląda na to, że go nie sprawdza – limit/cooldown trzeba zrobić po naszej stronie.
- **`deliver_cargo_town` wymaga popytu miasta na dany towar** – `toDeliver` jest ograniczane
  przez `town_util.getCargoSupplyDemands`, bez popytu wyjdzie 0. Listę towarów do wyboru brać
  z potrzeb miasta (już je eksportujemy w `collectors.lua`, `cargoNeeds`).
- Jeśli encja zniknie (miasto/fabryka), subwencja kończy się jako `InvalidEntity`.
- Gra może być w trybie `setPause` subwencji (np. misje) – sprawdzić, czy to blokuje też nasze.

## Opcja na później: własny typ subwencji

Gdyby wbudowane nie wystarczyły (np. stała nagroda, nick widza w tytule, inne zadanie):

- `xyz.res.lua` z `type = "subvention"` i `data = { scriptFile = "estiv0_tf3export::/…/xyz.script@xyz", icons = { fallback = "…tga" } }`
- skrypt implementujący `SubventionScript<T>` (`subvention.d.tl`): `canSpawn`, `onSpawn`,
  `spawnSpecific`, `isComplete`, `onUpdate`, `shouldDespawn`, `getCardData`,
  `getNotificationData` itd. – wzór: `deliver_visitors` z kampanii 6.
- Uwaga: typ z `canSpawn` = true będzie też losowany przez grę (`trySpawnRandom` bierze
  wszystkie zasoby typu `subvention`), więc dla „tylko od widzów” `canSpawn` powinno zwracać false.

## Do sprawdzenia w grze

- Czy `subventionSpawn` wysłany z `guiUpdate` naszego moda działa (powinien – to zwykłe zdarzenie).
- Czy własne `uid` (ujemne/duże) nie psuje okna subwencji ani powiadomień.
- Jak wygląda propozycja w UI i czy da się dołożyć nick widza (wbudowane typy go nie pokażą –
  można wysłać obok własne powiadomienie z custom-notifications.md).
