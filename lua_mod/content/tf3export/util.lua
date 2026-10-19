-- Safe wrappers around api.engine.
-- Values from the API are often userdata (vectors, enums), so everything
-- that goes into the JSON is copied into plain Lua tables.

local util = {}

-- The game scripts use api.type.ComponentType; api.engine.ComponentType as a fallback.
local ct = (api.type and api.type.ComponentType) or api.engine.ComponentType
util.ct = ct

local function field(obj, key)
	local ok, value = pcall(function() return obj[key] end)
	if ok then return value end
	return nil
end
util.field = field

-- Writes a line to the game log (debugPrint in base/init.lua also uses log.message).
function util.print(line)
	if log and log.message then log.message(line) else print(line) end
end

function util.log(msg)
	util.print("[tf3export] " .. msg)
end

-- Wall time in seconds; os may be unavailable in TF3.
function util.clock()
	if os and os.clock then return os.clock() end
	return 0
end

function util.comp(entity, componentType)
	if entity == nil or componentType == nil then return nil end
	local ok, c = pcall(api.engine.getComponent, entity, componentType)
	if ok then return c end
	return nil
end

function util.entities(componentType, filters)
	local ok, list = pcall(api.engine.getEntitiesWithComponent, componentType, filters or {})
	if not ok or list == nil then
		util.log("getEntitiesWithComponent failed: " .. tostring(list))
		return {}
	end
	return util.list(list)
end

function util.round(x, decimals)
	if type(x) ~= "number" then return nil end
	local m = 10 ^ (decimals or 2)
	return math.floor(x * m + 0.5) / m
end

