# Industry API in TF3: method dump and how the game computes the production cycle

Collected on 2026-10-09 while hunting the strike bug: after the effect ends, the industry keeps producing 1/year, and
the production bar shows "5 years". Sources: Teal definitions (`<TF3>/api/tealdef/api/*.d.tl`) and game scripts
unpacked from `base/content/gui.zip`, `game_mechanics.zip`, `industries/industry_*.tl`.

The "us" column = where we use it in the mod (`lua_mod/content/tf3export/`).

## How the game computes the production cycle (this explains the strike bug)

Each industry rule (`StockList.rules[i]`) produces in cycles. When a cycle starts, the engine appends
an entry to `StockList.ruleDatas[i].applyHistory`: `{ time, interval, consumedCargo, producedCargo }`.
**`interval` is computed once, when the cycle starts**, from the productivity at that moment:

```
interval ≈ gameYear / rule.capacity / boost     -- boost = modifiers.productivity × boosters
         × lastApplyDecayFactor                 -- "+ X days due to delivery time"
```

The industry window (`gui/entity_window/industry_react_util.tl`, `GetRuleDurationTextAndBoosted`,
`RuleProgressBar`) shows:
- bar = `(now - last.time) / last.interval`,
- tooltip = `last.interval` while the cycle runs (`last.time + last.interval > now`); otherwise
  `gameYear / capacity / boost` (how long the next cycle would take).

Changing `modifiers.productivity` (`makeStockListSetModifiersCmd`) **does not recompute the running cycle**.
A ×0.01 strike on a 107/year industry gives a ~1-year cycle (longer when the base was lower at that moment;
"5 years" ≈ 1/(107 × 0.002)). After the strike ends productivity comes back, but the running cycle only ends
after its `interval`. `cargoData[c].produceFrequency` (what the website shows) also follows
that cycle, so it stays at 1/year.

I found nothing in the API that interrupts or shortens a running cycle (details below).

## Components

### `INDUSTRY` (`Engine.Component.Industry`)
| field | description | us |
|---|---|---|
| `stockList` | stock list entity (for us usually = the industry entity) | collectors, effects |
| `level`, `maxLevel` | industry level | collectors |
| `upgradeProgress` | progress to level up/down | collectors |
| `manualDevelopment` | no closing and no level changes | collectors |
| `closureTimeStamp` | closing time (0 = none) | – |
| `construction` | construction entity | collectors |

### `STOCK_LIST` (`Engine.Component.StockList`)
| field | description | us |
|---|---|---|
| `stocks[]` | `type` (Input/Output/Storage), `cargoType`, `cargoTypes`, `mixedTypes`, `capacity`, `loadSpeedModifier`, `tag` | collectors |
| `rules[]` | `input {{int}}`, `output {cargo: int}`, `capacity` (cycles/year), `consumptionFactor`, `booster`, `boostFactor`, `priority`, `slot` | collectors (without consumptionFactor/boostFactor/priority) |
| `stockDatas[]` | `consumeFrequency` (actual consumption/year) | collectors |
| `ruleDatas[]` | `applyHistory[] { time, interval, consumedCargo {StockId}, producedCargo {{cargo, n}} }`, `lastApplyDecayFactor` | **no** (the cycle length lives here) |
| `cargoData{cargo}` | `produceFrequency` (actual production/year) | collectors |
| `modifiers` | `productivity` | collectors, effects |
| `thrownAwayCargo` | wasted cargo | – |

### `IndustryScriptingInfo` (`api/type/transformator.d.tl`, for model scripts)
`boosted01`, `doubleBoosted01`, `idle01`, `level`, `production01`, `workplaces`, `progress {number}`,
`producing`, `boosting`. Read-only in model scripts; we can't reach it from a game script.

## Reading: `api.engine.util.stock.*` (argument: the stockList entity)
| function | returns | us |
|---|---|---|
| `getStockCargoTypes(sl, stockId)` | the stock's cargo | – |
| `getCargoTypeShippedPerYear(sl, cargo)` / `getCargoShippedPerYear(sl)` | shipped/year | – |
| `getCargoTypeDeliveredPerYear(sl, cargo)` / `getCargoDeliveredPerYear(sl)` | delivered/year | – |
| `getCargoConsumedPerYear(sl, cargo)` | consumed/year | – |
| `getCargoProducedPerYear(sl, cargo)` | produced/year (from the logbook) | – |
| `getCargoMaxProductionPerYear(sl, cargo)` / `getCargoMaxConsumptionPerYear(sl, cargo)` | maximum/year | – |
| `getInputsOutputsFromRules(sl)` | `{inputs, outputs}` | – (game: industries.script) |
| `getCargoOutputPerYear(sl)` | production/year by rules | – (game: industry expansion) |
| `getCargoLogValueDiffPerYear(sl, logType, cargo)` | logbook difference/year | – |
| `getCargoLogPerYearForTarget(src, dst, logType, cargo)` / `...ForTargetFilter(src, fn, logType, cargo)` | flow to a target | – |
| `getProductionRating(sl)` | 0..1, production rating (game: expansion chance) | – |
| `getStockGroups...(sl, ..., producedOnly)` | stock groups | – |
| `getStockListsWithThrownAwayCargo()` | `{sl: amount}` | – |
| `hasAnyCompatibleStock(entities, cargo, load)` | bool | – |

