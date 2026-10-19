# Przegląd API TF3: pomysły dla moda (2026-10-04)

Stan: **zbadane tylko w plikach gry, nic nie przetestowane w grze.** Lista uzupełnia
[twitch-channel-points.md](twitch-channel-points.md) (już zrobione nagrody) oraz
[custom-notifications.md](custom-notifications.md) i [custom-subsidies.md](custom-subsidies.md).

Źródła (ścieżki względem `<gra>`):
- `api/tealdef/api/cmd.d.tl`, `api/gui.d.tl`, `api/engine/util.d.tl`, `api/engine/system.d.tl`
- `base/content/game_mechanics.zip` (towns, company, fun_elements, celebrations), `base/content/gui.zip`
- `base/tealdef/scripts/gamescript.d.tl` (typy zdarzeń game scriptu)

Najlepsze tropy to komentarze „Useful console commands” w skryptach gry: twórcy zostawili w nich gotowe wywołania.

## 1. Gotowe zdarzenia gry (`makeScriptingSendEventCmd`): nowe nagrody bez pisania mechaniki

Wszystkie wysyła się jak u nas `subventionSpawn`:
`api.cmd.sendCommand(api.cmd.makeScriptingSendEventCmd("", id, name, param))`.

| id / name | param | Efekt | Pomysł na nagrodę |
|---|---|---|---|
| `fireworks` / `startFireworkForTown` | `{ townEntity = e }` | 2 min fajerwerków nad miastem (to samo co przy awansie miasta) | **„Fajerwerki nad miastem X”**: tania, czysto wizualna, idealna na start |
| `fireworks` / `addFirework` | macierz `MatCols` (pozycja) | pojedyncza rakieta | Fajerwerk nad wskazanym obiektem (stacja, fabryka) |
| `Towns` / `startMarketingCampaign` | `{ entity = town, type = "Small" \| "Medium" \| "Large" \| "Bribery" }` (konsola) albo `{ entity, marketingParams }` (narzędzie gry) | kampania marketingowa: rośnie autorytet miasta, mieszkańcy chętniej jeżdżą liniami (`lineCostFactor`), **nad miastem lata sterowiec reklamowy** (`fun_elements`) | **„Kampania reklamowa w mieście X”** ze sterowcem. `Bribery` jako droższa, „łapówka” |
| `Towns` / `decreaseReputation` | `{ townEntity, amount, reason? }`; `amount > 0` odzyskuje reputację, `< 0` ją obniża | reputacja (autorytet) miasta | „Miasto cię kocha” / „Skandal w mieście” (sabotaż) |
| `Towns` / `addExperiencePercentage` | patrz `subvention_util.tl` | doświadczenie miasta (awans poziomu) | Boost miasta inny niż obecny `makeTownUpdateSizeCmd` |
| `Towns` / `_debugAddExperience` | `{ entity = town, amount = 500 }` | XP miasta (debug) | jak wyżej, prościej |
| `Companies` / `_debugAddExperience` | `{ amount = 100 }` | XP firmy gracza | „Prestiż firmy” |
| `Companies` / `MakeGreen`, `Emissions` / `MakeGreen` | `{ entity = construction }` | „zazielenienie” fabryki (mniej zanieczyszczeń) | **„Eko-fabryka”** dla kupionego zakładu |
| `Towns` / `setTownRatingSensitivity` | `{ townEntity, rating, sensitivity }` | wrażliwość miasta na dany rating | Efekt czasowy „Miasto marudzi / jest wyrozumiałe” |
| `Industries` / `_debugExtendIndustry` | `{ entityId = e }` | rozbudowa zakładu o slot | Alternatywa dla boostu produkcji |
| `GameTime` / `SetMode`, `OnManualGameTimeChanged`, `SkipPhase` | `{ mode }`, wartość, `{}` | pora dnia (jak suwak w pasku gry) | „Noc w grze” – ładne na streamie |
| `CloudCoverage` / `SetMode`, `OnManualCloudCoverageChanged`, `SkipPhase` | `{ weatherMode }`, wartość, `{}` | pogoda (jak w pasku gry) | „Burza / słońce” – pewniejsze niż surowe `makeGameSetCloudCoverageCmd`, bo nie walczy z trybem auto |
| `Subvention` / `subventionSpawn`, `_debugSpawn` | patrz [custom-subsidies.md](custom-subsidies.md) | subwencja | już opisane |

