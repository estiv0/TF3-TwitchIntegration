# API przemysłu w TF3: zrzut metod i jak gra liczy cykl produkcji

Zebrane 2026-10-09 przy szukaniu błędu strajku: po końcu efektu zakład dalej produkuje 1/rok, pasek
produkcji pokazuje „5 lat”. Źródła: definicje Teal (`<TF3>/api/tealdef/api/*.d.tl`) i skrypty gry
rozpakowane z `base/content/gui.zip`, `game_mechanics.zip`, `industries/industry_*.tl`.

Kolumna „my” = gdzie tego używamy w modzie (`lua_mod/content/tf3export/`).

## Jak gra liczy cykl produkcji (to tłumaczy błąd strajku)

Każda reguła zakładu (`StockList.rules[i]`) produkuje w cyklach. Gdy cykl startuje, silnik dopisuje
wpis do `StockList.ruleDatas[i].applyHistory`: `{ time, interval, consumedCargo, producedCargo }`.
**`interval` jest liczony raz, przy starcie cyklu**, z ówczesnej produktywności:

```
interval ≈ rokGry / rule.capacity / boost       -- boost = modifiers.productivity × boostery
         × lastApplyDecayFactor                 -- „+ X dni z powodu czasu dostawy”
```

Okno zakładu (`gui/entity_window/industry_react_util.tl`, `GetRuleDurationTextAndBoosted`,
`RuleProgressBar`) pokazuje:
- pasek = `(teraz - last.time) / last.interval`,
- tooltip = `last.interval`, gdy cykl trwa (`last.time + last.interval > teraz`); inaczej
  `rokGry / capacity / boost` (czas, jaki miałby następny cykl).

Zmiana `modifiers.productivity` (`makeStockListSetModifiersCmd`) **nie przelicza trwającego cyklu**.
Strajk ×0,01 na zakładzie 107/rok daje cykl ~1 rok (dłuższy, gdy baza była w tej chwili niższa;
„5 lat” ≈ 1/(107 × 0,002)). Po końcu strajku produktywność wraca, ale trwający cykl kończy się
dopiero po swoim `interval`. `cargoData[c].produceFrequency` (to, co pokazuje strona) też wynika
z tego cyklu, więc trzyma 1/rok.

Nie znalazłem w API niczego, co przerywa albo skraca trwający cykl (szczegóły niżej).

## Komponenty

### `INDUSTRY` (`Engine.Component.Industry`)
| pole | opis | my |
|---|---|---|
| `stockList` | encja listy magazynowej (u nas zwykle = encja zakładu) | collectors, effects |
| `level`, `maxLevel` | poziom zakładu | collectors |
| `upgradeProgress` | postęp do awansu/spadku | collectors |
| `manualDevelopment` | bez zamykania i zmiany poziomu | collectors |
| `closureTimeStamp` | czas zamknięcia (0 = brak) | – |
| `construction` | encja konstrukcji | collectors |

### `STOCK_LIST` (`Engine.Component.StockList`)
| pole | opis | my |
|---|---|---|
| `stocks[]` | `type` (Input/Output/Storage), `cargoType`, `cargoTypes`, `mixedTypes`, `capacity`, `loadSpeedModifier`, `tag` | collectors |
| `rules[]` | `input {{int}}`, `output {cargo: int}`, `capacity` (cykli/rok), `consumptionFactor`, `booster`, `boostFactor`, `priority`, `slot` | collectors (bez consumptionFactor/boostFactor/priority) |
| `stockDatas[]` | `consumeFrequency` (faktyczne zużycie/rok) | collectors |
| `ruleDatas[]` | `applyHistory[] { time, interval, consumedCargo {StockId}, producedCargo {{cargo, n}} }`, `lastApplyDecayFactor` | **nie** (tu siedzi długość cyklu) |
| `cargoData{cargo}` | `produceFrequency` (faktyczna produkcja/rok) | collectors |
| `modifiers` | `productivity` | collectors, effects |
| `thrownAwayCargo` | zmarnowany ładunek | – |

### `IndustryScriptingInfo` (`api/type/transformator.d.tl`, dla skryptów modeli)
`boosted01`, `doubleBoosted01`, `idle01`, `level`, `production01`, `workplaces`, `progress {number}`,
`producing`, `boosting`. Tylko odczyt w skryptach modeli, nie mamy do niego dostępu z game scriptu.

