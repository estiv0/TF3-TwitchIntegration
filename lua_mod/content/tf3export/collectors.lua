-- Collectors: each returns plain Lua tables ready for json.encode.
-- Whitelist - we only take entities with a specific component.
-- Component fields match api/tealdef/api/engine.d.tl from the TF3 install.

local util = ug_require "util.lua"
local json = ug_require "json.lua"
local ct = util.ct

local C = {}

---------------------------------------------------------------- cargo
-- CargoTypeId -> resource name (e.g. "::/cargos/logs/logs.cargo"), cached.
local cargoNames = {}
function C.cargoName(id)
	if id == nil then return nil end
	local key = util.scalar(id)
	if cargoNames[key] == nil then
		local ok, name = pcall(function() return api.res.cargoTypeRep.getName(id) end)
		cargoNames[key] = (ok and name) or tostring(key)
	end
	return cargoNames[key]
end

---------------------------------------------------------------- player
function C.player()
	local id = api.engine.util.getPlayer()
	local balance
	local ok, b = pcall(function() return api.engine.util.finance.getPlayersBalance(id) end)
	if ok then balance = b end
	return {
		id = id,
		name = util.name(id),
		balance = balance, -- nil = unlimited money
	}
end

---------------------------------------------------------------- company
-- JournalEntry enum names (scripts/journal.d.tl) - the game scripts compare them with ==.
local JOURNAL = {
	Type = { "LOAN", "INTEREST", "CONSTRUCTION", "ACQUISITION", "MAINTENANCE", "INCOME", "OTHER", "SUBSIDY" },
	Maintenance = { "VEHICLE", "INFRASTRUCTURE", "OTHER", "VEHICLE_MAINTENANCE" },
	Construction = { "STREET", "TRACK", "SIGNAL", "STATION", "DEPOT", "BULLDOZER", "WAREHOUSE", "OTHER" },
	Carrier = { "ROAD", "RAIL", "TRAM", "OTHER", "AIR", "WATER" },
}

local function journalName(kind, value)
	if value == nil then return nil end
	local je = api.type and api.type.JournalEntry
	local e = je and util.field(je, kind)
	if e then
		for _, n in ipairs(JOURNAL[kind]) do
			local ok, eq = pcall(function() return e[n] == value end)
			if ok and eq then return n end
		end
	end
	return util.scalar(value)
end

local function amounts(v)
	local out = {}
	for i, x in ipairs(util.list(v)) do out[i] = util.round(x, 0) end
	return out
end