Do sprawdzenia: czy `_debug*` działają w zwykłej grze (nie tylko w trybie debug) i czy zmiany autorytetu i XP nie psują osiągnięć.

## 2. Haki game scriptu, które **zmieniają** symulację (wartość zwracana z `handleEvent`)

Mod może zasubskrybować te zdarzenia (`state:subscribeToEvent(...)`) i zwrócić wynik, który gra zastosuje:

- **`OnCalcTicketPrice`** (id `TransportVehicleSystem` albo `SimEntityAtVehicleSystem`). Param: lista
  `{vehicleEntity, lineEntity, simEntity, stockListEntity?, basePrice, distance}`. Zwrot: tablica mnożników,
  `[0]` działa na wszystkie (tak robi `company_growth.script.tl` z `ticketPriceMultiplier`).
  - **„Happy hour na linii X: bilety ×2 przez miesiąc”** – efekt czasowy jak nasze boosty, tylko dla wybranej linii.
    Do sprawdzenia, czy indeks `i` w zwracanej tablicy odpowiada `params[i]` (wtedy mnożnik per linia).
  - Sabotaż: „Strajk biletowy”, mnożnik < 1.
- **`OnModifyPathfinding`** (id `SimPersonSystem`). Param/zwrot: `{ town2lineCostFactor = { [town] = factor } }`.
  Factor < 1 sprawia, że mieszkańcy miasta chętniej wybierają linie (tak działa marketing). Własny efekt
  „Miasto X kocha komunikację” bez ruszania reputacji.
- Strumienie zdarzeń do feedu na stronie/czacie (bez zmiany gry): `OnArriveAtStop`, `OnCargoLoaded`/`Unloaded`,
  `OnToArriveAtDestination` (towar dotarł do budynku), `OnStartedLineUsage`/`OnCompletedLineUsage`,
  `Towns`/`OnLevelUp`, `Companies`/`OnLevelUp`, `AuthorityScoreChanged`, `ScoreDecreased`, `DeliveryRatingChanged`.
  Np. „Miasto X (właściciel: widz) awansowało!” w czacie.

## 3. Nowe komendy `api.cmd`, których jeszcze nie używamy

| Komenda | Pomysł |
|---|---|
| `makeVehicleSetModifiersCmd(veh, mods)` – `topSpeedScale`, `noiseScale`, `pollutionScale`, `comfortScale` | **„Turbo dla pociągu”**: +30% prędkości na miesiąc; sabotaż: „Hamulec ręczny”, -30%; „Luksusowy wagon”: komfort ×1.5. Najbardziej obiecująca nowa nagroda |
| `makeEntitySetColorCmd(entity, Vec3f)` | kolor linii albo pojazdu w barwach widza (już na liście pomysłów) |
| `makeEntitySetEmissionsCmd(entity, noise, pollution, radius)` | „Smog” albo „filtr” na fabryce |
| `makeVehicleReverseCmd(veh)` | troll: pociąg zawraca |
| `makeVehicleSendToDepotCmd(veh, false)` | troll: „pociąg na przegląd” |
| `makeTownBuildingSetBlockedDevelopmentCmd(building, true)` | „Zabytek”: budynek widza nigdy się nie zmieni |
| `makeWorldSetBulldozableCmd(entity, false)` | ochrona kupionego obiektu przed wyburzeniem (do rozważenia, bo blokuje streamera) |
| `makeIndustrySetDespawnTimeCmd(industry, t)` | „Ratunek dla fabryki”: przedłuża życie zamykanego zakładu (lista: `industry.getClosingIndustries()`) |
| `makeAnimalSpawnAtCmd(file, pos)` | stado zwierząt w mieście widza |
| `makeCustomEntityCreateCmd(modelId)` + `UpdateTransformation` | postawienie dowolnego modelu (np. pomnik widza) – wzór: `fun_elements/custom_entity_util.tl` |
| `makeJournalBookAssetCmd(player, entry)` | dotacja / kara (już na liście) |