## Reading: other
| function | description | us |
|---|---|---|
| `api.engine.util.industry.getIndustryProductivityInfo(e)` | `{ producing, boostFromRule, boostFromPersonCapacity }` | – |
| `api.engine.util.industry.getClosingIndustries()` | `{{e, resName}}` | – |
| `api.engine.util.industry.getEconomyCargoDependencies(id)` | cargo dependencies | – |
| `api.engine.system.simEntityAtStockSystem.getStockCount(sl, stockId)` | stock level | collectors |
| `...simEntityAtStockSystem` (others) | stock → cargo map, waiting entities, longest waiting | – |
| `api.engine.util.logbook.getLogValuePerYear(sl, name)` | `itemsProduced`, `itemsConsumed`, `itemsDestroyed`, `itemsLoaded` | collectors |
| `api.engine.util.logbook.getLogValueMostRecent / getLogValueOldest` | – | – |
| `api.engine.util.logbook.getChart` | chart | collectors |
| `api.engine.system.streetConnectorSystem.getConstructionEntityForSubconstruction(e)` | the industry's construction | collectors |
| `api.engine.util.cargo.getCargoQualityDataForStockList / ...ForStock / getSummarizedCargoQualityDataForStockList` | cargo quality | – |
| `api.engine.util.construction.getNumAndFilledAutoFillSlots(constr)` | industry satellites | – |
| `industry_productivity_util` (game, `/industries/industry_productivity_util.tl`) | `getVisitors`, `isIndustryWorkersBoosted`, `getPersonCapacityPotentialBoost`, `workersTimeIntervalMax` = 2 years, `RequiredWorkersFractionForBoost` = 0.5 | – |

## Writing (commands)
| command | what it does | recomputes the cycle? |
|---|---|---|
| `makeStockListSetModifiersCmd(industry, modifiers)` | sets `productivity` | **no** (confirmed by the strike bug) |
| `makeStockListDiscardCargoCmd(sl, stockIds, remainingTimeToDelivery)` | throws cargo out of stocks | shouldn't (it's about stocks, not rules) |
| `makeStockListSetStocksCargoTypeCmd(sl, stockIds, cargo?)` | changes a stock's cargo (warehouses/wholesale) | unknown, risky for industries |
| `makeIndustrySetManualDevelopmentCmd(e, manual)` | manual mode | no |
| `makeIndustrySetDespawnTimeCmd(e, t)` | closing time | no |
| `makeCreateIndustryExtendProposalCmd(constr, try, keep)` / `proposal.makeIndustryExtendProposal` | expansion (the game does this itself) | unknown; changes the industry permanently |
| `proposal.refreshConstruction(e, file?)` + `makeWorldBuildProposalCmd` | rebuilding the construction | may reset `ruleDatas`, but most likely creates new entities (breaks industry ownership by id, stock levels) |
| `makeStockSetCargoAmountCmd(sl, stockId, n, cargo?)` | **DEBUG**: stock level | no |
| `makeComponentExchangeCmd(e, comp)` | **DEBUG**: only `EmissionEmitter` / `ModelInstanceList` | the StockList can't be swapped |
| `makeGamePerformSimulationStepsCmd(n)` | **DEBUG**: simulation steps of the whole world | no (global) |

## Checked in the game (2026-10-09, industry 226115)

- `makeStockListDiscardCargoCmd(sl, {stock}, 1.0)` clears the **whole** stock. With `0.5` it did nothing
  (291 items before and after). The parameter most likely relates to quality / delivery time, and cargo straight
  from the factory has `averageQuality = 0`. Not usable for removing only new items.
- `simEntityAtStockSystem.getStockEntities(sl, stock)` returns pairs `{arrival time (game ms), entity}`,
  oldest first; the same as `SIM_ENTITY_AT_STOCK.arrivalTime`. The factory adds a batch at once
  (e.g. 9 items with the same time). The output stock had a limit of 300.
- `api.cmd.debug.makeStockSetCargoAmountCmd(sl, stock, n)` **works** from the console (without debug mode).
  The strike and the tax raid are built on it (`effects.lua`, shipping block).

## Conclusions for the strike (before testing; the shipping block was chosen)

Since a running cycle can't be shortened, a cycle longer than the rest of the effect must not be allowed to start:

1. **A lower bound depending on the time left in the effect** (recommended). The productivity controller knows
   `rules[i].capacity`, the game time and the effect's `untilTime`. A cycle started now lasts
   `gameYear / (capacity × p)`, so it's enough to keep
   `p ≥ gameYear / (capacity × timeLeft)` (with a margin for `lastApplyDecayFactor`).
   A 3-month strike on a 107/year industry: p ≥ 12/(107 × 3) ≈ 0.037, i.e. 1 item at the end of the strike
   and normal production right away. Near the end of the effect the bound grows, so late cycles are short.
2. **Productivity 0** instead of 0.01: maybe the engine then starts no cycle (`producing = false`) and after
   the end starts a normal one right away. Risk of a division by 0 (an "infinite" cycle); needs checking
   in the game on a test industry.
3. **A fixed minimum** (e.g. 0.1): the simplest, but the strike is weaker and can still run past the end of the effect.

The same trap applies to the tax raid (`industryTaxRaid`). A boost (×2..5) doesn't have it: after the boost ends
the running cycle is shorter than normal.

## Game console snippet

The cycle state of an industry (id from the export / website). Paste into the game console:

```lua
local e = 12345 -- industry id
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
