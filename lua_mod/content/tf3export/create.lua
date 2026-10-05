-- Nowe miasta i zakłady z nagród za punkty kanału (widz wskazuje miejsce na mapie strony).
--
-- Tak samo jak edytor mapy gry (gui/map_editor/map_editor.tl, makeTowns / makeIndustries):
--   miasto: api.engine.mapgen.createTowns -> toAdd -> api.cmd.makeTownCreateCmd
--   zakład: api.engine.mapgen.createIndustries -> toAdd -> api.engine.util.proposal.createIndustryProposal
--           -> api.cmd.makeWorldBuildProposalCmd
-- toRemove z mapgen pomijamy: nowy obiekt nigdy nie usuwa istniejących.
--
-- Zakład budujemy bez ignoreErrors: kolizja z torami, drogą albo stacją streamera kończy się błędem
-- (Tf3Bridge zwraca widzowi punkty), a nie zburzeniem cudzej infrastruktury.
--
-- Nazwa: "Nazwa | widz" (jak po kupnie miasta), więc widz od razu jest właścicielem.

local util = ug_require "util.lua"
local ct = util.ct

local C = {}

local MAX_NAME = 200       -- bajty, jak w setName
local FIND_RADIUS = 1500   -- m: tak daleko od wskazanego punktu szukamy nowego zakładu

-- Zakłady stawiane na wodzie (reszta tylko na lądzie).
local ON_WATER = { oil_platform = true, fishing_grounds = true, dredging_platform = true }

local function vec2(x, y) return api.type.Vec2f.new(x, y) end

local function waterLevel()
	local c = util.comp(api.engine.util.getWorld(), ct.TERRAIN)
	local w = c and util.field(c, "waterLevel")
	return type(w) == "number" and w or nil
end

-- true/false: punkt pod wodą; nil, gdy nie wiadomo (brak poziomu wody albo wysokości).
local function underWater(x, y)
	local w = waterLevel()
	if not w then return nil end
	local ok, h = pcall(function() return api.engine.terrain.getHeightAt(vec2(x, y)) end)
	if not ok or type(h) ~= "number" then return nil end
	return h < w
end

-- Wspólne sprawdzenie argumentów: x, y w granicach mapy, nazwa i właściciel bez "|".
local function place(c)
	local x, y = tonumber(c.x), tonumber(c.y)
	local name, owner = c.name, c.owner
	if not x or not y or type(name) ~= "string" or type(owner) ~= "string" then return nil, "bad arguments" end
	name = name:match("^%s*(.-)%s*$")
	if name == "" or owner == "" or name:find("|", 1, true) or owner:find("|", 1, true) then return nil, "bad arguments" end
	local full = name .. " | " .. owner
	if #full > MAX_NAME then return nil, "name too long" end
	local ok, valid = pcall(function() return api.engine.terrain.isValidCoordinate(vec2(x, y)) end)
	if ok and valid == false then return nil, "outside of the map" end
	return { x = x, y = y, name = name, full = full }
end

-- Encje z danym komponentem w zbiorze (id -> true).
local function entitySet(componentType)
	local set = {}
	for _, id in ipairs(util.entities(componentType)) do set[id] = true end
	return set
end

local function townNamed(full)
	for _, id in ipairs(util.entities(ct.TOWN)) do
		if util.name(id) == full then return id end
	end
	return nil
end

-- Potrzeby ładunkowe najbliższego miasta (kategorie, nie CargoTypeId), żeby nowe miasto
-- miało podobne zapotrzebowanie jak sąsiedzi. nil, gdy się nie da (gra dobierze sama albo wcale).
local function nearestCargoNeeds(x, y)
	local ok, map = pcall(function() return api.engine.terrain.makeMapFromGame(false, true, false, false) end)
	if not ok or not map then return nil end
	local best, bestD = nil, math.huge
	for _, t in ipairs(util.list(util.field(map, "towns"))) do
		local p = util.field(t, "pos")
		local tx, ty = p and util.field(p, "x"), p and util.field(p, "y")
		if type(tx) == "number" and type(ty) == "number" then
			local d = (tx - x) ^ 2 + (ty - y) ^ 2
			if d < bestD then best, bestD = t, d end
		end
	end
	return best and util.field(best, "landUse2CargoNeeds") or nil
end

local function seed(x, y) return string.format("tf3x_%d_%d", math.floor(x), math.floor(y)) end

