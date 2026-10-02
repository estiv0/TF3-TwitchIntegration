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

function C.towns()
	local out = {}
	for _, id in ipairs(util.entities(ct.TOWN)) do
		local t = util.comp(id, ct.TOWN)
		if t then
			out[#out + 1] = {
				id = id,
				name = util.name(id),
				pos = util.center(id),
				noise = util.round(t.noise),
				pollution = util.round(t.pollution),
				growing = t.developmentActive,
				capacities = util.list(t.initialLandUseCapacities),
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

function C.lines(vehicles)
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

	local out = {}
	for _, id in ipairs(ids) do
		local l = util.comp(id, ct.LINE)
		if l then
			local stops = {}
			for i, s in ipairs(util.list(l.stops)) do
				stops[i] = {
					group = s.stationGroup,
					station = s.station,
					terminal = s.terminal,
				}
			end
			local color = util.comp(id, ct.COLOR)
			out[#out + 1] = {
				id = id,
				name = util.name(id),
				color = color and util.vec3(color.color),
				owner = util.owner(id),
				carrier = carrier[id],
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