## Odczyt: `api.engine.util.stock.*` (argument: encja stockList)
| funkcja | zwraca | my |
|---|---|---|
| `getStockCargoTypes(sl, stockId)` | ładunki magazynu | – |
| `getCargoTypeShippedPerYear(sl, cargo)` / `getCargoShippedPerYear(sl)` | wywiezione/rok | – |
| `getCargoTypeDeliveredPerYear(sl, cargo)` / `getCargoDeliveredPerYear(sl)` | dowiezione/rok | – |
| `getCargoConsumedPerYear(sl, cargo)` | zużyte/rok | – |
| `getCargoProducedPerYear(sl, cargo)` | wyprodukowane/rok (z logbooka) | – |
| `getCargoMaxProductionPerYear(sl, cargo)` / `getCargoMaxConsumptionPerYear(sl, cargo)` | maksimum/rok | – |
| `getInputsOutputsFromRules(sl)` | `{wejścia, wyjścia}` | – (gra: industries.script) |
| `getCargoOutputPerYear(sl)` | produkcja/rok wg reguł | – (gra: rozbudowa zakładu) |
| `getCargoLogValueDiffPerYear(sl, logType, cargo)` | różnica logbooka/rok | – |
| `getCargoLogPerYearForTarget(src, dst, logType, cargo)` / `...ForTargetFilter(src, fn, logType, cargo)` | przepływ do celu | – |
| `getProductionRating(sl)` | 0..1, ocena produkcji (gra: szansa rozbudowy) | – |
| `getStockGroups...(sl, ..., producedOnly)` | grupy magazynów | – |
| `getStockListsWithThrownAwayCargo()` | `{sl: ilość}` | – |
| `hasAnyCompatibleStock(entities, cargo, load)` | bool | – |

## Odczyt: inne
| funkcja | opis | my |
|---|---|---|
| `api.engine.util.industry.getIndustryProductivityInfo(e)` | `{ producing, boostFromRule, boostFromPersonCapacity }` | – |
| `api.engine.util.industry.getClosingIndustries()` | `{{e, resName}}` | – |
| `api.engine.util.industry.getEconomyCargoDependencies(id)` | zależności ładunków | – |
| `api.engine.system.simEntityAtStockSystem.getStockCount(sl, stockId)` | stan magazynu | collectors |
| `...simEntityAtStockSystem` (inne) | mapa magazyn → ładunek, encje czekające, najdłużej czekająca | – |
| `api.engine.util.logbook.getLogValuePerYear(sl, name)` | `itemsProduced`, `itemsConsumed`, `itemsDestroyed`, `itemsLoaded` | collectors |
| `api.engine.util.logbook.getLogValueMostRecent / getLogValueOldest` | – | – |
| `api.engine.util.logbook.getChart` | wykres | collectors |
| `api.engine.system.streetConnectorSystem.getConstructionEntityForSubconstruction(e)` | konstrukcja zakładu | collectors |
| `api.engine.util.cargo.getCargoQualityDataForStockList / ...ForStock / getSummarizedCargoQualityDataForStockList` | jakość ładunku | – |
| `api.engine.util.construction.getNumAndFilledAutoFillSlots(constr)` | satelity zakładu | – |
| `industry_productivity_util` (gra, `/industries/industry_productivity_util.tl`) | `getVisitors`, `isIndustryWorkersBoosted`, `getPersonCapacityPotentialBoost`, `workersTimeIntervalMax` = 2 lata, `RequiredWorkersFractionForBoost` = 0,5 | – |

## Zapis (komendy)
| komenda | co robi | czy przelicza cykl |
|---|---|---|
| `makeStockListSetModifiersCmd(industry, modifiers)` | ustawia `productivity` | **nie** (potwierdzone błędem strajku) |
| `makeStockListDiscardCargoCmd(sl, stockIds, remainingTimeToDelivery)` | wyrzuca ładunek z magazynów | nie powinno (dotyczy magazynów, nie reguł) |
| `makeStockListSetStocksCargoTypeCmd(sl, stockIds, cargo?)` | zmienia ładunek magazynu (magazyny/hurtownie) | nieznane, ryzykowne dla zakładów |
| `makeIndustrySetManualDevelopmentCmd(e, manual)` | tryb ręczny | nie |
| `makeIndustrySetDespawnTimeCmd(e, t)` | czas zamknięcia | nie |
| `makeCreateIndustryExtendProposalCmd(constr, try, keep)` / `proposal.makeIndustryExtendProposal` | rozbudowa (gra robi to sama) | nieznane; zmienia zakład na stałe |
| `proposal.refreshConstruction(e, file?)` + `makeWorldBuildProposalCmd` | przebudowa konstrukcji | może zresetować `ruleDatas`, ale najpewniej tworzy nowe encje (zrywa właściciela zakładu po id, stan magazynów) |
| `makeStockSetCargoAmountCmd(sl, stockId, n, cargo?)` | **DEBUG**: stan magazynu | nie |
| `makeComponentExchangeCmd(e, comp)` | **DEBUG**: tylko `EmissionEmitter` / `ModelInstanceList` | nie da się podmienić StockList |
| `makeGamePerformSimulationStepsCmd(n)` | **DEBUG**: kroki symulacji całego świata | nie (globalne) |

