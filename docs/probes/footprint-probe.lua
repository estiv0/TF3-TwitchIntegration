-- Console probe: construction footprints (stations, depots, warehouses) + street widths.
-- Paste the whole thing into the in-game console; lines start with [probe] in stdout.txt.
local C = api.type.ComponentType
local function P(s) print("[probe] " .. s) end
local function g(e, c) local ok, v = pcall(api.engine.getComponent, e, c); return ok and v or nil end
local function n(l) if l == nil then return "nil" end local ok, k = pcall(function() return #l end); return ok and k or "?" end
local function v3(v) if not v then return "nil" end return string.format("(%.1f %.1f %.1f)", v.x or v[1] or 0, v.y or v[2] or 0, v.z or v[3] or 0) end

local function constr(tag, c)
	local k = g(c, C.CONSTRUCTION)
	local bv = g(c, C.BOUNDING_VOLUME)
	P(string.format("== %s constr %s file %s", tag, tostring(c), tostring(k and k.fileName)))
	P("  bbox " .. (bv and bv.bbox and (v3(bv.bbox.min) .. " - " .. v3(bv.bbox.max)) or "none"))
	if not k then return end
	local t = k.transf
	if t then
		local ok, s = pcall(function() local o = {} for i = 1, 16 do o[i] = string.format("%.2f", t[i]) end return table.concat(o, " ") end)
		P("  transf " .. (ok and s or tostring(t)))
	end
	P(string.format("  frozenEdges %s frozenNodes %s depots %s stations %s subconstr %s",
		n(k.frozenEdges), n(k.frozenNodes), n(k.depots), n(k.stations), n(k.subconstructions)))
	for i = 1, math.min(3, n(k.frozenEdges) == "?" and 0 or n(k.frozenEdges) or 0) do
		local be = g(k.frozenEdges[i], C.BASE_EDGE)
		if be then P("  edge " .. i .. " " .. v3(be.position0) .. " -> " .. v3(be.position1)) end
	end
	local sub = k.subconstructions and k.subconstructions[1]
	if sub then
		local sb = g(sub, C.BOUNDING_VOLUME)
		P("  sub1 " .. tostring(sub) .. " bbox " .. (sb and sb.bbox and (v3(sb.bbox.min) .. " - " .. v3(sb.bbox.max)) or "none"))
	end
end

-- stations: first construction of each kind (by file path)
local seen, ok, map = {}, pcall(function() return api.engine.system.streetConnectorSystem.getStation2ConstructionMap() end)
local cnt = 0
if ok and map then
	for _, c in pairs(map) do
		local k = g(c, C.CONSTRUCTION)
		local f = k and tostring(k.fileName) or ""
		local kind = f:match("/stations/(%a+)/") or f:match("(%a+)_station") or f
		if c > 0 and not seen[kind] and cnt < 8 then seen[kind] = true; cnt = cnt + 1; constr("station " .. kind, c) end
	end
else P("getStation2ConstructionMap failed " .. tostring(map)) end

-- depots
local depots = api.engine.getEntitiesWithComponent(C.VEHICLE_DEPOT, {})
P("depots " .. n(depots))
for i = 1, math.min(2, n(depots)) do
	local d = depots[i]
	local okc, c = pcall(function() return api.engine.system.streetConnectorSystem.getConstructionEntityForDepot(d) end)
	P("depot " .. tostring(d) .. " constr " .. tostring(okc and c or c))
	if okc and c and c > 0 then constr("depot", c) end
end

-- warehouses
local ws = api.engine.getEntitiesWithComponent(C.WAREHOUSE, {})
P("warehouses " .. n(ws))
for i = 1, math.min(2, n(ws)) do
	local w = g(ws[i], C.WAREHOUSE)
	if w and w.construction then constr("warehouse", w.construction) end
end

-- street widths: laneConfigs of a few edges
local okm, nodes = pcall(function() return api.engine.system.streetSystem.getNode2SegmentMap() end)
local done, kinds = 0, {}
if okm and nodes then
	for _, edges in pairs(nodes) do
		for _, e in ipairs(edges) do
			local be = g(e, C.BASE_EDGE)
			if be and done < 12 then
				local ws_, sum = {}, 0
				for _, l in ipairs(be.laneConfigs or {}) do ws_[#ws_ + 1] = string.format("%.1f", l.width or -1); sum = sum + (l.width or 0) end
				local key = table.concat(ws_, ",") .. "|" .. tostring(be.typeIndex)
				if not kinds[key] then
					kinds[key] = true; done = done + 1
					P(string.format("edge %s typeIndex %s street %s lanes [%s] sum %.1f", tostring(e), tostring(be.typeIndex),
						tostring(g(e, C.BASE_EDGE_STREET) ~= nil), table.concat(ws_, ","), sum))
				end
			end
		end
		if done >= 12 then break end
	end
end
P("done")
