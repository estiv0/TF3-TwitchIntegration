-- Efekty czasowe z nagród za punkty kanału: boost miasta, boost przemysłu, zatrzymanie linii.
-- Każdy efekt trwa X miesięcy gry i sam się cofa (czas gry stoi na pauzie, więc efekt też).
-- Miesiące, nie dni: dzień gry trwa tylko 2-3 s, więc efekt na kilka dni mijał, zanim było go widać.
--
-- Działa w wątku GUI (tam czytamy kolejkę komend). Lista efektów jest kopiowana do stanu game scriptu
-- (state, zapisywany w save'ie) zdarzeniem skryptowym "tf3x_effects" obsługiwanym w handleEvent
-- (wątek symulacji; tylko on może pisać state). Po wczytaniu gry efekty wracają ze stanu.
--
-- Efekt na tym samym celu (np. drugi boost tego samego miasta) przedłuża czas zamiast się kumulować;
-- gdy nowe wykupienie ma większy mnożnik (zmiana ustawień), efekt dostaje ten większy.

local util = ug_require "util.lua"
local ct = util.ct

local E = {}

E.EVENT = "tf3x_effects"
E.SCRIPT_ID = "tf3export"

local MIN_FACTOR, MAX_FACTOR = 1.0, 5.0
local MIN_MONTHS, MAX_MONTHS = 1, 60
local SETTLE_POLLS = 3 -- tyle odczytów (~s) czekamy, aż gra przyjmie ustawioną produktywność

local active = nil -- key -> efekt (same dane, bez funkcji); nil = jeszcze nie wczytane ze stanu

---------------------------------------------------------------- pomocnicze
local function send(cmd)
	api.cmd.sendCommand(cmd)
end

local function copyList(v)
	local out = {}
	for i, x in ipairs(util.list(v)) do out[i] = x end
	return out
end

local function lineVehicles(line)
	local ok, vs = pcall(function() return api.engine.system.transportVehicleSystem.getLineVehicles(line) end)
	return ok and copyList(vs) or {}
end

-- Produktywność zakładu: modyfikatory listy magazynowej (na encji zakładu albo w ind.stockList).
local function productivity(industry)
	local ind = util.comp(industry, ct.INDUSTRY)
	local sl = (ind and util.comp(ind.stockList, ct.STOCK_LIST)) or util.comp(industry, ct.STOCK_LIST)
	local m = sl and util.field(sl, "modifiers")
	return m and tonumber(m.productivity) or nil
end

local function setProductivity(industry, p)
	local m = api.type.StockList.Modifiers.new()
	m.productivity = p
	send(api.cmd.makeStockListSetModifiersCmd(industry, m))
end

local function near(a, b) return a ~= nil and b ~= nil and math.abs(a - b) < 1e-3 end

-- Miesiąc gry w jednostkach gameTime (rok = 365 dni).
local function monthLength()
	return util.dayLength() * 365 / 12
end

local function fmt(x) return x and string.format("%.3f", x) or "?" end

---------------------------------------------------------------- rodzaje efektów
-- apply(e) -> verify (function() -> true, gdy efekt widoczny) albo nil, błąd
-- revert(e): cofa efekt (wartości zapamiętane w e przy apply)
-- maintain(e) (opcjonalnie): pilnuje efektu; true = zmienił dane efektu (trzeba zapisać stan)
local TYPES = {}

-- Miasto: mnożymy sizeFactors (mieszkalne, usługowe, przemysłowe). Budynki dorastają do nowego
-- rozmiaru stopniowo (updateAllBuildings = false), po końcu efektu wracają stare współczynniki.
TYPES.townBoost = {
	needsFactor = true,
	apply = function(e)
		local town = util.comp(e.entity, ct.TOWN)
		if not town then return nil, "not a town" end
		local original = copyList(town.sizeFactors)
		if #original == 0 then return nil, "town has no size factors" end
		local boosted = {}
		for i, v in ipairs(original) do boosted[i] = v * e.factor end
		e.original = original
		send(api.cmd.makeTownUpdateSizeCmd(e.entity, boosted, false))
		return function()
			local t = util.comp(e.entity, ct.TOWN)
			return t ~= nil and near(copyList(t.sizeFactors)[1], boosted[1])
		end
	end,
	upgrade = function(e)
		local boosted = {}
		for i, v in ipairs(e.original) do boosted[i] = v * e.factor end
		send(api.cmd.makeTownUpdateSizeCmd(e.entity, boosted, false))
	end,
	revert = function(e)
		if util.comp(e.entity, ct.TOWN) then send(api.cmd.makeTownUpdateSizeCmd(e.entity, e.original, false)) end
	end,
	describe = function(e)
		return string.format("factor %s, size factors %s -> x%s", fmt(e.factor), table.concat(e.original or {}, "/"), fmt(e.factor))
	end,
}

-- Zakład: produktywność × factor (na stronie: „Produktywność”). Skrypt gry industry_workers też ją
-- ustawia (boost od pracowników), więc pilnujemy jej w maintain. Po naszym ustawieniu czekamy kilka
-- odczytów, aż gra je przyjmie, i zapamiętujemy, co faktycznie przyjęła (e.applied; gra może mieć
-- własny limit). Dopiero zmiana względem e.applied znaczy, że ktoś inny ją zmienił: wtedy nowa
-- wartość staje się bazą. Bez tego każda różnica od celu przeliczałaby bazę w kółko.
-- Po końcu efektu wraca baza; skrypt pracowników poprawi ją sam, jeśli trzeba.
local function setTarget(e)
	e.target = e.baseline * e.factor
	e.applied, e.settle = nil, SETTLE_POLLS
	setProductivity(e.entity, e.target)
end

TYPES.industryBoost = {
	needsFactor = true,
	apply = function(e)
		if not util.comp(e.entity, ct.INDUSTRY) then return nil, "not an industry" end
		e.baseline = productivity(e.entity) or 1.0
		setTarget(e)
		return function() return near(productivity(e.entity), e.target) end
	end,
	upgrade = setTarget,
	maintain = function(e)
		local p = productivity(e.entity)
		if p == nil then return false end
		if e.applied == nil then
			e.settle = (e.settle or 1) - 1
			if not near(p, e.target) and e.settle > 0 then return false end
			e.applied, e.settle = p, nil
			if not near(p, e.target) then
				util.log(string.format("effect %s: game keeps productivity %s instead of %s", e.key, fmt(p), fmt(e.target)))
			end
			return true
		end
		if near(p, e.applied) then return false end
		e.rebases = (e.rebases or 0) + 1
		if e.rebases <= 3 then
			util.log(string.format("effect %s: productivity changed by the game %s -> %s, new base (x%s)",
				e.key, fmt(e.applied), fmt(p), fmt(e.factor)))
		end
		e.baseline = p
		setTarget(e)
		return true
	end,
	revert = function(e)
		if util.comp(e.entity, ct.INDUSTRY) then setProductivity(e.entity, e.baseline or 1.0) end
	end,
	describe = function(e)
		return string.format("productivity %s -> %s (x%s)", fmt(e.baseline), fmt(e.target), fmt(e.factor))
	end,
}

-- Linia, wariant "stop": jak przycisk zatrzymania w grze; pojazd staje tam, gdzie jest (także na szlaku).
-- Pojazdy zatrzymane wcześniej przez gracza zostawiamy w spokoju (nie ruszamy ich też przy cofaniu).
TYPES.lineStop = {
	apply = function(e)
		local vehicles = lineVehicles(e.entity)
		if #vehicles == 0 then return nil, "line has no vehicles" end
		local stopped = {}
		for _, v in ipairs(vehicles) do
			local tv = util.comp(v, ct.TRANSPORT_VEHICLE)
			if tv and not tv.userStopped then
				send(api.cmd.makeVehicleSetStoppedByUserCmd(v, true))
				stopped[#stopped + 1] = v
			end
		end
		if #stopped == 0 then return nil, "all vehicles already stopped" end
		util.log(string.format("effect lineStop:%s: stopping %d of %d vehicles", tostring(e.entity), #stopped, #vehicles))
		e.vehicles = stopped
		return function()
			for _, v in ipairs(stopped) do
				local tv = util.comp(v, ct.TRANSPORT_VEHICLE)
				if tv and not tv.userStopped then return false end
			end
			return true
		end
	end,
	revert = function(e)
		for _, v in ipairs(e.vehicles or {}) do
			local tv = util.comp(v, ct.TRANSPORT_VEHICLE)
			if tv and tv.userStopped then send(api.cmd.makeVehicleSetStoppedByUserCmd(v, false)) end
		end
	end,
}

-- Linia, wariant "hold": ręczny odjazd; pojazdy dojeżdżają do przystanku i czekają tam.
-- API nie pokazuje tego trybu, więc efektu nie da się sprawdzić (uznajemy wysłanie za sukces).
TYPES.lineHold = {
	apply = function(e)
		local vehicles = lineVehicles(e.entity)
		if #vehicles == 0 then return nil, "line has no vehicles" end
		for _, v in ipairs(vehicles) do send(api.cmd.makeVehicleSetManualDepartureCmd(v, true)) end
		util.log(string.format("effect lineHold:%s: manual departure for %d vehicles", tostring(e.entity), #vehicles))
		e.vehicles = vehicles
		return function() return true end
	end,
	revert = function(e)
		for _, v in ipairs(e.vehicles or {}) do
			if util.comp(v, ct.TRANSPORT_VEHICLE) then
				send(api.cmd.makeVehicleSetManualDepartureCmd(v, false))
				pcall(send, api.cmd.makeVehicleTryToDepartCmd(v))
			end
		end
	end,
}

---------------------------------------------------------------- stan
local function persist()
	local list = {}
	for _, e in pairs(active) do list[#list + 1] = e end
	local ok, err = pcall(send, api.cmd.makeScriptingSendEventCmd("", E.SCRIPT_ID, E.EVENT, { effects = list }))
	if not ok then util.log("effects: saving to game state failed: " .. tostring(err)) end
end

-- Nowa sesja (wczytana gra): efekty wracają ze stanu game scriptu przy następnym update.
function E.reset()
	active = nil
end

local function load(shared)
	active = {}
	local n = 0
	for _, e in ipairs(util.list(shared and shared.effects)) do
		if type(e) == "table" and e.key and TYPES[e.type] then
			active[e.key] = e
			n = n + 1
		end
	end
	if n > 0 then util.log(string.format("effects: %d active effect(s) restored from the save", n)) end
end

-- Co sekundę z guiUpdate: wygasanie i pilnowanie efektów.
function E.update(now, shared)
	if active == nil then load(shared) end
	local changed = false
	for key, e in pairs(active) do
		local t = TYPES[e.type]
		if now >= e.untilTime then
			local ok, err = pcall(t.revert, e)
			util.log(string.format("effect %s ended%s", key, ok and "" or (" (revert failed: " .. tostring(err) .. ")")))
			active[key] = nil
			changed = true
		elseif t.maintain then
			local ok, res = pcall(t.maintain, e)
			if ok and res then changed = true end
		end
	end
	if changed then persist() end
end

-- Efekt, którego nie udało się potwierdzić (commands.lua zwraca wtedy punkty): cofamy i usuwamy.
function E.abort(c)
	local key = tostring(c.effect) .. ":" .. tostring(tonumber(c.entity))
	local e = active and active[key]
	if not e then return end
	pcall(TYPES[e.type].revert, e)
	active[key] = nil
	persist()
	util.log("effect " .. key .. " aborted")
end

-- Komenda "effect" z kolejki: { effect = "townBoost", entity, months, factor }.
-- Zwraca verify albo nil, błąd (jak rodzaje komend w commands.lua).
function E.start(c, now)
	if active == nil then return nil, "effects not loaded yet" end
	local t = TYPES[c.effect]
	local entity, months = tonumber(c.entity), tonumber(c.months)
	if not t or not entity or not months or now == nil then return nil, "bad arguments" end
	months = math.max(MIN_MONTHS, math.min(MAX_MONTHS, months))
	local factor = nil
	if t.needsFactor then
		factor = tonumber(c.factor)
		if not factor then return nil, "bad arguments" end
		factor = math.max(MIN_FACTOR, math.min(MAX_FACTOR, factor))
	end
	local key = c.effect .. ":" .. tostring(entity)

	local existing = active[key]
	if existing then -- przedłużenie; siła tylko w górę (np. po zmianie mnożnika w ustawieniach)
		existing.untilTime = math.max(existing.untilTime, now) + months * monthLength()
		local stronger = factor and existing.factor and factor > existing.factor + 1e-6
		if stronger then
			existing.factor = factor
			t.upgrade(existing)
		end
		persist()
		util.log(string.format("effect %s extended by %d months%s", key, months,
			stronger and (", now " .. t.describe(existing)) or ""))
		return function() return true end
	end

	local e = { key = key, type = c.effect, entity = entity, untilTime = now + months * monthLength(), startTime = now, factor = factor }
	local verify, err = t.apply(e)
	if not verify then return nil, err end
	active[key] = e
	persist()
	util.log(string.format("effect %s started for %d months%s", key, months, t.describe and (": " .. t.describe(e)) or ""))
	return verify
end

return E
