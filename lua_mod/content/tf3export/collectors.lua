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

---------------------------------------------------------------- firma
-- Nazwy enumów JournalEntry (scripts/journal.d.tl) - skrypty gry porównują je przez ==.
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

-- Tabela z okna Finanse (game_mechanics/finance/finances_table.tl): kolumny = lata (header), wiersze =
-- { carrier, type, maintenance, construction, values } z kategorii transportu (per nośnik), inwestycji
-- i innych; do tego zysk (total), stan konta (balance), odsetki, kredyt.
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

-- Historia z logbooka świata (wykresy z okna Finanse/Miasta): nazwa -> [[rok, wartość], ...], rok jako
-- liczba z ułamkiem (1921.5 = połowa 1921). Najwyżej maxPoints punktów na serię (równo przerzedzone,
-- ostatni zawsze). ChartResult.series[i] to para list { czasy gry, wartości } (domyślnie po 50 punktów
-- od początku gry do teraz, wartości uśrednione w przedziałach); czas zamieniamy na datę przez util.date.
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

-- Zaspokojenie potrzeb miasta: [{ cargo, supply, demand }] jak w statystykach miast w grze
-- (townBuildingSystem.getCargoSupplyAndLimit, wszystkie strefy razem; game_mechanics/towns/town_util.tl).
-- demand -1 = bez limitu. nil, gdy API zawiedzie.
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

-- Magazyny zakładu: ładunek, pojemność, stan teraz (simEntityAtStockSystem, StockId od 0),
-- input (magazyn wejściowy) i consumedYear (zużycie na rok, stockDatas[i].consumeFrequency -
-- to samo, co okno zakładu pokazuje jako faktyczne zużycie).
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

-- Faktyczna produkcja na rok: ładunek -> stockList.cargoData[ładunek].produceFrequency.
local function producedYear(stockList)
	local out = {}
	if not stockList then return json.EMPTY_OBJECT end
	for cargo, d in util.pairs(stockList.cargoData) do
		local f = util.field(d, "produceFrequency")
		if type(f) == "number" and f > 0 then out[C.cargoName(cargo)] = util.round(f, 0) end
	end
	return next(out) and out or json.EMPTY_OBJECT
end

-- Liczniki z logbooka zakładu za ostatni rok (wykres "Przepływ ładunków" w oknie zakładu):
-- wyprodukowane, zużyte, zmarnowane (niezabrane). Brak wpisu = nil.
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

-- Dopisuje do pojazdu (wpis z C.vehicles) pos (m, do 1 m) i speed (m/s) z api.engine.util.vehicle.
-- Bez nich strona zgaduje położenie z numeru przystanku, a ten na długim odcinku (pociąg, statek)
-- nie zmienia się przez minuty. Pojazdy w zajezdni pomijamy. false = API niedostępne.
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

-- Model z api.res: { name, lifespan (w jednostkach gameTime) }, z cache (modeli jest kilkadziesiąt).
-- Nazwa i żywotność jak w oknie pojazdu (gui/line_vehicle_mgmt/vehicle_util.tl, vehicle_store_util.tl).
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
		modelCache[key] = info
	end
	return modelCache[key]
end

-- Nazwa zespołu trakcyjnego (multiple unit) z pliku w transportVehicleConfig.muFileNames, z cache.
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

-- Data gry jako ułamek roku (2031.5 = około 1 lipca 2031); nil bez kalendarza.
local function fracYear(t)
	local d = util.date(t)
	if not d or type(d.year) ~= "number" then return nil end
	return d.year + ((d.month or 1) - 1) / 12 + ((d.day or 1) - 1) / 365
end
C.fracYear = fracYear

-- Dopisuje do pojazdu (wpis z C.vehicles) szczegóły ze składu (transportVehicleConfig) i config:
--   model    = id modelu czoła składu (nazwa w models)
--   parts    = liczba części składu (lokomotywa + wagony), tylko gdy więcej niż 1
--   age      = wiek najstarszej części w latach kalendarza gry (jak "Wiek" w oknie pojazdu)
--   wear     = wiek / żywotność tej części (1 = koniec żywotności, jak procent w oknie linii)
--   maint    = średni stan techniczny części, 0..1 (vehicle_util.getAvgMaintenanceState)
--   cap      = pojemność według ładunku { [nazwa ładunku] = liczba }
-- models: tabela "id modelu" -> nazwa (klucze tekstowe, żeby JSON dał obiekt), uzupełniana o modele tego pojazdu.
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
			models[key] = (type(mu) == "string" and mu ~= "" and muName(mu)) or modelInfo(head).name
		end
		-- najstarsza część (przy równym czasie zakupu ta z krótszą żywotnością), jak getMinPurchaseTimeAndLifespan
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
	-- pojemności: tablica Lua od 1, CargoTypeId = indeks - 1 (jak getLineCapacityUsages w C.lineStats)
	local cap = {}
	for index, n in util.pairs(util.field(util.field(tv, "config"), "capacities")) do
		if type(index) == "number" and type(n) == "number" and n > 0 then cap[C.cargoName(index - 1)] = n end
	end
	if next(cap) then v.cap = cap end
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
		-- to tablica Lua indeksowana od 1: CargoTypeId = indeks - 1 (jak w gui/main/cargo_util.tl)
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

-- Zysk linii ze wszystkich zakończonych lat: od początku gry do yearStart (początek bieżącego roku).
-- Zmienia się tylko na przełomie roku, więc skrypt liczy go raz na rok gry (nil = brak danych).
function C.lineProfitTotal(id, yearStart)
	local ok, v = pcall(function() return api.engine.util.finance.calculateBalance({ id }, 0, yearStart, true) end)
	if ok and type(v) == "number" then return v end
	return nil
end

-- stationKinds: encja stacji -> rodzaj (z C.stations); nośnik linii bez pojazdów.
-- vehicles = nil (pojazdy wyłączone): vehicleCount = nil, nośnik tylko z rodzaju stacji.
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
				vehicleCount = vehicles and (perLine[id] or 0) or nil,
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
