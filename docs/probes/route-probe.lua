-- Console probe: the real route of a line's vehicles (MOVE_PATH.path.edges) - how far it reaches and what an EdgeId is.
-- Paste into the in-game console; lines start with [route] in stdout.txt. Looks for a line named "15", else the first one.
local C = api.type.ComponentType
local function P(s) print("[route] " .. s) end
local function g(e, c) local ok, v = pcall(api.engine.getComponent, e, c); return ok and v or nil end
local function n(l) if l == nil then return 0 end local ok, k = pcall(function() return #l end); return ok and k or 0 end
local function name(e) local ok, s = pcall(function() return api.engine.util.getEntityName(e) end); return ok and tostring(s) or "?" end

local player = api.engine.util.getPlayer()
local lines = api.engine.system.lineSystem.getLinesForPlayer(player)
-- a line named exactly "15" (also "Linia 15", "Line 15"), else the first line with road vehicles
local line, roadLine
for i = 1, n(lines) do
	local nm = name(lines[i])
	if nm:match("^15$") or nm:match("%s15$") then line = lines[i]; break end
	if not roadLine then
		local v1 = api.engine.system.transportVehicleSystem.getLineVehicles(lines[i])[1]
		if v1 and g(v1, C.LAND_VEHICLE) and not g(v1, C.TRAIN) then roadLine = lines[i] end
	end
end
line = line or roadLine or lines[1]
local l = g(line, C.LINE)
P(string.format("line %s '%s': %d stops", tostring(line), name(line), n(l and l.stops)))

-- EdgeId and direction from a path entry; the documented {{EdgeId, boolean}} shape is tried first, then named fields
local function parts(pe)
	local function f(o, k) local ok, v = pcall(function() return o[k] end); return ok and v or nil end
	local id = f(pe, 1) or f(pe, "edgeId") or f(pe, "id") or f(pe, "edge")
	local fwd = f(pe, 2)
	if fwd == nil then fwd = f(pe, "dir") end
	if fwd == nil then fwd = f(pe, "forward") end
	local ent = id and (f(id, "entity") or f(id, 1)) or f(pe, "entity")
	local idx = id and (f(id, "index") or f(id, 2)) or f(pe, "index")
	return ent, idx, fwd
end

local function edgeStr(pe)
	local ent, idx, fwd = parts(pe)
	return string.format("%s/%s%s%s", tostring(ent), tostring(idx), fwd and ">" or "<", g(ent, C.BASE_EDGE) and "" or "(no BASE_EDGE)")
end

local vs = api.engine.system.transportVehicleSystem.getLineVehicles(line)
P("vehicles " .. n(vs))
for i = 1, math.min(4, n(vs)) do
	local v = vs[i]
	local tv = g(v, C.TRANSPORT_VEHICLE)
	local mp = g(v, C.MOVE_PATH)
	if not mp then
		P(string.format("  vehicle %s: no MOVE_PATH; LAND_VEHICLE %s TRAIN %s SHIP %s AIRCRAFT %s",
			tostring(v), tostring(g(v, C.LAND_VEHICLE) ~= nil), tostring(g(v, C.TRAIN) ~= nil),
			tostring(g(v, C.SHIP) ~= nil), tostring(g(v, C.AIRCRAFT) ~= nil)))
		local cl = g(v, C.CARRIAGE_LIST)
		local first = cl and cl.carriages and cl.carriages[1]
		if first then P("    first part " .. tostring(first) .. " MOVE_PATH " .. tostring(g(first, C.MOVE_PATH) ~= nil)) end
	else
	local edges = mp.path and mp.path.edges
	local m = n(edges)
	local first, last = {}, {}
	for j = 1, math.min(6, m) do first[#first + 1] = edgeStr(edges[j]) end
	for j = math.max(7, m - 3), m do last[#last + 1] = edgeStr(edges[j]) end
	P(string.format("  vehicle %s stopIndex %s state %s: path %d edges, endOffset %s, at edge %s, servedFrom %s servedTo %s",
		tostring(v), tostring(tv and tv.stopIndex), tostring(mp.state), m, tostring(mp.path.endOffset),
		tostring(mp.dyn and mp.dyn.pathPos and mp.dyn.pathPos.edgeIndex), tostring(mp.servedFrom), tostring(mp.servedTo)))
	P("    first " .. table.concat(first, " "))
	P("    last  " .. table.concat(last, " "))
	if i == 1 and m > 0 then
		P("    raw first entry (" .. type(edges[1]) .. "):")
		debugPrint(edges[1])
		local ent, idx = parts(edges[1])
		local tn = ent and g(ent, C.TRANSPORT_NETWORK)
		P(string.format("    TRANSPORT_NETWORK of %s: %d edges, %d nodes", tostring(ent), n(tn and tn.edges), n(tn and tn.nodes)))
		-- debugPrint(tn.edges[idx + 1]) crashed the game (2026-10-26) - don't dump TransportNetwork edges
	end
	end
end
P("done")
