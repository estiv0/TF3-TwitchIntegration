-- Kolektory: każdy zwraca zwykłe tabele Lua gotowe do json.encode.
-- Biała lista - pobieramy tylko encje z konkretnym komponentem.
-- Pola komponentów zgodne z api/tealdef/api/engine.d.tl z instalacji TF3.

local util = ug_require "util.lua"
local json = ug_require "json.lua"
local ct = util.ct

local C = {}

---------------------------------------------------------------- ładunki
-- CargoTypeId -> nazwa zasobu (np. "::/cargos/logs/logs.cargo"), z cache.
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

---------------------------------------------------------------- gracz
function C.player()
	local id = api.engine.util.getPlayer()
	local balance
	local ok, b = pcall(function() return api.engine.util.finance.getPlayersBalance(id) end)
	if ok then balance = b end
	return {
		id = id,
		name = util.name(id),
		balance = balance, -- nil = nieskończone pieniądze
	}
end

---------------------------------------------------------------- miasta
-- cargoNeeds: {{{CargoTypeId, number}}} - lista (typ zabudowy) list par {ładunek, wartość}
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

-- Aktualne pojemności dzielnic {mieszkalne, usługowe, przemysłowe} wszystkich miast, jednym wywołaniem.
-- To one rosną razem z miastem (suma = rozmiar z okna miasta); TOWN.initialLandUseCapacities
-- to tylko wartości startowe.
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
			}
		end
	end
	return out
end

---------------------------------------------------------------- przemysł
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

local function stocks(stockList)
	local out = {}
	if not stockList then return out end
	for _, s in ipairs(util.list(stockList.stocks)) do
		out[#out + 1] = {
			cargo = C.cargoName(s.cargoType),
			capacity = s.capacity,
		}
	end
	return out
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
				stocks = stocks(stock),
			}
		end
	end
	return out
end

---------------------------------------------------------------- stacje
-- Stacje z własną konstrukcją: dworce kolejowe, dworce autobusowe/ciężarówkowe, porty,
-- lotniska. Przystanki przy ulicy nie mają konstrukcji albo są na liście STOP_PATHS - pomijamy je.
-- STATION nie wiadomo, czy da się listować (LINE i BASE_EDGE się nie dało), więc bierzemy
-- mapę stacja -> konstrukcja. Jedna konstrukcja (np. lotnisko) bywa kilkoma stacjami,
-- dlatego wiersz = konstrukcja.

-- Rodzaj najpierw ze ścieżki konstrukcji (base/content/stations/<air|water|rail|street>/...).
-- getCarriers(group, -1, -1) zwraca sumę dla całej GRUPY stacji, a dworzec autobusowy
-- zgrupowany z kolejowym dostałby wtedy RAIL - dlatego nośniki to tylko zapas (mody).
local STATION_PATHS = { { "/air/", "AIR" }, { "/water/", "WATER" }, { "/rail/", "RAIL" }, { "/street/", "ROAD" } }
local STATION_KINDS = { "AIR", "WATER", "RAIL", "ROAD", "TRAM" } -- kolejność = priorytet
-- Przystanki, które są konstrukcjami: małe przystanki i przystanek podziemny ("Underground Stop").
local STOP_PATHS = { "small_stops", "underground_station" }

local function isStop(file)
	for _, p in ipairs(STOP_PATHS) do
		if file and string.find(file, p, 1, true) then return true end
	end
	return false
end

-- Zakład, do którego należy konstrukcja stacji (stacja wbudowana w przemysł) albo nil.
-- Stacja może być podkonstrukcją, więc sprawdzamy też konstrukcję nadrzędną.
local function stationIndustry(constrId, constr)
	local first = constr and util.list(util.field(constr, "industries"))[1]
	if first then return first end
	local ok, parent = pcall(function()
		return api.engine.system.streetConnectorSystem.getConstructionEntityForSubconstruction(constrId)
	end)
	if ok and parent and parent > 0 and parent ~= constrId then
		local pc = util.comp(parent, ct.CONSTRUCTION)
		return pc and util.list(util.field(pc, "industries"))[1] or nil
	end
	return nil
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

-- Zwraca listę stacji i mapę encja stacji -> rodzaj (do nośnika linii bez pojazdów).
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
			if kind or industry then -- stacja przemysłu zostaje, nawet bez rozpoznanego rodzaju
				for _, st in ipairs(stations) do kindOf[st] = kind end
				out[#out + 1] = {
					id = constrId,
					kind = kind or "OTHER",
					industry = industry, -- id zakładu (industries[].id), gdy stacja do niego należy
					carriers = carriers, -- nośniki całej grupy stacji
					name = (groups[1] and util.name(groups[1])) or util.name(constrId),
					file = file,
					pos = util.center(constrId) or util.center(stations[1]),
					owner = util.owner(constrId) or util.owner(stations[1]),
					passengers = passengers,
					cargo = cargo,
					groups = groups,
					stations = stations,
				}
			end
		end
	end
	return out, kindOf