-- The table from the Finances window (game_mechanics/finance/finances_table.tl): columns = years (header), rows =
-- { carrier, type, maintenance, construction, values } from the transport (per carrier), investment
-- and other categories; plus profit (total), balance, interest, loan.
function C.financeTable(years)
	local player = api.engine.util.getPlayer()
	local config = api.type.ChartConfig.new()
	config.count = years
	local fd = api.engine.util.finance.computeFinanceTable(player, config)
	local rows = {}
	local function addRow(carrier, key, values)
		local k = util.list(fd:unfoldKey(key))
		local typ = journalName("Type", k[1])
		local maintenance = typ == "MAINTENANCE" and journalName("Maintenance", k[2]) or nil
		rows[#rows + 1] = {
			carrier = carrier,
			type = typ,
			maintenance = maintenance,
			construction = (typ == "CONSTRUCTION" or maintenance == "INFRASTRUCTURE") and journalName("Construction", k[3]) or nil,
			values = amounts(values),
		}
	end
	fd:foreach_carrier(function(carrier)
		local name = journalName("Carrier", carrier)
		fd:foreach_transport(function(key, values) addRow(name, key, values) end, carrier)
	end)
	fd:foreach_investment(function(key, values) addRow(nil, key, values) end)
	fd:foreach_other(function(_, values) rows[#rows + 1] = { type = "OTHER", values = amounts(values) } end)
	local header = {}
	for i, h in ipairs(util.list(fd.header)) do header[i] = tostring(h) end
	return {
		header = header,
		rows = rows,
		total = amounts(fd.total),
		balance = amounts(fd.balance),
		interest = amounts(fd.interest),
		loan = amounts(fd.loan),
	}
end

-- History from the world logbook (charts from the Finances/Towns windows): name -> [[year, value], ...], the year as
-- a fractional number (1921.5 = the middle of 1921). At most maxPoints points per series (evenly thinned,
-- always the last one). ChartResult.series[i] is a pair of lists { game times, values } (by default 50 points
-- from the start of the game until now, values averaged per bucket); time is turned into a date with util.date.
function C.worldHistory(names, maxPoints)
	local res = api.engine.util.logbook.getChart(api.engine.util.getWorld(), names)
	local out = {}
	for i, series in ipairs(util.list(res and res.series)) do
		local xs, ys = util.list(util.field(series, 1)), util.list(util.field(series, 2))
		local n = math.min(#xs, #ys)
		local step = math.max(1, math.ceil(n / maxPoints))
		local o = {}
		local function add(j)
			local x, y = xs[j], ys[j]
			local d = type(x) == "number" and util.date(x)
			if d and type(d.year) == "number" and type(y) == "number" then
				o[#o + 1] = { util.round(d.year + ((d.month or 1) - 1) / 12 + ((d.day or 1) - 1) / 365, 3), util.round(y, 0) }
			end
		end
		for j = 1, n, step do add(j) end
		if n > 0 and (n - 1) % step ~= 0 then add(n) end
		if names[i] then out[names[i]] = o end
	end
	return out
end

---------------------------------------------------------------- towns
-- cargoNeeds: {{{CargoTypeId, number}}} - a list (by building type) of lists of {cargo, value} pairs
local function cargoNeeds(needs)
	local out = {}
	for i, group in ipairs(util.list(needs)) do
		local g = {}
		for _, pair in ipairs(util.list(group)) do
			local cargo = util.field(pair, 1)
			if cargo ~= nil then
				g[C.cargoName(cargo)] = util.round(util.field(pair, 2), 3)
			end
		end
		out[i] = next(g) and g or json.EMPTY_OBJECT
	end
	return out
end

-- Current district capacities {residential, commercial, industrial} of all towns, in one call.
-- These grow with the town (sum = the size from the town window); TOWN.initialLandUseCapacities
-- are only the starting values.
local function townCapacities()
	local ok, map = pcall(function() return api.engine.system.townBuildingSystem.getTown2personCapacitiesMap() end)
	if not ok or map == nil then
		util.log("getTown2personCapacitiesMap failed: " .. tostring(map))
		return {}
	end
	local out = {}
	for town, caps in util.pairs(map) do
		out[town] = util.list(caps)
	end
	return out
end

-- Town needs satisfaction: [{ cargo, supply, demand }] as in the game's town statistics
-- (townBuildingSystem.getCargoSupplyAndLimit, all zones together; game_mechanics/towns/town_util.tl).
-- demand -1 = no limit. nil when the API fails.
local function townSupply(id)
	local ok, map = pcall(function() return api.engine.system.townBuildingSystem.getCargoSupplyAndLimit(id) end)
	if not ok or map == nil then return nil end
	local out = {}
	for cargo, v in util.pairs(map) do
		local supply, limit = util.field(v, 1), util.field(v, 2)
		if type(supply) == "number" and type(limit) == "number" then
			out[#out + 1] = { cargo = C.cargoName(cargo), supply = math.ceil(supply), demand = math.floor(limit) }
		end
	end
	table.sort(out, function(a, b) return a.demand > b.demand end)
	return out
end

function C.towns()
	local out = {}
	local current = townCapacities()
	for _, id in ipairs(util.entities(ct.TOWN)) do
		local t = util.comp(id, ct.TOWN)
		if t then
			local initial = util.list(t.initialLandUseCapacities)
			out[#out + 1] = {
				id = id,
				name = util.name(id),
				pos = util.center(id),
				noise = util.round(t.noise),
				pollution = util.round(t.pollution),
				growing = t.developmentActive,
				capacities = current[id] or initial,
				initialCapacities = initial,
				cargoNeeds = cargoNeeds(t.cargoNeeds),
				supply = townSupply(id),
			}
		end
	end
	return out
end

---------------------------------------------------------------- industry
local function stockRules(stockList)
	local rules = {}
	if not stockList then return rules end
	for _, r in ipairs(util.list(stockList.rules)) do
		local output = {}
		for cargo, amount in util.pairs(r.output) do
			output[C.cargoName(cargo)] = amount
		end
		local input = {}
		for i, group in ipairs(util.list(r.input)) do
			input[i] = util.list(group)
		end
		rules[#rules + 1] = {
			input = input,
			output = next(output) and output or json.EMPTY_OBJECT,
			capacity = r.capacity,
			booster = r.booster,
		}
	end
	return rules
end

-- Industry stocks: cargo, capacity, current level (simEntityAtStockSystem, StockId from 0),
-- input (input stock) and consumedYear (consumption per year, stockDatas[i].consumeFrequency -
-- the same the industry window shows as actual consumption).
local function stocks(stockList, stockEntity)
	local out = {}
	if not stockList then return out end
	local enums = api.type and api.type["enum"]
	local inputType = enums and util.field(util.field(enums, "StockListType"), "InputStock")
	local datas = util.list(stockList.stockDatas)
	for i, s in ipairs(util.list(stockList.stocks)) do
		local amount
		if stockEntity then
			local ok, n = pcall(function() return api.engine.system.simEntityAtStockSystem.getStockCount(stockEntity, i - 1) end)
			if ok and type(n) == "number" then amount = n end
		end
		local consume = datas[i] and util.field(datas[i], "consumeFrequency")
		out[#out + 1] = {
			cargo = C.cargoName(s.cargoType),
			capacity = s.capacity,
			amount = amount,
			input = inputType ~= nil and s.type == inputType or nil,
			consumedYear = type(consume) == "number" and consume > 0 and util.round(consume, 0) or nil,
		}
	end
	return out
end

-- Actual production per year: cargo -> stockList.cargoData[cargo].produceFrequency.
local function producedYear(stockList)
	local out = {}
	if not stockList then return json.EMPTY_OBJECT end
	for cargo, d in util.pairs(stockList.cargoData) do
		local f = util.field(d, "produceFrequency")
		if type(f) == "number" and f > 0 then out[C.cargoName(cargo)] = util.round(f, 0) end
	end
	return next(out) and out or json.EMPTY_OBJECT
end

-- Counters from the industry logbook for the last year (the "Cargo flow" chart in the industry window):
-- produced, consumed, wasted (not picked up). No entry = nil.
local function yearLog(entity)
	local function value(name)
		local ok, v = pcall(function() return api.engine.util.logbook.getLogValuePerYear(entity, name) end)
		return ok and type(v) == "number" and v or nil
	end
	return { produced = value("itemsProduced"), consumed = value("itemsConsumed"), destroyed = value("itemsDestroyed") }
end

function C.industries()
	local out = {}
	for _, id in ipairs(util.entities(ct.INDUSTRY)) do
		local ind = util.comp(id, ct.INDUSTRY)
		if ind then
			local constr = util.comp(ind.construction, ct.CONSTRUCTION)
			local stock = util.comp(ind.stockList, ct.STOCK_LIST)
			local modifiers = stock and util.field(stock, "modifiers")
			out[#out + 1] = {
				id = id,
				name = util.name(id) or util.name(ind.construction),
				file = constr and util.scalar(constr.fileName),
				pos = util.center(ind.construction) or util.center(id),
				level = ind.level,
				maxLevel = ind.maxLevel,
				upgrade = util.round(ind.upgradeProgress, 3),
				manual = ind.manualDevelopment,
				productivity = modifiers and util.round(util.field(modifiers, "productivity"), 3),
				rules = stockRules(stock),
				stocks = stocks(stock, ind.stockList),
				producedYear = producedYear(stock),
				year = yearLog(ind.stockList),
			}
		end
	end
	return out
end

-- What a construction produces: the output cargos of its rules without boosters (updateScript.params.stockListConfig,
-- as in the base .con files), sorted; empty when unknown.
local function constructionOutputs(id)
	local ok, rules = pcall(function()
		local desc = api.res.constructionRep.getAsTable(id)
		return desc.updateScript.params.stockListConfig.rules
	end)
	local set, out = {}, {}
	for _, rule in ipairs(ok and type(rules) == "table" and rules or {}) do
		if type(rule) == "table" and not rule.booster and type(rule.output) == "table" then
			for cargo in pairs(rule.output) do
				if type(cargo) == "string" and not set[cargo] then set[cargo] = true; out[#out + 1] = cargo end
			end
		end
	end
	table.sort(out)
	return out
end

-- Every industry the game can build (visible "::/industries/x/x.con" constructions, like the sandbox menu), also
-- those not on this map: { file, name, outputs } sorted by file. Resources don't change while the game runs, so cached.
local industryTypes
function C.industryTypes()
	if industryTypes then return industryTypes end
	local out = {}
	local ok, all = pcall(function() return api.res.constructionRep.getAll() end)
	for id, res in pairs(ok and type(all) == "table" and all or {}) do
		local kind = type(res) == "string" and res:match("industries/([%w_]+)/%1%.con$")
		if kind then
			local okN, name = pcall(function() return api.res.constructionRep.get(id).description.name end)
			out[#out + 1] = {
				file = "::/industries/" .. kind .. "/" .. kind .. ".con",
				name = okN and type(name) == "string" and name or nil,
				outputs = constructionOutputs(id),
			}
		end
	end
	table.sort(out, function(a, b) return a.file < b.file end)
	if ok then industryTypes = out end -- after an error try again next time
	return out
end

---------------------------------------------------------------- warehouses
-- The player's warehouses (WAREHOUSE component: stockList + construction), like the game's warehouse window
-- (gui/main/cargo_util.tl, forEachCargoInStock): a warehouse has several stores (stocks), a store has
-- a capacity and one or more allowed cargos (getStockCargoTypes); the level is counted per cargo.

-- Construction -> warehouse entity (to recognize warehouse stations in C.stations).
function C.warehouseConstructions()
	local out = {}
	for _, id in ipairs(util.entities(ct.WAREHOUSE)) do
		local w = util.comp(id, ct.WAREHOUSE)
		if w and w.construction then out[w.construction] = id end
	end
	return out
end

local function warehouseStocks(sl)
	local out, cargo, capacity, amount = {}, {}, 0, 0
	local stockList = util.comp(sl, ct.STOCK_LIST)
	for i, s in ipairs(util.list(stockList and stockList.stocks)) do
		local stockId = i - 1
		local okT, types = pcall(function() return api.engine.util.stock.getStockCargoTypes(sl, stockId) end)
		local byCargo, sum = {}, 0
		for _, c in ipairs(okT and util.list(types) or {}) do
			local ok, n = pcall(function()
				return api.engine.system.simEntityAtStockSystem.getStockCountForCargoType(sl, stockId, c)
			end)
			if ok and type(n) == "number" and n > 0 then
				local name = C.cargoName(c)
				byCargo[name] = n
				cargo[name] = (cargo[name] or 0) + n
				sum = sum + n
			end
		end
		if sum == 0 then -- no cargo list (or the API failed): at least the store's total level
			local ok, n = pcall(function() return api.engine.system.simEntityAtStockSystem.getStockCount(sl, stockId) end)
			if ok and type(n) == "number" then sum = n end
		end
		local cap = type(s.capacity) == "number" and s.capacity or 0
		capacity, amount = capacity + cap, amount + sum
		local cargos = {}
		for _, c in ipairs(okT and util.list(types) or {}) do cargos[#cargos + 1] = C.cargoName(c) end
		out[#out + 1] = {
			cargos = cargos, -- allowed cargo (empty = a store without a cargo set yet)
			capacity = cap,
			amount = sum,
			cargo = next(byCargo) and byCargo or json.EMPTY_OBJECT,
		}
	end
	return out, (next(cargo) and cargo or json.EMPTY_OBJECT), capacity, amount
end

function C.warehouses()
	local out = {}
	for _, id in ipairs(util.entities(ct.WAREHOUSE)) do
		local w = util.comp(id, ct.WAREHOUSE)
		if w then
			local stocks, cargo, capacity, amount = warehouseStocks(w.stockList)
			local function perYear(name)
				local ok, v = pcall(function() return api.engine.util.logbook.getLogValuePerYear(id, name) end)
				return ok and type(v) == "number" and v or nil
			end
			local constr = util.comp(w.construction, ct.CONSTRUCTION)
			out[#out + 1] = {
				id = id,
				construction = w.construction,
				name = util.name(id) or util.name(w.construction),
				file = constr and util.scalar(constr.fileName),
				pos = util.center(w.construction) or util.center(id),
				capacity = capacity,
				amount = amount,
				cargo = cargo, -- cargo -> amount now (all stores together)
				stocks = stocks,
				year = { unloaded = perYear("itemsUnloaded"), loaded = perYear("itemsLoaded") }, -- brought in / shipped out
			}
		end
	end
	return out
end

---------------------------------------------------------------- stations
-- Stations with their own construction: train stations, bus/truck stations, harbors,
-- airports. Street stops have no construction or are on the STOP_PATHS list - we skip them.
-- It's unknown whether STATION can be listed (LINE and BASE_EDGE couldn't), so we take
-- the station -> construction map. One construction (e.g. an airport) can be several stations,
-- so a row = a construction.

-- The kind comes first from the construction path (base/content/stations/<air|water|rail|street>/...).
-- getCarriers(group, -1, -1) returns the sum for the whole station GROUP, and a bus station
-- grouped with a train station would then get RAIL - so the carriers are only a fallback (mods).
local STATION_PATHS = { { "/air/", "AIR" }, { "/water/", "WATER" }, { "/rail/", "RAIL" }, { "/street/", "ROAD" } }
local STATION_KINDS = { "AIR", "WATER", "RAIL", "ROAD", "TRAM" } -- order = priority
-- Stops that are constructions: small stops and the underground stop ("Underground Stop").
local STOP_PATHS = { "small_stops", "underground_station" }

local function isStop(file)
	for _, p in ipairs(STOP_PATHS) do
		if file and string.find(file, p, 1, true) then return true end
	end
	return false
end

-- The industry the station construction belongs to (a station built into an industry), or nil.
-- A station can be a sub-construction, so the parent construction is checked too.
local function parentConstruction(constrId)
	local ok, parent = pcall(function()
		return api.engine.system.streetConnectorSystem.getConstructionEntityForSubconstruction(constrId)
	end)
	return ok and parent and parent > 0 and parent ~= constrId and parent or nil
end

local function stationIndustry(constrId, constr)
	local first = constr and util.list(util.field(constr, "industries"))[1]
	if first then return first end
	local parent = parentConstruction(constrId)
	if parent then
		local pc = util.comp(parent, ct.CONSTRUCTION)
		return pc and util.list(util.field(pc, "industries"))[1] or nil
	end
	return nil
end

-- The warehouse the station construction (itself or its parent) belongs to, or nil.
local function stationWarehouse(constrId, warehouses)
	if warehouses[constrId] then return warehouses[constrId] end
	local parent = parentConstruction(constrId)
	return parent and warehouses[parent] or nil
end

-- Passengers waiting in station groups: (all, unhappy) or nil when the API fails.
-- The game's station statistics window counts them the same way (gui/statistics/statistic_stations.tl).
local passengerCargo
local function passengerCargoId()
	if passengerCargo == nil then
		local ok, id = pcall(function() return api.res.cargoTypeRep.getPassengerCargoTypeId() end)
		passengerCargo = ok and id or false
	end
	return passengerCargo or nil
end

-- Passengers waiting for a given line at its stop (stopIndex from 0, like vehicles[].stop), or nil.
local function lineStopWaiting(line, stopIndex)
	local cargo = passengerCargoId()
	if not cargo then return nil end
	local ok, n = pcall(function()
		return api.engine.system.simEntityAtTerminalSystem.getLineStopSimEntitiesCount(line, stopIndex, cargo)
	end)
	return ok and tonumber(n) or nil
end

local function waitingPassengers(groups)
	local passengerCargo = passengerCargoId()
	if not passengerCargo then return nil end
	local total, bad, any = 0, 0, false
	for _, group in ipairs(groups) do
		local ok, q = pcall(function() return api.engine.util.cargo.getCargoQualityDataAtStationGroup(group, passengerCargo) end)
		if ok and q then
			any = true
			total = total + (tonumber(util.field(q, "countTotal")) or 0)
			bad = bad + (tonumber(util.field(q, "countBad")) or 0)
		end
	end
	if not any then return nil end
	return total, bad
end

local function stationKind(carriers, file)
	for _, p in ipairs(STATION_PATHS) do
		if file and string.find(file, p[1], 1, true) then return p[2] end
	end
	local set = {}
	for _, c in ipairs(carriers) do set[c] = true end
	for _, k in ipairs(STATION_KINDS) do
		if set[k] then return k end
	end
	return nil
end

-- Returns the list of stations and a station entity -> kind map (for the carrier of a line without vehicles).
function C.stations()
	local ok, map = pcall(function() return api.engine.system.streetConnectorSystem.getStation2ConstructionMap() end)
	if not ok or map == nil then
		util.log("getStation2ConstructionMap failed: " .. tostring(map))
		return {}, {}
	end

	local byConstr, order = {}, {}
	for station, constr in util.pairs(map) do
		if constr and constr > 0 then
			if not byConstr[constr] then byConstr[constr] = {}; order[#order + 1] = constr end
			table.insert(byConstr[constr], station)
		end
	end
	table.sort(order)

	local okW, warehouses = pcall(C.warehouseConstructions)
	if not okW then warehouses = {} end

	local out, kindOf = {}, {}
	for _, constrId in ipairs(order) do
		local stations = byConstr[constrId]
		table.sort(stations)
		local constr = util.comp(constrId, ct.CONSTRUCTION)
		local file = constr and util.scalar(constr.fileName)
		file = file and tostring(file)
		if not isStop(file) then
			local carriers, carrierSet, groups, groupSet = {}, {}, {}, {}
			local passengers, cargo = false, false
			for _, st in ipairs(stations) do
				local okG, group = pcall(function() return api.engine.system.stationGroupSystem.getStationGroup(st) end)
				if okG and group and group > 0 then
					if not groupSet[group] then groupSet[group] = true; groups[#groups + 1] = group end
				end
				local s = util.comp(st, ct.STATION)
				for _, t in ipairs(util.list(s and s.terminals)) do
					if util.field(t, "passengersLoad") or util.field(t, "passengersUnload") then passengers = true end
					if util.field(t, "cargoLoad") or util.field(t, "cargoUnload") then cargo = true end
				end
			end
			for _, group in ipairs(groups) do
				local okC, res = pcall(function() return api.engine.system.stationGroupSystem.getCarriers(group, -1, -1) end)
				if okC and res then
					for _, c in ipairs(util.list(util.field(res, 1))) do
						local name = util.enumName("Carrier", c, util.CARRIERS)
						if name and not carrierSet[name] then carrierSet[name] = true; carriers[#carriers + 1] = name end
					end
				end
			end

			local kind = stationKind(carriers, file)
			local industry = stationIndustry(constrId, constr)
			local warehouse = stationWarehouse(constrId, warehouses)
			if kind or industry or warehouse then -- an industry / warehouse station stays, even without a recognized kind
				for _, st in ipairs(stations) do kindOf[st] = kind end
				local waiting, waitingBad
				if passengers then waiting, waitingBad = waitingPassengers(groups) end
				out[#out + 1] = {
					id = constrId,
					kind = kind or "OTHER",
					industry = industry, -- industry id (industries[].id) when the station belongs to it
					warehouse = warehouse, -- warehouse id (warehouses[].id) when the station belongs to it
					carriers = carriers, -- carriers of the whole station group
					name = (groups[1] and util.name(groups[1])) or util.name(constrId),
					file = file,
					pos = util.center(constrId) or util.center(stations[1]),
					owner = util.owner(constrId) or util.owner(stations[1]),
					passengers = passengers,
					cargo = cargo,
					waiting = waiting, -- passengers waiting in the station group
					waitingBad = waitingBad, -- those of them who are unhappy (waiting too long)
					groups = groups,
					stations = stations,
				}
			end
		end
	end
	return out, kindOf
end

---------------------------------------------------------------- lines and vehicles
function C.vehicles()
	local out = {}
	for _, id in ipairs(util.entities(ct.TRANSPORT_VEHICLE)) do
		local v = util.comp(id, ct.TRANSPORT_VEHICLE)
		if v then
			out[#out + 1] = {
				id = id,
				line = v.line,
				stop = v.stopIndex,
				stopped = v.userStopped,
				state = util.enumName("TransportVehicleState", v.state, util.VEHICLE_STATES),
				carrier = util.enumName("Carrier", v.carrier, util.CARRIERS),
				owner = util.owner(id),
			}
		end
	end
	return out
end

-- Adds pos (m, to 1 m) and speed (m/s) from api.engine.util.vehicle to a vehicle (an entry from C.vehicles).
-- Without them the website guesses the location from the stop number, and on a long segment (train, ship)
-- that doesn't change for minutes. Vehicles in a depot are skipped. false = API unavailable.
function C.vehiclePosition(v)
	local vu = api.engine.util.vehicle
	if not vu then return false end
	if v.state == "IN_DEPOT" then return true end
	local okP, p = pcall(vu.getPosition, v.id)
	if okP and p then
		local x, y = util.field(p, "x"), util.field(p, "y")
		if type(x) == "number" and type(y) == "number" then
			v.pos = { util.round(x, 0), util.round(y, 0), util.round(util.field(p, "z") or 0, 0) }
		end
	end
	local okS, s = pcall(vu.getSpeed, v.id)
	if okS then v.speed = util.round(s, 1) end
	return true
end

-- Model from api.res: { name, lifespan (in gameTime units), icon }, cached (there are a few dozen models).
-- Name and lifespan as in the vehicle window (gui/line_vehicle_mgmt/vehicle_util.tl, vehicle_store_util.tl).
local modelCache = {}
local function modelInfo(modelId)
	local key = util.scalar(modelId)
	if key == nil then return {} end
	if modelCache[key] == nil then
		local info = {}
		pcall(function()
			local md = api.res.modelRep.get(modelId).metadata
			info.name = md.description.name
			info.lifespan = md.maintenance.lifespan * 1000
		end)
		-- "vehicle/bus/mb_o405g.mdl" -> "mb_o405g": the icon name on the website (web/img/vehicles/<icon>.png,
		-- extracted from the game files by tools/extract_game_icons.py)
		local okF, file = pcall(function() return api.res.modelRep.getName(modelId) end)
		if okF and type(file) == "string" then info.icon = file:match("([^/\\]+)%.mdl$") end
		modelCache[key] = info
	end
	return modelCache[key]
end

-- The multiple unit name from the file in transportVehicleConfig.muFileNames, cached.
local muCache = {}
local function muName(file)
	if muCache[file] == nil then
		local ok, name = pcall(function()
			local rep = api.res.multipleUnitRep
			return rep.get(rep.find(file)).name
		end)
		muCache[file] = (ok and type(name) == "string" and name ~= "") and name or false
	end
	return muCache[file] or nil
end

-- Game date as a fraction of a year (2031.5 = about 1 July 2031); nil without a calendar.
local function fracYear(t)
	local d = util.date(t)
	if not d or type(d.year) ~= "number" then return nil end
	return d.year + ((d.month or 1) - 1) / 12 + ((d.day or 1) - 1) / 365
end
C.fracYear = fracYear

-- Adds details from the consist (transportVehicleConfig) and config to a vehicle (an entry from C.vehicles):
--   model    = model id of the lead unit (name and icon in models)
--   parts    = number of parts in the consist (locomotive + wagons), only when more than 1
--   age      = age of the oldest part in game calendar years (like "Age" in the vehicle window)
--   wear     = age / lifespan of that part (1 = end of lifespan, like the percentage in the line window)
--   maint    = average maintenance state of the parts, 0..1 (vehicle_util.getAvgMaintenanceState)
--   cap      = capacity by cargo { [cargo name] = count }
--   load     = what it carries now, the same keys as cap (simEntityAtVehicleSystem)
-- models: a "model id" -> { name, icon } table (string keys, so JSON gives an object), filled in with this vehicle's models.
function C.vehicleDetails(v, now, nowYear, models)
	local tv = util.comp(v.id, ct.TRANSPORT_VEHICLE)
	if not tv then return end
	local cfgTv = util.field(tv, "transportVehicleConfig")
	local parts = util.list(cfgTv and util.field(cfgTv, "vehicles"))
	if #parts > 0 then
		local head = util.field(util.field(parts[1], "part"), "modelId")
		v.model = util.scalar(head)
		if #parts > 1 then v.parts = #parts end
		local key = v.model ~= nil and tostring(v.model)
		if key and models[key] == nil then
			local mu = util.list(util.field(cfgTv, "muFileNames"))[1]
			local info = modelInfo(head)
			models[key] = { name = (type(mu) == "string" and mu ~= "" and muName(mu)) or info.name, icon = info.icon }
		end
		-- the oldest part (with equal purchase time the one with the shorter lifespan), like getMinPurchaseTimeAndLifespan
		local bought, lifespan, maintSum, maintN = nil, nil, 0, 0
		for _, p in ipairs(parts) do
			local t = util.field(p, "purchaseTime")
			local life = modelInfo(util.field(util.field(p, "part"), "modelId")).lifespan
			if type(t) == "number" and (bought == nil or t < bought or (t == bought and life and (lifespan == nil or life < lifespan))) then
				bought, lifespan = t, life
			end
			local m = util.field(p, "maintenanceState")
			if type(m) == "number" then maintSum, maintN = maintSum + m, maintN + 1 end
		end
		if bought then
			local boughtYear = fracYear(bought)
			if nowYear and boughtYear then v.age = util.round(math.max(nowYear - boughtYear, 0), 1) end
			if lifespan and lifespan > 0 then v.wear = util.round(math.max(now - bought, 0) / lifespan, 2) end
		end
		if maintN > 0 then v.maint = util.round(maintSum / maintN, 3) end
	end
	-- capacities: a 1-based Lua array, CargoTypeId = index - 1 (like gui/main/cargo_util.tl); cargo on board
	-- with the same call as the game window, one per cargo type with capacity
	local sim = api.engine.system.simEntityAtVehicleSystem
	local cap, load = {}, {}
	for index, n in util.pairs(util.field(util.field(tv, "config"), "capacities")) do
		if type(index) == "number" and type(n) == "number" and n > 0 then
			local cargo = C.cargoName(index - 1)
			cap[cargo] = n
			local ok, used = pcall(function() return sim.getVehicleSimEntitiesCountForCargoType(v.id, index - 1) end)
			if ok and type(used) == "number" then load[cargo] = used end
		end
	end
	if next(cap) then v.cap = cap end
	if next(load) then v.load = load end
end

-- Line statistics as in the line window and the game statistics (gui/line_vehicle_mgmt/line_react_util.tl,
-- gui/statistics/statistic_lines.tl). prevYear = { year, from, to } or nil.
-- Each field in its own pcall: an error in one API doesn't wipe the rest.
function C.lineStats(id, now, prevYear)
	local function try(f)
		local ok, v = pcall(f)
		if ok and type(v) == "number" then return v end
		return nil
	end
	local finance = api.engine.util.finance
	local yearLen = try(function() return api.util.getDefaultYearDuration() end)
	local s = {
		-- income minus maintenance over the last 12 months ("Profit" in the line window)
		profit12m = yearLen and try(function()
			return finance.calculateBalance({ id }, math.max(now - yearLen, 0), now, true)
		end),
		transported12m = try(function()
			return api.engine.util.logbook.getLogValuePerYear(id, "itemsTransported")
		end),
		rate = try(function() return api.engine.util.line.calcLineStationThroughput(id) end),
	}
	if prevYear then
		s.prevYear = prevYear.year
		s.profitPrevYear = try(function()
			return finance.calculateBalance({ id }, prevYear.from, prevYear.to, true)
		end)
	end

	-- vehicle load at the time of measurement, per cargo and in total
	local ok, usages = pcall(function() return api.engine.util.line.getLineCapacityUsages(id, false) end)
	if ok and usages then
		local used, capacity, cargo = 0, 0, {}
		-- this is a 1-based Lua array: CargoTypeId = index - 1 (as in gui/main/cargo_util.tl)
		for index, u in util.pairs(usages) do
			local uu, uc = util.field(u, "used"), util.field(u, "capacity")
			if type(index) == "number" and type(uu) == "number" and type(uc) == "number" and uc > 0 then
				used, capacity = used + uu, capacity + uc
				cargo[#cargo + 1] = { cargo = C.cargoName(index - 1), used = util.round(uu, 0), capacity = util.round(uc, 0) }
			end
		end
		table.sort(cargo, function(a, b) return a.capacity > b.capacity end)
		s.load = capacity > 0 and util.round(used / capacity, 3) or nil
		s.cargo = cargo
	end
	return s
end

-- Line profit over all finished years: from the start of the game to yearStart (the start of the current year).
-- It only changes at the turn of a year, so the script computes it once per game year (nil = no data).
function C.lineProfitTotal(id, yearStart)
	local ok, v = pcall(function() return api.engine.util.finance.calculateBalance({ id }, 0, yearStart, true) end)
	if ok and type(v) == "number" then return v end
	return nil
end

-- stationKinds: station entity -> kind (from C.stations); the carrier of a line without vehicles.
-- vehicles = nil (vehicles turned off): vehicleCount = nil, the carrier only from the station kind.
function C.lines(vehicles, stationKinds)
	-- vehicle count and line carrier from the already collected vehicle list (without a second pass)
	local perLine, carrier = {}, {}
	for _, v in ipairs(vehicles or {}) do
		if v.line then
			perLine[v.line] = (perLine[v.line] or 0) + 1
			carrier[v.line] = carrier[v.line] or v.carrier
		end
	end

	-- LINE can't be listed with getEntitiesWithComponent ("Cannot loop over this
	-- component type"). The game uses lineSystem.getLinesForPlayer; we add the lines found
	-- in vehicles (e.g. of other companies).
	local ids, seen = {}, {}
	local function addLine(id)
		if id and id > 0 and not seen[id] then seen[id] = true; ids[#ids + 1] = id end
	end
	local ok, playerLines = pcall(function()
		return api.engine.system.lineSystem.getLinesForPlayer(api.engine.util.getPlayer())
	end)
	if ok then
		for _, id in ipairs(util.list(playerLines)) do addLine(id) end
	else
		util.log("getLinesForPlayer failed: " .. tostring(playerLines))
	end
	for id in pairs(perLine) do addLine(id) end
	table.sort(ids)

	-- Stop position: stop.station is an index (from 0) into STATION_GROUP.stations.
	-- The station entity usually has no BOUNDING_VOLUME, so we go to its construction;
	-- as a last resort the center of the station group. Cached for one export (stations repeat).
	local stopPos, stopStation = {}, {}
	local function stationOf(group, index)
		local key = tostring(group) .. ":" .. tostring(index)
		if stopStation[key] == nil then
			local sg = util.comp(group, ct.STATION_GROUP)
			stopStation[key] = (sg and util.list(sg.stations)[(index or 0) + 1]) or false
		end
		return stopStation[key] or nil
	end
	local function stationPos(group, index)
		local key = tostring(group) .. ":" .. tostring(index)
		if stopPos[key] == nil then
			local station = stationOf(group, index)
			local pos = station and util.center(station)
			if not pos and station then
				local ok, constr = pcall(function()
					return api.engine.system.streetConnectorSystem.getConstructionEntityForStation(station)
				end)
				if ok and constr and constr > 0 then pos = util.center(constr) end
			end
			stopPos[key] = pos or util.center(group) or false
		end
		return stopPos[key] or nil
	end
	-- Passengers waiting at a street stop (a group without a station from C.stations - stations count them themselves).
	local stopWaiting = {}
	local function waitingAt(group, station)
		if stationKinds and station and stationKinds[station] then return nil end
		if stopWaiting[group] == nil then stopWaiting[group] = waitingPassengers({ group }) or false end
		return stopWaiting[group] or nil
	end

	local out = {}
	for _, id in ipairs(ids) do
		local l = util.comp(id, ct.LINE)
		if l then
			local stops = {}
			local lineCarrier = carrier[id]
			for i, s in ipairs(util.list(l.stops)) do
				if not lineCarrier and stationKinds then
					local st = stationOf(s.stationGroup, s.station)
					lineCarrier = st and stationKinds[st]
				end
				stops[i] = {
					group = s.stationGroup,
					station = s.station,
					terminal = s.terminal,
					entity = stationOf(s.stationGroup, s.station), -- station entity, cf. stations[].stations
					pos = stationPos(s.stationGroup, s.station),
					name = util.name(s.stationGroup),
					waiting = waitingAt(s.stationGroup, stationOf(s.stationGroup, s.station)),
					lineWaiting = lineStopWaiting(id, i - 1), -- waiting for exactly this line
				}
			end
			local color = util.comp(id, ct.COLOR)
			out[#out + 1] = {
				id = id,
				name = util.name(id),
				color = color and util.vec3(color.color),
				owner = util.owner(id),
				carrier = lineCarrier, -- from the vehicles, and without vehicles from the station kind (nil = street stops)
				stops = stops,
				vehicleCount = vehicles and (perLine[id] or 0) or nil,
			}
		end
	end
	return out
end

---------------------------------------------------------------- network (static)
-- Compact format: one edge = one array, field order in EDGE_FIELDS.
C.EDGE_FIELDS = {
	"id", "node0", "node1",
	"x0", "y0", "z0", "x1", "y1", "z1",
	"tx0", "ty0", "tz0", "tx1", "ty1", "tz1",
	"road",   -- "STREET" / "TRACK"
	"type",   -- "NORMAL" / "BRIDGE" / "TUNNEL"
	"owner",  -- 0 = no owner (town/world)
}

-- List of the IDs of all edges (streets + tracks).
-- BASE_EDGE can't be listed with getEntitiesWithComponent, so we collect the edges
-- from the node -> segments map (streetSystem.getNode2SegmentMap). One C call.
function C.edgeIds()
	local ok, map = pcall(function() return api.engine.system.streetSystem.getNode2SegmentMap() end)
	if not ok or map == nil then
		util.log("getNode2SegmentMap failed: " .. tostring(map))
		return {}
	end
	local ids, seen = {}, {}
	for _, edges in util.pairs(map) do
		for _, e in ipairs(util.list(edges)) do
			if not seen[e] then seen[e] = true; ids[#ids + 1] = e end
		end
	end
	table.sort(ids)
	return ids
end

-- Returns a row, or nil when the entity is gone or has no position (the edge is skipped).
function C.edge(id)
	local e = util.comp(id, ct.BASE_EDGE)
	if not e then return nil end
	local p0, p1 = util.vec3(e.position0), util.vec3(e.position1)
	if not p0 or not p1 then return nil end
	local t0 = util.vec3(e.tangent0) or { 0, 0, 0 }
	local t1 = util.vec3(e.tangent1) or { 0, 0, 0 }
	return {
		id, e.node0 or 0, e.node1 or 0,
		p0[1], p0[2], p0[3], p1[1], p1[2], p1[3],
		t0[1], t0[2], t0[3], t1[1], t1[2], t1[3],
		util.enumName("RoadType", e.roadType, util.ROAD_TYPES),
		util.enumName("BaseEdgeType", e.type, util.EDGE_TYPES),
		util.owner(id) or 0,
	}
end

return C