-- Wynik api.cmd.sendCommand -> pending[id].callback (commands.lua sprawdza go razem z verify).
local function remember(pending, id)
	return function(_, success)
		local p = pending[id]
		if p then p.callback = success end
	end
end

---------------------------------------------------------------- miasto
-- { x, y, name, owner }
function C.town(c, pending)
	local p, err = place(c)
	if not p then return nil, err end
	if underWater(p.x, p.y) then return nil, "on water" end
	if townNamed(p.full) then return nil, "town with this name already exists" end

	local town = api.type.Map.Town.new()
	town.pos = vec2(p.x, p.y)
	town.name = p.full
	town.sizeFactors = { 1, 1, 1 }
	local needs = nearestCargoNeeds(p.x, p.y)
	if needs then pcall(function() town.landUse2CargoNeeds = needs end) end

	local result = api.engine.mapgen.createTowns(seed(p.x, p.y), { town })
	local toAdd = result and util.field(result, "toAdd")
	if #util.list(toAdd) == 0 then return nil, "no room for a town here" end
	api.cmd.sendCommand(api.cmd.makeTownCreateCmd(toAdd), remember(pending, c.id)) -- jak edytor: lista z mapgen bez zmian
	local info = {} -- info.entity: id nowego miasta (do linku na czacie i powiadomienia)
	return function()
		info.entity = townNamed(p.full)
		return info.entity ~= nil
	end, nil, info
end

---------------------------------------------------------------- zakład
-- Typ "coal_mine" z pliku "::/industries/coal_mine/coal_mine.con".
local function industryType(file)
	return type(file) == "string" and file:match("^::/industries/([%w_]+)/%1%.con$") or nil
end

local function placementTag(file)
	local ok, tag = pcall(function()
		local id = api.res.constructionRep.find(file)
		return id and id >= 0 and api.res.constructionRep.get(id).placementTag or nil
	end)
	return ok and type(tag) == "string" and tag or nil
end

-- Nowy zakład (spoza zbioru before) najbliżej punktu.
local function newIndustryNear(before, x, y)
	local best, bestD = nil, FIND_RADIUS ^ 2
	for _, id in ipairs(util.entities(ct.INDUSTRY)) do
		if not before[id] then
			local ind = util.comp(id, ct.INDUSTRY)
			local pos = (ind and util.center(ind.construction)) or util.center(id)
			local d = pos and ((pos[1] - x) ^ 2 + (pos[2] - y) ^ 2) or 0 -- bez pozycji: i tak nowy
			if d <= bestD then best, bestD = id, d end
		end
	end
	return best
end

-- { x, y, file, name, owner }; file: plik .con zakładu z eksportu (industries[].file).
function C.industry(c, pending)
	local p, err = place(c)
	if not p then return nil, err end
	local kind = industryType(c.file)
	if not kind then return nil, "unknown industry type" end
	local wet = underWater(p.x, p.y)
	if wet ~= nil and wet ~= (ON_WATER[kind] == true) then
		return nil, ON_WATER[kind] and "needs water" or "on water"
	end

	local industry = api.type.Map.Industry.new()
	industry.pos = vec2(p.x, p.y)
	industry.angle = math.random() * 2 * math.pi
	industry.onWater = ON_WATER[kind] == true
	industry.fileName = c.file
	industry.tag = placementTag(c.file) or kind

	local s = seed(p.x, p.y)
	local result = api.engine.mapgen.createIndustries(s, { industry })
	local toAdd = util.list(result and util.field(result, "toAdd"))
	if #toAdd == 0 then return nil, "no room for this industry here" end
	local context = api.type.Context.new()
	local proposal = api.engine.util.proposal.createIndustryProposal(s, toAdd[1], context)
	if not proposal then return nil, "no room for this industry here" end

	local before = entitySet(ct.INDUSTRY)
	api.cmd.sendCommand(api.cmd.makeWorldBuildProposalCmd(proposal, context, false, false, true), remember(pending, c.id))

	-- Najpierw czekamy na nowy zakład, potem nadajemy mu nazwę (gra nazywa go sama: "Miasto Kopalnia").
	local found, renamed = nil, false
	local info = {} -- info.entity: id nowego zakładu (do linku na czacie i powiadomienia)
	return function()
		found = found or newIndustryNear(before, p.x, p.y)
		if not found then return false end
		if util.name(found) == p.full then
			info.entity = found
			return true
		end
		if not renamed then
			renamed = true
			api.cmd.sendCommand(api.cmd.makeEntitySetNameCmd(found, p.full))
		end
		return false
	end, nil, info
end

return C
