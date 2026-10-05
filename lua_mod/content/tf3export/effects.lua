-- Efekty czasowe z nagród za punkty kanału: boost miasta, produktywność zakładów (boost, strajk,
-- nalot skarbówki), prędkość pojazdów linii (paliwo premium / chrzczone), zatrzymanie linii.
-- Każdy efekt trwa X miesięcy gry i sam się cofa (czas gry stoi na pauzie, więc efekt też).
-- Miesiące, nie dni: dzień gry trwa tylko 2-3 s, więc efekt na kilka dni mijał, zanim było go widać.
--
-- Działa w wątku GUI (tam czytamy kolejkę komend). Lista efektów jest kopiowana do stanu game scriptu
-- (state, zapisywany w save'ie) zdarzeniem skryptowym "tf3x_effects" obsługiwanym w handleEvent
-- (wątek symulacji; tylko on może pisać state). Po wczytaniu gry efekty wracają ze stanu.
--
-- Efekt na tym samym celu (np. drugi boost tego samego miasta) przedłuża czas zamiast się kumulować;
-- gdy nowe wykupienie ma silniejszy mnożnik (zmiana ustawień), efekt dostaje ten silniejszy.
--
-- Produktywność zakładu i prędkość pojazdu ustawia jeden sterownik na obiekt (Controller niżej):
-- różne efekty na tym samym zakładzie (boost ×2 i strajk ×0,01) mnożą się, zamiast walczyć o wartość.

local util = ug_require "util.lua"
local ct = util.ct

local E = {}

E.EVENT = "tf3x_effects"
E.SCRIPT_ID = "tf3export"

local MIN_MONTHS, MAX_MONTHS = 1, 60
local SETTLE_POLLS = 3 -- tyle odczytów (~s) czekamy, aż gra przyjmie ustawioną wartość

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

local function near(a, b) return a ~= nil and b ~= nil and math.abs(a - b) < 1e-3 end

-- Miesiąc gry w jednostkach gameTime (rok = 365 dni).
local function monthLength()
	return util.dayLength() * 365 / 12
end

local function fmt(x) return x and string.format("%.3f", x) or "?" end

---------------------------------------------------------------- sterownik wartości
-- Pilnuje jednej liczby na wielu obiektach (produktywność zakładu, prędkość pojazdu): baza (wartość
-- sprzed efektów) × iloczyn mnożników aktywnych efektów. Gra może tę liczbę zmieniać sama (skrypt
-- pracowników ustawia produktywność), więc po ustawieniu czekamy kilka odczytów, aż gra ją przyjmie,
-- i zapamiętujemy, co faktycznie przyjęła (applied; gra może mieć własny limit). Dopiero zmiana względem
-- applied znaczy, że ktoś inny ją zmienił: wtedy nowa wartość staje się bazą. Bez tego każda różnica
-- od celu przeliczałaby bazę w kółko. Po końcu efektów wraca baza.
local function Controller(name, read, write)
	local C = { entries = {} } -- id -> { baseline, factor, target, applied, settle, rebases }

	local function setTarget(id, en)
		en.target = en.baseline * en.factor
		en.applied, en.settle = nil, SETTLE_POLLS
		write(id, en.target)
	end

	local function maintain(id, en)
		local v = read(id)
		if v == nil then return false end
		if en.applied == nil then
			en.settle = (en.settle or 1) - 1
			if not near(v, en.target) and en.settle > 0 then return false end
			en.applied, en.settle = v, nil
			if not near(v, en.target) then
				util.log(string.format("%s %s: game keeps %s instead of %s", name, tostring(id), fmt(v), fmt(en.target)))
			end
			return true
		end
		if near(v, en.applied) then return false end
		en.rebases = (en.rebases or 0) + 1
		if en.rebases <= 3 then
			util.log(string.format("%s %s: changed by the game %s -> %s, new base (x%s)",
				name, tostring(id), fmt(en.applied), fmt(v), fmt(en.factor)))
		end
		en.baseline = v
		setTarget(id, en)
		return true
	end

	-- wanted: id -> mnożnik. Zwraca true, gdy zmieniły się dane do zapisania w stanie.
	function C.sync(wanted)
		local changed = false
		for id, f in pairs(wanted) do
			local en = C.entries[id]
			if en == nil then
				local v = read(id)
				if v ~= nil then
					en = { baseline = v, factor = f }
					C.entries[id] = en
					setTarget(id, en)
					changed = true
				end
			elseif not near(en.factor, f) then
				en.factor = f
				setTarget(id, en)
				changed = true
			elseif maintain(id, en) then
				changed = true
			end
		end
		for id, en in pairs(C.entries) do
			if wanted[id] == nil then
				if read(id) ~= nil then
					local ok, err = pcall(write, id, en.baseline)
					if not ok then util.log(string.format("%s %s: restoring %s failed: %s", name, tostring(id), fmt(en.baseline), tostring(err))) end
				end
				C.entries[id] = nil
				changed = true
			end
		end
		return changed
	end

	-- Czy gra przyjęła cel: wartość równa celowi albo przesunięta od bazy w jego stronę (gra może mieć limit).
	function C.reached(id)
		local en, v = C.entries[id], read(id)
		if en == nil or v == nil then return false end
		return near(v, en.target) or (v - en.baseline) * (en.target - en.baseline) > 0
	end

	function C.describe(id)
		local en = C.entries[id]
		return en and string.format("%s -> %s (x%s)", fmt(en.baseline), fmt(en.target), fmt(en.factor)) or "?"
	end

	function C.save()
		local out = {}
		for id, en in pairs(C.entries) do
			out[#out + 1] = { id = id, baseline = en.baseline, factor = en.factor, target = en.target, applied = en.applied }
		end
		return out
	end

	function C.load(list)
		C.entries = {}
		for _, x in ipairs(util.list(list)) do
			if type(x) == "table" and tonumber(x.id) and x.baseline and x.factor and x.target then
				-- settle od nowa: po wczytaniu nie wiemy, czy gra zdążyła przyjąć ostatni cel
				C.entries[tonumber(x.id)] = { baseline = x.baseline, factor = x.factor, target = x.target, applied = x.applied,
					settle = x.applied == nil and SETTLE_POLLS or nil }
			end
		end
	end

	return C
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

-- Prędkość maksymalna pojazdu: modifiers.topSpeedScale komponentu TRANSPORT_VEHICLE.
local VEHICLE_SCALES = { "noiseScale", "pollutionScale", "comfortScale" }

local function vehicleModifiers(vehicle)
	local tv = util.comp(vehicle, ct.TRANSPORT_VEHICLE)
	return tv and util.field(tv, "modifiers")
end

local function speedScale(vehicle)
	local m = vehicleModifiers(vehicle)
	return m and tonumber(util.field(m, "topSpeedScale")) or nil
end

local function setSpeedScale(vehicle, scale)
	local cur = vehicleModifiers(vehicle)
	local m = api.type.TransportVehicle.Modifiers.new()
	for _, k in ipairs(VEHICLE_SCALES) do -- komenda ustawia wszystkie skale naraz: pozostałe bez zmian
		local x = cur and tonumber(util.field(cur, k))
		m[k] = x or 1.0
	end
	m.topSpeedScale = scale
	send(api.cmd.makeVehicleSetModifiersCmd(vehicle, m))
end

local industries = Controller("productivity", productivity, setProductivity)
local vehicles = Controller("speed", speedScale, setSpeedScale)

---------------------------------------------------------------- rodzaje efektów
-- Dwa style:
--  * group = "industry" | "speed": efekt tylko podaje mnożnik (range = dozwolony zakres), resztę robi
--    sterownik; multi = efekt na wielu zakładach naraz (e.entities), klucz po c.target;
--  * apply(e) -> verify albo nil, błąd; revert(e) cofa (lineStop, lineHold, townBoost).
local TYPES = {}

-- Miasto: mnożymy sizeFactors (mieszkalne, usługowe, przemysłowe). Budynki dorastają do nowego
-- rozmiaru stopniowo (updateAllBuildings = false), po końcu efektu wracają stare współczynniki.
TYPES.townBoost = {
	range = { 1.0, 5.0 },
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

-- Zakład (na stronie: „Produktywność”): boost, strajk, nalot skarbówki (wszystkie zakłady jednego widza).
TYPES.industryBoost = { group = "industry", range = { 1.0, 5.0 } }
TYPES.industryStrike = { group = "industry", range = { 0.01, 1.0 } }
TYPES.industryTaxRaid = { group = "industry", range = { 0.01, 1.0 }, multi = true }

-- Linia: prędkość maksymalna wszystkich jej pojazdów (także dokupionych w trakcie efektu).
TYPES.lineFuelPremium = { group = "speed", range = { 1.0, 3.0 } }
TYPES.lineFuelBad = { group = "speed", range = { 0.1, 1.0 } }

-- Linia, wariant "stop": jak przycisk zatrzymania w grze; pojazd staje tam, gdzie jest (także na szlaku).
-- Pojazdy zatrzymane wcześniej przez gracza zostawiamy w spokoju (nie ruszamy ich też przy cofaniu).
TYPES.lineStop = {
	apply = function(e)
		local vs = util.lineVehicles(e.entity)
		if #vs == 0 then return nil, "line has no vehicles" end
		local stopped = {}
		for _, v in ipairs(vs) do
			local tv = util.comp(v, ct.TRANSPORT_VEHICLE)
			if tv and not tv.userStopped then
				send(api.cmd.makeVehicleSetStoppedByUserCmd(v, true))
				stopped[#stopped + 1] = v
			end
		end
		if #stopped == 0 then return nil, "all vehicles already stopped" end
		util.log(string.format("effect lineStop:%s: stopping %d of %d vehicles", tostring(e.entity), #stopped, #vs))
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
		local vs = util.lineVehicles(e.entity)
		if #vs == 0 then return nil, "line has no vehicles" end
		for _, v in ipairs(vs) do send(api.cmd.makeVehicleSetManualDepartureCmd(v, true)) end
		util.log(string.format("effect lineHold:%s: manual departure for %d vehicles", tostring(e.entity), #vs))
		e.vehicles = vs
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

-- Cele efektu: lista zakładów (multi) albo jedna encja.
local function targets(e)
	return e.entities or { e.entity }
end

---------------------------------------------------------------- sterowniki
-- Mnożniki ze wszystkich aktywnych efektów -> sterowniki. True = zmiana do zapisania w stanie.
local function syncControllers()
	local prod, lineSpeed, speed = {}, {}, {}
	for _, e in pairs(active) do
		local t = TYPES[e.type]
		if t.group == "industry" then
			for _, id in ipairs(targets(e)) do prod[id] = (prod[id] or 1.0) * e.factor end
		elseif t.group == "speed" then
			lineSpeed[e.entity] = (lineSpeed[e.entity] or 1.0) * e.factor
		end
	end
	for line, f in pairs(lineSpeed) do
		for _, v in ipairs(util.lineVehicles(line)) do speed[v] = f end
	end
	local a = industries.sync(prod)
	local b = vehicles.sync(speed)
	return a or b
end

---------------------------------------------------------------- stan
local function persist()
	local list = {}
	for _, e in pairs(active) do list[#list + 1] = e end
	local ctl = { industries = industries.save(), vehicles = vehicles.save() }
	local ok, err = pcall(send, api.cmd.makeScriptingSendEventCmd("", E.SCRIPT_ID, E.EVENT, { effects = list, ctl = ctl }))
	if not ok then util.log("effects: saving to game state failed: " .. tostring(err)) end
end

-- Nowa sesja (wczytana gra): efekty wracają ze stanu game scriptu przy następnym update.
function E.reset()
	active = nil
end

local function load(shared)
	active = {}
	local ctl = shared and shared.effectCtl or {}
	industries.load(ctl.industries)
	vehicles.load(ctl.vehicles)
	local n = 0
	for _, e in ipairs(util.list(shared and shared.effects)) do
		if type(e) == "table" and e.key and TYPES[e.type] then
			-- save sprzed sterownika: boost przemysłu trzymał bazę w samym efekcie
			if e.type == "industryBoost" and e.baseline and industries.entries[e.entity] == nil then
				industries.entries[e.entity] = { baseline = e.baseline, factor = e.factor, target = e.target or e.baseline * e.factor,
					applied = e.applied }
			end
			e.baseline, e.target, e.applied, e.settle, e.rebases = nil, nil, nil, nil, nil
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
		if now >= e.untilTime then
			local t = TYPES[e.type]
			local ok, err = true, nil
			if t.revert then ok, err = pcall(t.revert, e) end
			util.log(string.format("effect %s ended%s", key, ok and "" or (" (revert failed: " .. tostring(err) .. ")")))
			active[key] = nil
			changed = true
		end
	end
	local ok, res = pcall(syncControllers)
	if not ok then util.log("effects: controllers failed: " .. tostring(res)) end
	if changed or (ok and res) then persist() end
end

local function keyOf(c)
	if type(c.target) == "string" and c.target ~= "" then return tostring(c.effect) .. ":" .. c.target end
	return tostring(c.effect) .. ":" .. tostring(tonumber(c.entity))
end

-- Efekt, którego nie udało się potwierdzić (commands.lua zwraca wtedy punkty): cofamy i usuwamy.
function E.abort(c)
	local key = keyOf(c)
	local e = active and active[key]
	if not e then return end
	local t = TYPES[e.type]
	if t.revert then pcall(t.revert, e) end
	active[key] = nil
	pcall(syncControllers)
	persist()
	util.log("effect " .. key .. " aborted")
end

-- "123,456" -> { 123, 456 } (tylko istniejące zakłady)
local function parseIndustries(s)
	local out = {}
	if type(s) ~= "string" then return out end
	for x in s:gmatch("[^,%s]+") do
		local id = tonumber(x)
		if id and util.comp(id, ct.INDUSTRY) then out[#out + 1] = id end
	end
	return out
end

local function union(a, b)
	local seen, out = {}, {}
	for _, l in ipairs({ a, b }) do
		for _, x in ipairs(l) do
			if not seen[x] then seen[x] = true; out[#out + 1] = x end
		end
	end
	return out
end

-- Grupa (zakłady / prędkość): sprawdzenie celu i verify po ustawieniu przez sterownik.
local function startGroup(t, e)
	if t.group == "industry" then
		for _, id in ipairs(targets(e)) do
			if not util.comp(id, ct.INDUSTRY) then return nil, "not an industry" end
		end
		if #targets(e) == 0 then return nil, "no industries" end
		return function()
			for _, id in ipairs(targets(e)) do
				if not industries.reached(id) then return false end
			end
			return true
		end
	end
	local vs = util.lineVehicles(e.entity)
	if #vs == 0 then return nil, "line has no vehicles" end
	return function()
		for _, v in ipairs(vs) do
			if util.comp(v, ct.TRANSPORT_VEHICLE) and not vehicles.reached(v) then return false end
		end
		return true
	end
end

local function describeGroup(t, e)
	local ids = t.group == "industry" and targets(e) or util.lineVehicles(e.entity)
	local ctl = t.group == "industry" and industries or vehicles
	return string.format("x%s on %d object(s), e.g. %s", fmt(e.factor), #ids, ids[1] and ctl.describe(ids[1]) or "-")
end

-- Komenda "effect" z kolejki: { effect, entity, months, factor, user } (+ entities = "1,2,3", target = klucz
-- dla efektów multi). Zwraca verify albo nil, błąd (jak rodzaje komend w commands.lua), a na końcu info
-- { until = data końca w kalendarzu gry } dla wiadomości na czacie.
function E.start(c, now)
	if active == nil then return nil, "effects not loaded yet" end
	local t = TYPES[c.effect]
	local entity, months = tonumber(c.entity), tonumber(c.months)
	if not t or not entity or not months or now == nil then return nil, "bad arguments" end
	if t.multi and (type(c.target) ~= "string" or c.target == "") then return nil, "bad arguments" end
	months = math.max(MIN_MONTHS, math.min(MAX_MONTHS, months))
	local factor = nil -- tylko efekty z mnożnikiem (range)
	if t.range then
		factor = tonumber(c.factor)
		if not factor then return nil, "bad arguments" end
		factor = math.max(t.range[1], math.min(t.range[2], factor))
	end
	local list = t.multi and parseIndustries(c.entities) or nil
	if t.multi and #list == 0 then return nil, "no industries" end
	local key = keyOf(c)

	local existing = active[key]
	if existing then -- przedłużenie; siła tylko mocniej (np. po zmianie mnożnika w ustawieniach)
		existing.untilTime = math.max(existing.untilTime, now) + months * monthLength()
		local stronger = factor and existing.factor and math.abs(factor - 1) > math.abs(existing.factor - 1) + 1e-6
		if stronger then
			existing.factor = factor
			if t.upgrade then t.upgrade(existing) end
		end
		if list then existing.entities = union(existing.entities or {}, list) end -- nowe zakłady ofiary też
		if type(c.user) == "string" and c.user ~= "" then existing.user = c.user end
		if t.group then syncControllers() end
		persist()
		util.log(string.format("effect %s extended by %d months%s", key, months, stronger and (", now x" .. fmt(factor)) or ""))
		return function() return true end, nil, { ["until"] = util.date(existing.untilTime) }
	end

	local e = { key = key, type = c.effect, entity = entity, entities = list, untilTime = now + months * monthLength(),
		startTime = now, factor = factor, user = type(c.user) == "string" and c.user or nil }
	local verify, err
	if t.group then
		verify, err = startGroup(t, e)
		if verify then
			active[key] = e
			syncControllers()
		end
	else
		verify, err = t.apply(e)
		if verify then active[key] = e end
	end
	if not verify then return nil, err end
	persist()
	local desc = t.group and describeGroup(t, e) or (t.describe and t.describe(e))
	util.log(string.format("effect %s started for %d months%s", key, months, desc and (": " .. desc) or ""))
	return verify, nil, { ["until"] = util.date(e.untilTime) }
end

-- Aktywne efekty do eksportu dynamicznego (strona pokazuje je przy mieście, zakładzie i linii);
-- efekt na wielu zakładach (nalot skarbówki) to osobny wpis przy każdym z nich.
function E.list()
	local out = {}
	for _, e in pairs(active or {}) do
		for _, id in ipairs(targets(e)) do
			out[#out + 1] = {
				type = e.type, entity = id, user = e.user, factor = e.factor,
				startTime = e.startTime, untilTime = e.untilTime, ["until"] = util.date(e.untilTime),
			}
		end
	end
	table.sort(out, function(a, b) return a.untilTime < b.untilTime end)
	return out
end

return E