-- Turns a list (a table or a native vector) into a plain Lua array.
-- The TF3 documentation types lists as plain {T} arrays (native ones have the _native suffix).
function util.list(v)
	if v == nil then return {} end
	if type(v) == "table" then return v end
	local ok, n = pcall(function() return #v end)
	if not ok or type(n) ~= "number" then return {} end
	local out = {}
	for i = 1, n do
		out[i] = field(v, i)
	end
	return out
end

-- A line's vehicles (a copy as a plain array); {} on error.
function util.lineVehicles(line)
	local ok, vs = pcall(function() return api.engine.system.transportVehicleSystem.getLineVehicles(line) end)
	if not ok then return {} end
	local out = {}
	for i, v in ipairs(util.list(vs)) do out[i] = v end
	return out
end

-- Iterating over a map (a Lua table or a native map, if it supports pairs).
function util.pairs(v)
	if v == nil then return function() return nil end end
	local ok, f, st, init = pcall(pairs, v)
	if ok then return f, st, init end
	return function() return nil end
end

-- Vec3f -> { x, y, z } rounded to 2 decimal places.
function util.vec3(v)
	if v == nil then return nil end
	local x = field(v, "x") or field(v, 1)
	local y = field(v, "y") or field(v, 2)
	local z = field(v, "z") or field(v, 3) or 0
	if type(x) ~= "number" or type(y) ~= "number" then return nil end
	return { util.round(x), util.round(y), util.round(z) }
end

-- The userdata folder for all mod files. Since build 40420 the game only allows app.*Userdata on
-- towns_industries, heightmaps and mod_presets ("The directory you trying to access is not available
-- or invalid"), so we write to towns_industries (it only shows up in the map editor import),
-- and a name prefix tells our files apart. Tf3Bridge: SteamLocator.UserdataDir / FilePrefix.
util.USERDATA_DIR = "towns_industries"
util.FILE_PREFIX = "tf3export_"

-- Does the userdata file exist? app.loadUserdata on a missing file raises a game error
-- ("Failed to load userdata", with an error window) despite pcall, so we check the file list first.
-- getAllUserdata returns names; just in case we also accept the variant with ".lua". GUI thread only.
function util.userdataExists(dir, name)
	local ok, names = pcall(function() return app.getAllUserdata(dir) end)
	if not ok then return false end
	for _, n in ipairs(util.list(names)) do
		if n == name or n == name .. ".lua" then return true end
	end
	return false
end

function util.name(entity)
	if entity == nil then return nil end
	local ok, n = pcall(function() return api.engine.util.getEntityName(entity) end)
	if ok and type(n) == "string" and n ~= "" then return n end
	local c = util.comp(entity, ct.NAME)
	return c and c.name or nil
end

-- The center of an entity's bounding box (Box3f has min/max fields - confirmed in type.d.tl).
function util.center(entity)
	local bv = util.comp(entity, ct.BOUNDING_VOLUME)
	if not bv then return nil end
	local bbox = field(bv, "bbox")
	if not bbox then return nil end
	local a = util.vec3(field(bbox, "min"))
	local b = util.vec3(field(bbox, "max"))
	if not a or not b then return nil end
	return {
		util.round((a[1] + b[1]) / 2),
		util.round((a[2] + b[2]) / 2),
		util.round((a[3] + b[3]) / 2),
	}
end

-- The owner company ID, or nil for town / world objects.
function util.owner(entity)
	local po = util.comp(entity, ct.PLAYER_OWNED)
	return po and po.player or nil
end

-- Returns a numeric or a text value (for ResName etc.).
function util.scalar(v)
	local t = type(v)
	if t == "number" or t == "string" or t == "boolean" then return v end
	if v == nil then return nil end
	local n = tonumber(v)
	if n then return n end
	return tostring(v)
end

-- Enum -> name (e.g. Carrier.RAIL -> "RAIL"). The game scripts compare enums with ==.
function util.enumName(enumType, value, names)
	if value == nil then return nil end
	local enums = api.type and api.type["enum"]
	local e = enums and field(enums, enumType)
	if e then
		for _, n in ipairs(names) do
			local ok, eq = pcall(function() return e[n] == value end)
			if ok and eq then return n end
		end
	end
	return util.scalar(value)
end

util.CARRIERS = { "ROAD", "RAIL", "TRAM", "AIR", "WATER", "OTHER" }
util.VEHICLE_STATES = { "IN_DEPOT", "EN_ROUTE", "AT_TERMINAL", "GOING_TO_DEPOT" }
util.EDGE_TYPES = { "NORMAL", "BRIDGE", "TUNNEL" }
util.ROAD_TYPES = { "STREET", "TRACK" }

---------------------------------------------------------------- game time
function util.gameTime()
	local gt = util.comp(api.engine.util.getWorld(), ct.GAME_TIME)
	return gt and gt.gameTime or nil
end

-- Day length in gameTime units. Depends on the calendar speed setting.
-- TO CHECK: whether GAME_SPEED sits on the world entity.
function util.dayLength()
	local gs = util.comp(api.engine.util.getWorld(), ct.GAME_SPEED)
	if gs and type(gs.millisPerDay) == "number" and gs.millisPerDay > 0 then
		return gs.millisPerDay
	end
	local ok, d = pcall(function() return api.util.getDefaultDayDuration() end)
	if ok and type(d) == "number" and d > 0 then return d end
	return 2000
end

function util.date(gameTime)
	if gameTime == nil then return nil end
	local ok, d = pcall(function() return api.engine.util.getCalendarDate(gameTime) end)
	if not ok or d == nil then return nil end
	return { year = field(d, "year"), month = field(d, "month"), day = field(d, "day") }
end

-- The gameTime of the start of the calendar year t falls in. The API has no inverse of getCalendarDate,
-- so we search binarily (~30 calls); like getCalendarDate it assumes
-- the current calendar speed. 0 when the year started before the game did (or the calendar
-- stands still), nil on an API error.
function util.yearStart(t)
	local d = util.date(t)
	if not d or type(d.year) ~= "number" then return nil end
	local function sameYear(x)
		local dx = util.date(x)
		return dx ~= nil and dx.year == d.year
	end
	local span = 400 * util.dayLength()
	while sameYear(t - span) do
		if t - span <= 0 then return 0 end
		span = span * 2
	end
	local lo, hi = math.max(t - span, 0), t -- lo: the previous year, hi: this year
	while hi - lo > 1 do
		local mid = math.floor((lo + hi) / 2)
		if sameYear(mid) then hi = mid else lo = mid end
	end
	return hi
end

return util
