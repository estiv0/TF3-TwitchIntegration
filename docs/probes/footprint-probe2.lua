-- Console probe 2: construction footprint from model instances (MODEL_INSTANCE_LIST + modelRep boundingInfo).
-- Paste into the in-game console; lines start with [probe2] in stdout.txt.
local C = api.type.ComponentType
local function P(s) print("[probe2] " .. s) end
local function g(e, c) if c == nil then return nil end local ok, v = pcall(api.engine.getComponent, e, c); return ok and v or nil end
local function n(l) if l == nil then return 0 end local ok, k = pcall(function() return #l end); return ok and k or 0 end
P("LOT_LIST component type: " .. tostring(C.LOT_LIST))

local bbCache = {}
local function modelBB(id)
	if bbCache[id] ~= nil then return bbCache[id] end
	local ok, m = pcall(function() return api.res.modelRep.get(id) end)
	local bi = ok and m and m.boundingInfo
	bbCache[id] = bi and { bi.bbMin.x, bi.bbMin.y, bi.bbMin.z, bi.bbMax.x, bi.bbMax.y, bi.bbMax.z } or false
	return bbCache[id]
end
local function modelName(id)
	local ok, nm = pcall(function() return api.res.modelRep.getName(id) end)
	return ok and tostring(nm) or "?"
end

-- corners of all models of an entity, in the frame of the construction (ct = construction transf)
local function collect(entity, ct, acc, verbose)
	local mil = g(entity, C.MODEL_INSTANCE_LIST)
	if not mil then return end
	local fat, thin = mil.fatInstances, mil.thinInstances
	acc.fat = acc.fat + n(fat); acc.thin = acc.thin + n(thin)
	local cx, cy = ct[13], ct[14]
	local ax, ay, bx, by = ct[1], ct[2], ct[5], ct[6]
	for i = 1, n(fat) do
		local inst = fat[i]
		local bb = modelBB(inst.modelId)
		local t = inst.transf
		if bb then
			local lmin, lmax = { 1e9, 1e9 }, { -1e9, -1e9 }
			for _, x in ipairs({ bb[1], bb[4] }) do for _, y in ipairs({ bb[2], bb[5] }) do
				local wx = t[1] * x + t[5] * y + t[13]
				local wy = t[2] * x + t[6] * y + t[14]
				local dx, dy = wx - cx, wy - cy
				local lx, ly = dx * ax + dy * ay, dx * bx + dy * by -- into construction-local frame
				lmin[1] = math.min(lmin[1], lx); lmin[2] = math.min(lmin[2], ly)
				lmax[1] = math.max(lmax[1], lx); lmax[2] = math.max(lmax[2], ly)
			end end
			acc.min[1] = math.min(acc.min[1], lmin[1]); acc.min[2] = math.min(acc.min[2], lmin[2])
			acc.max[1] = math.max(acc.max[1], lmax[1]); acc.max[2] = math.max(acc.max[2], lmax[2])
			if verbose and acc.shown < 6 then
				acc.shown = acc.shown + 1
				P(string.format("    model %s bb %.1fx%.1fx%.1f local x[%.1f..%.1f] y[%.1f..%.1f]", modelName(inst.modelId),
					bb[4] - bb[1], bb[5] - bb[2], bb[6] - bb[3], lmin[1], lmax[1], lmin[2], lmax[2]))
			end
		else acc.nobb = acc.nobb + 1 end
	end
end

local function constr(tag, c)
	local k = g(c, C.CONSTRUCTION)
	if not k then P("no construction " .. tostring(c)); return end
	local acc = { fat = 0, thin = 0, nobb = 0, shown = 0, min = { 1e9, 1e9 }, max = { -1e9, -1e9 } }
	collect(c, k.transf, acc, true)
	for i = 1, n(k.subconstructions) do collect(k.subconstructions[i], k.transf, acc, true) end
	local lot = g(c, C.LOT_LIST)
	P(string.format("== %s %s %s: fat %d thin %d nobb %d -> local rect x[%.1f..%.1f] y[%.1f..%.1f] = %.0fx%.0f m; lots %s",
		tag, tostring(c), tostring(k.fileName), acc.fat, acc.thin, acc.nobb,
		acc.min[1], acc.max[1], acc.min[2], acc.max[2], acc.max[1] - acc.min[1], acc.max[2] - acc.min[2],
		lot and tostring(n(lot.lots)) or "nil"))
end

local ok, map = pcall(function() return api.engine.system.streetConnectorSystem.getStation2ConstructionMap() end)
local seen = {}
if ok and map then
	for _, c in pairs(map) do
		local k = g(c, C.CONSTRUCTION)
		local f = k and tostring(k.fileName) or ""
		local kind = f:match("/stations/(%a+)/")
		if kind and c > 0 and not seen[kind] then seen[kind] = true; constr("station " .. kind, c) end
	end
end
local d = api.engine.getEntitiesWithComponent(C.VEHICLE_DEPOT, {})[1]
if d then constr("depot", api.engine.system.streetConnectorSystem.getConstructionEntityForDepot(d)) end
local w = api.engine.getEntitiesWithComponent(C.WAREHOUSE, {})[1]
if w then constr("warehouse", g(w, C.WAREHOUSE).construction) end
P("done")