## Sprawdzone w grze (2026-10-09, zakład 226115)

- `makeStockListDiscardCargoCmd(sl, {stock}, 1.0)` czyści **cały** magazyn. Z `0.5` nie zrobił nic
  (291 sztuk przed i po). Parametr najpewniej dotyczy jakości / czasu dostawy, a ładunek prosto
  z fabryki ma `averageQuality = 0`. Nie nadaje się do zdejmowania tylko nowych sztuk.
- `simEntityAtStockSystem.getStockEntities(sl, stock)` zwraca pary `{czas przybycia (ms gry), encja}`,
  od najstarszej; to samo co `SIM_ENTITY_AT_STOCK.arrivalTime`. Fabryka dokłada partię naraz
  (np. 9 sztuk z tym samym czasem). Magazyn wyjściowy miał limit 300.
- `api.cmd.debug.makeStockSetCargoAmountCmd(sl, stock, n)` **działa** z konsoli (bez trybu debug).
  Na tym oparty jest strajk i nalot skarbówki (`effects.lua`, blokada wywozu).

## Wnioski dla strajku (przed testami; wybrana blokada wywozu)

Skoro trwającego cyklu nie da się skrócić, trzeba nie dopuścić do cyklu dłuższego niż reszta efektu:

1. **Dolna granica zależna od czasu do końca efektu** (zalecane). Sterownik produktywności zna
   `rules[i].capacity`, czas gry i `untilTime` efektu. Cykl zaczęty teraz trwa
   `rokGry / (capacity × p)`, więc wystarczy trzymać
   `p ≥ rokGry / (capacity × pozostałyCzas)` (z zapasem na `lastApplyDecayFactor`).
   Strajk 3 mies. na zakładzie 107/rok: p ≥ 12/(107 × 3) ≈ 0,037, czyli 1 sztuka na końcu strajku
   i od razu normalna produkcja. Pod koniec efektu granica rośnie, więc późne cykle są krótkie.
2. **Produktywność 0** zamiast 0,01: może silnik wtedy nie startuje cyklu (`producing = false`) i po
   końcu od razu startuje normalny. Ryzyko dzielenia przez 0 (cykl „nieskończony”); trzeba sprawdzić
   w grze na zakładzie testowym.
3. **Stałe minimum** (np. 0,1): najprostsze, ale strajk słabszy i nadal może wyjść poza koniec efektu.

Ta sama pułapka dotyczy nalotu skarbówki (`industryTaxRaid`). Boost (×2..5) jej nie ma: po końcu
boostu trwający cykl jest krótszy niż normalny.

## Snippet do konsoli gry

Stan cyklu zakładu (id z eksportu / strony). Wklej w konsoli gry:

```lua
local e = 12345 -- id zakładu
local sl = api.engine.getComponent(e, api.type.ComponentType.STOCK_LIST)
local now = api.engine.getComponent(api.engine.util.getWorld(), api.type.ComponentType.GAME_TIME).gameTime
local year = api.util.getDefaultYearDuration()
print("productivity", sl.modifiers.productivity, "now", now, "year(ms)", year)
for i = 1, #sl.rules do
  local r, d = sl.rules[i], sl.ruleDatas[i]
  local h = d.applyHistory
  local last = #h > 0 and h[#h] or nil
  print(string.format("rule %d cap=%s booster=%s decay=%s hist=%d", i, tostring(r.capacity), tostring(r.booster),
    tostring(d.lastApplyDecayFactor), #h))
  if last then
    print(string.format("  last: start=%d interval=%d (%.2f y) ends in %.2f y", last.time, last.interval,
      last.interval / year, (last.time + last.interval - now) / year))
  end
end
for c, d in pairs(sl.cargoData) do print("cargo", c, "produceFrequency", d.produceFrequency) end
local info = api.engine.util.industry.getIndustryProductivityInfo(e)
print("producing", info.producing, "boostFromRule", info.boostFromRule, "boostFromPersonCapacity", info.boostFromPersonCapacity)
```