end

---------------------------------------------------------------- linie i pojazdy
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

-- Statystyki linii jak w oknie linii i statystykach gry (gui/line_vehicle_mgmt/line_react_util.tl,
-- gui/statistics/statistic_lines.tl). prevYear = { year, from, to } albo nil.
-- Każde pole osobno w pcall: błąd jednego API nie kasuje reszty.
function C.lineStats(id, now, prevYear)
	local function try(f)
		local ok, v = pcall(f)
		if ok and type(v) == "number" then return v end
		return nil
	end
	local finance = api.engine.util.finance
	local yearLen = try(function() return api.util.getDefaultYearDuration() end)
	local s = {
		-- przychody minus utrzymanie z ostatnich 12 miesięcy ("Zysk" w oknie linii)
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

	-- zapełnienie pojazdów w chwili pomiaru, per ładunek i łącznie
	local ok, usages = pcall(function() return api.engine.util.line.getLineCapacityUsages(id, false) end)
	if ok and usages then
		local used, capacity, cargo = 0, 0, {}
		for cargoId, u in util.pairs(usages) do
			local uu, uc = util.field(u, "used"), util.field(u, "capacity")
			if type(uu) == "number" and type(uc) == "number" and uc > 0 then
				used, capacity = used + uu, capacity + uc
				cargo[#cargo + 1] = { cargo = C.cargoName(cargoId), used = util.round(uu, 0), capacity = util.round(uc, 0) }
			end
		end
		table.sort(cargo, function(a, b) return a.capacity > b.capacity end)
		s.load = capacity > 0 and util.round(used / capacity, 3) or nil
		s.cargo = cargo
	end
	return s
end

-- stationKinds: encja stacji -> rodzaj (z C.stations); nośnik linii bez pojazdów.
function C.lines(vehicles, stationKinds)
	-- liczba pojazdów i nośnik linii z już zebranej listy pojazdów (bez drugiego przejścia)
	local perLine, carrier = {}, {}
	for _, v in ipairs(vehicles or {}) do
		if v.line then
			perLine[v.line] = (perLine[v.line] or 0) + 1
			carrier[v.line] = carrier[v.line] or v.carrier
		end
	end

	-- LINE nie da się listować przez getEntitiesWithComponent ("Cannot loop over this
	-- component type"). Gra używa lineSystem.getLinesForPlayer; dokładamy linie znalezione
	-- w pojazdach (np. innych firm).
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

	-- Pozycja przystanku: stop.station to indeks (od 0) w STATION_GROUP.stations.
	-- Encja stacji zwykle nie ma BOUNDING_VOLUME, więc sięgamy do jej konstrukcji;
	-- w ostateczności środek grupy stacji. Cache na jeden eksport (stacje się powtarzają).
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
					entity = stationOf(s.stationGroup, s.station), -- encja stacji, por. stations[].stations
					pos = stationPos(s.stationGroup, s.station),
					name = util.name(s.stationGroup),
				}
			end
			local color = util.comp(id, ct.COLOR)
			out[#out + 1] = {
				id = id,
				name = util.name(id),
				color = color and util.vec3(color.color),
				owner = util.owner(id),
				carrier = lineCarrier, -- z pojazdów, a bez pojazdów z rodzaju stacji (nil = przystanki przy ulicy)
				stops = stops,
				vehicleCount = perLine[id] or 0,
			}
		end
	end
	return out
end

---------------------------------------------------------------- sieć (statyczna)
-- Format zwarty: jedna krawędź = jedna tablica, kolejność pól w EDGE_FIELDS.
C.EDGE_FIELDS = {
	"id", "node0", "node1",
	"x0", "y0", "z0", "x1", "y1", "z1",
	"tx0", "ty0", "tz0", "tx1", "ty1", "tz1",
	"road",   -- "STREET" / "TRACK"
	"type",   -- "NORMAL" / "BRIDGE" / "TUNNEL"
	"owner",  -- 0 = brak właściciela (miasto/świat)
}

-- Lista ID wszystkich krawędzi (drogi + tory).
-- BASE_EDGE nie da się listować przez getEntitiesWithComponent, więc zbieramy krawędzie
-- z mapy węzeł -> segmenty (streetSystem.getNode2SegmentMap). Jedno wywołanie C.
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

-- Zwraca wiersz albo nil, gdy encja zniknęła lub nie ma pozycji (krawędź pomijamy).
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
