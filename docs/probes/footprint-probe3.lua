-- Console probe 3: LOT_LIST (ground lots) on constructions and their subconstructions, plus the biggest models.
-- Paste into the in-game console; lines start with [probe3] in stdout.txt.
local C = api.type.ComponentType
local LOT = C.LOT_LIST or 17
local function P(s) print("[probe3] " .. s) end
local function g(e, c) local ok, v = pcall(api.engine.getComponent, e, c); return ok and v or nil end
local function n(l) if l == nil then return 0 end local ok, k = pcall(function() return #l end); return ok and k or 0 end
local function xy(v) if not v then return "nil" end return string.format("(%.1f %.1f)", v.x or v[1] or 0, v.y or v[2] or 0) end

local function lots(tag, e)
	local l = g(e, LOT)
	if not l then P("  " .. tag .. " " .. tostring(e) .. ": no LOT_LIST"); return end
	local list = l.lots
	P(string.format("  %s %s: %d lots", tag, tostring(e), n(list)))
	for i = 1, math.min(6, n(list)) do
		local lot = list[i]
		local vs, tr = lot.vertices, lot.triangles
		local first = {}
		for j = 1, math.min(6, n(vs)) do first[#first + 1] = xy(vs[j]) end
		P(string.format("    lot %d tex %s solid %s vertices %d triangles %d: %s", i, tostring(lot.texKey), tostring(lot.solid),
			n(vs), n(tr), table.concat(first, " ")))
	end
end

local function big(e, acc)
	local mil = g(e, C.MODEL_INSTANCE_LIST)
	local fat = mil and mil.fatInstances
	for i = 1, n(fat) do
		local ok, m = pcall(function() return api.res.modelRep.get(fat[i].modelId) end)
		local bi = ok and m and m.boundingInfo
		if bi then
			local w, h = bi.bbMax.x - bi.bbMin.x, bi.bbMax.y - bi.bbMin.y
			local okN, name = pcall(function() return api.res.modelRep.getName(fat[i].modelId) end)
			acc[#acc + 1] = { w * h, string.format("%.0fx%.0f %s", w, h, okN and tostring(name) or "?") }
		end
	end
end

local function constr(tag, c)
	local k = g(c, C.CONSTRUCTION)
	if not k then return end
	P(string.format("== %s %s %s at (%.1f %.1f)", tag, tostring(c), tostring(k.fileName), k.transf[13], k.transf[14]))
	lots("constr", c)
	local acc = {}
	big(c, acc)
	for i = 1, n(k.subconstructions) do
		lots("sub", k.subconstructions[i])
		big(k.subconstructions[i], acc)
	end
	table.sort(acc, function(a, b) return a[1] > b[1] end)
	local small = 0
	for _, a in ipairs(acc) do if a[1] < 20 then small = small + 1 end end
	P(string.format("  models %d (under 20 m2: %d), biggest:", #acc, small))
	for i = 1, math.min(10, #acc) do P("    " .. acc[i][2]) end
end

local ws = api.engine.getEntitiesWithComponent(C.WAREHOUSE, {})
for i = 1, math.min(3, n(ws)) do constr("warehouse", g(ws[i], C.WAREHOUSE).construction) end
local ok, map = pcall(function() return api.engine.system.streetConnectorSystem.getStation2ConstructionMap() end)
local seen = {}
if ok and map then
	for _, c in pairs(map) do
		local k = g(c, C.CONSTRUCTION)
		local kind = k and tostring(k.fileName):match("/stations/(%a+)/")
		if kind and c > 0 and not seen[kind] then seen[kind] = true; constr("station " .. kind, c) end
	end
end
local d = api.engine.getEntitiesWithComponent(C.VEHICLE_DEPOT, {})[1]
if d then constr("depot", api.engine.system.streetConnectorSystem.getConstructionEntityForDepot(d)) end
P("done")