## 4. Wątek GUI (`api.gui.*`, mamy go w `guiUpdate`): efekty na ekranie streamu

- **Kamera:** `api.gui.camera.focusEntity(e)`, `followEntity(e, jump)`, `enterFollowCameraCockpit(e)`,
  `focusPosition(pos, dist)`, `getCameraData`/`setCameraData`.
  **„Kamera widza”**: po wykupieniu kamera leci do jego miasta lub jedzie w kabinie pociągu przez 30 s,
  potem wraca (`getCameraData` przed, `setCameraData` po). Mocny efekt na streamie, zero wpływu na rozgrywkę.
- **Dźwięk:** `api.gui.sound.letVehicleHorn(veh)` – **„Trąbienie”** za grosze; `playRandomSoundEffect(paths)`.
- **Ikony nad obiektami:** `api.gui.spawnEphemeralHudImage(icon, entity, color, pos, czas, ...)` –
  na chwilę pokazuje ikonę nad miastem/pojazdem (np. serce albo logo Twitcha nad obiektem widza po wykupieniu).
- **Markery i strefy:** `api.gui.mission.setMarkerAtEntity(key, e, type, scale)`, `setZoneCircle(key, center, r,
  draw, color, prohibitBuilding)`. Trwałe oznaczenie obiektów widzów w grze. Uwaga: API jest „mission”,
  może działać tylko w misjach – sprawdzić.
- **Muzyka:** `api.gui.musicPlayer.goForward()`, `getTitle()`/`getArtist()` – „Pomiń utwór” i `!song` w czacie.
- **Zrzut ekranu:** `api.gui.camera.takeScreenshot(scale)` zapisuje do userdata – zrzut po wykonaniu nagrody,
  wysłany na stronę jako „dowód”.

## 5. Dane do strony i komend czatu (`api.engine.util.*`)

| Funkcja | Zastosowanie |
|---|---|
| `headquarters.getCompaniesValue()` – wynik, długość torów/dróg/mostów, liczba pojazdów, obsłużone miasta | panel „Firma” na stronie, `!firma` |
| `headquarters.getTransportedData()` – pasażerowie i towary łącznie | licznik na stronie |
| `town.getTownHappinessStats(town, n)` – niezadowoleni w podróży, per linia | „najbardziej wkurzone miasto”, `!miasto` |
| `town.getTownReachability`, `getTownEmissionDB`, `getTownCapacityUsage` | szczegóły miasta na stronie |
| `town.getTownProblems()`, `vehicle.getVehicleProblems()`, `line.getLinesIssues(player)`, `line.getLineProblems()` | **„Raport kryzysowy”**: korki, linie bez sensu, odcięte miasta – feed/alerty na stronie |
| `industry.getClosingIndustries()` | „Zakłady do uratowania” (łączy się z `makeIndustrySetDespawnTimeCmd`) |
| `cargo.getSummarizedCargoQualityDataForLine(line)` | punktualność linii, ranking „najlepsza linia” |
| `vehicle.getSpeed`, `getPosition`, `getVehicleMaintenanceState` | pozycje pojazdów na żywo na mapie |
| `industry.getIndustryProductivityInfo(e)` | czy zakład produkuje, czy działa boost |
| `town.computeTownsTrafficSpeedMap(k)` | warstwa korków na mapie |

## 6. Proponowana kolejność (od najtańszych)

1. **Fajerwerki nad miastem** i **trąbienie** – jedna linia kodu każda, czysto wizualne.
2. **Kamera widza** (focus/follow z powrotem) – mocny efekt na streamie.
3. **Kampania reklamowa ze sterowcem** – gotowa mechanika gry, wpływa na grę umiarkowanie.
4. **Turbo/hamulec pociągu** (`makeVehicleSetModifiersCmd`) i **happy hour linii** (`OnCalcTicketPrice`) – efekty czasowe na bazie istniejącego `effects.lua`.
5. Panel firmy i raport kryzysowy na stronie.
