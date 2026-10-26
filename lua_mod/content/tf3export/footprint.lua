-- Footprints of constructions (stations, depots, warehouses): the ground lots they paint on the terrain.
-- Checked in the game (docs/probes/footprint-probe*.lua, 2026-10-26): frozenEdges only hold the edge that connects
-- the construction to the road and BOUNDING_VOLUME is axis-aligned (too big for a rotated station). The
-- subconstructions (not the construction itself) have LOT_LIST: polygons in world coordinates, one or more per module
-- (a warehouse = entrance + one per store), which is the shape the game highlights. Non-solid lots are borders
-- (street_border, pedestrian_connection_border) and are skipped.
-- Fallback without lots: the convex hull of the corners of the models (MODEL_INSTANCE_LIST + modelRep boundingInfo),
-- without lamps and lights, whose bounding boxes include the light cone (22x25 m for a lamp post).
--
-- Computed with the network export (tf3export.script.lua), one construction at a time within its frame budget.

local util = ug_require "util.lua"

local ct = util.ct
local field = util.field

local F = {}

local modelBB = {} -- model id -> { minX, minY, maxX, maxY } or false (no boundingInfo, or a lamp)

-- Lamps and lights: their bounding box includes the light cone, much bigger than the post.
local SKIP_MODELS = { "/lamps/", "/lights/" }

local function boundsOf(modelId)
	local bb = modelBB[modelId]
	if bb ~= nil then return bb end
	bb = false
	local okN, name = pcall(function() return api.res.modelRep.getName(modelId) end)
	local skip = false
	for _, s in ipairs(SKIP_MODELS) do
		if okN and type(name) == "string" and string.find(name, s, 1, true) then skip = true end
	end
	if not skip then
		local ok, m = pcall(function() return api.res.modelRep.get(modelId) end)
		local bi = ok and m and field(m, "boundingInfo")
		local a, b = bi and field(bi, "bbMin"), bi and field(bi, "bbMax")
		if a and b and type(a.x) == "number" and type(b.x) == "number" then bb = { a.x, a.y, b.x, b.y } end
	end
	modelBB[modelId] = bb
	return bb
end

local function round(v)
	return math.floor(v * 2 + 0.5) / 2
end

-- Solid ground lots of the entity appended to polys as flat lists { x1, y1, x2, y2, ... } (0.5 m).
local function addLots(entity, polys)
	local ll = ct.LOT_LIST and util.comp(entity, ct.LOT_LIST)
	for _, lot in ipairs(util.list(ll and field(ll, "lots"))) do
		local vs = util.list(field(lot, "vertices"))
		if field(lot, "solid") and #vs >= 3 then
			local flat = {}
			for _, v in ipairs(vs) do
				local x, y = field(v, "x"), field(v, "y")
				if type(x) == "number" and type(y) == "number" then
					flat[#flat + 1] = round(x)
					flat[#flat + 1] = round(y)
				end
			end
			if #flat >= 6 then polys[#polys + 1] = flat end
		end
	end
end

-- The 4 ground corners of every model of the entity (world x, y) appended to pts.
local function addCorners(entity, pts)
	local mil = util.comp(entity, ct.MODEL_INSTANCE_LIST)
	local fat = mil and util.list(field(mil, "fatInstances")) or {}
	for _, inst in ipairs(fat) do
		local bb = boundsOf(inst.modelId)
		local t = inst.transf
		if bb and t then
			local ax, ay, bx, by, cx, cy = t[1], t[2], t[5], t[6], t[13], t[14]
			for _, x in ipairs({ bb[1], bb[3] }) do
				for _, y in ipairs({ bb[2], bb[4] }) do
					pts[#pts + 1] = { ax * x + bx * y + cx, ay * x + by * y + cy }
				end
			end
		end
	end
end

local function cross(o, a, b)
	return (a[1] - o[1]) * (b[2] - o[2]) - (a[2] - o[2]) * (b[1] - o[1])
end

-- Convex hull (monotone chain), counter-clockwise, without the repeated first point.
local function hull(pts)
	table.sort(pts, function(a, b) return a[1] < b[1] or (a[1] == b[1] and a[2] < b[2]) end)
	if #pts < 3 then return pts end
	local out = {}
	for _, p in ipairs(pts) do
		while #out >= 2 and cross(out[#out - 1], out[#out], p) <= 0 do out[#out] = nil end
		out[#out + 1] = p
	end
	local lower = #out + 1
	for i = #pts - 1, 1, -1 do
		local p = pts[i]
		while #out >= lower and cross(out[#out - 1], out[#out], p) <= 0 do out[#out] = nil end
		out[#out + 1] = p
	end
	out[#out] = nil
	return out
end

-- Computes the footprint of a construction (with its subconstructions): a list of polygons, each a flat list
-- { x1, y1, x2, y2, ... } rounded to 0.5 m - the ground lots, or one hull of the models when there are no lots;
-- nil when it has neither.
function F.compute(constrId)
	local c = util.comp(constrId, ct.CONSTRUCTION)
	local entities = { constrId }
	for _, sub in ipairs(util.list(c and field(c, "subconstructions"))) do entities[#entities + 1] = sub end

	local polys = {}
	for _, e in ipairs(entities) do addLots(e, polys) end
	if #polys > 0 then return polys end

	local pts = {}
	for _, e in ipairs(entities) do addCorners(e, pts) end
	if #pts < 3 then return nil end
	local flat = {}
	for _, p in ipairs(hull(pts)) do
		flat[#flat + 1] = round(p[1])
		flat[#flat + 1] = round(p[2])
	end
	return { flat }
end

return F
