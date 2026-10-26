-- Console probe: which lane a line's vehicle drives on - the geometry of MOVE_PATH lanes (TRANSPORT_NETWORK.edges[i].geometry).
-- Paste into the in-game console; lines start with [lane] in stdout.txt. Looks for a line named "67" (also "Linia 67"), else the first one.
-- Checks whether edgeId.index is 0- or 1-based: the lane's middle must lie within the street (distance from the edge's chord).
-- Doesn't debugPrint TN edges (that crashed the game, see route-probe.lua) - only reads .geometry and calcPosition.
local C = api.type.ComponentType
local function P(s) print("[lane] " .. s) end
local function g(e, c) local ok, v = pcall(api.engine.getComponent, e, c); return ok and v or nil end
local function f(o, k) local ok, v = pcall(function() return o[k] end); return ok and v or nil end
local function n(l) if l == nil then return 0 end local ok, k = pcall(function() return #l end); return ok and k or 0 end
local function name(e) local ok, s = pcall(function() return api.engine.util.getEntityName(e) end); return ok and tostring(s) or "?" end

local lines = api.engine.system.lineSystem.getLinesForPlayer(api.engine.util.getPlayer())
local line
for i = 1, n(lines) do
	local nm = name(lines[i])
	if nm:match("^67$") or nm:match("%s67$") then line = lines[i]; break end
end
line = line or lines[1]
P(string.format("line %s '%s'", tostring(line), name(line)))

-- the middle of TN edge i of entity e (Lua index), or nil + why
local function laneMid(tn, i)
	local te = f(tn.edges, i)
	if not te then return nil, "no edge" end
	local geom = f(te, "geometry")
	if not geom then return nil, "no geometry" end
	-- api.engine.util.calcPosition is nil (first run, 2026-10-26): it's under util.transport; EdgeGeometry.calcPos as a fallback
	local ok, p = pcall(api.engine.util.transport.calcPosition, geom, 0.5)
	if not ok or not p then
		local ok2, r = pcall(api.type.EdgeGeometry.calcPos, geom, 0.5)
		if not ok2 or not r then return nil, "calcPosition: " .. tostring(p) .. " / calcPos: " .. tostring(r) end
		p = f(r, 1) or r
	end
	return { x = f(p, "x") or f(p, 1), y = f(p, "y") or f(p, 2) }
end

-- distance of point p from the chord of a BASE_EDGE (its end positions)
local function chordDist(be, p)
	local a, b = f(be, "position0"), f(be, "position1")
	if not a or not b then return nil end
	local ax, ay, bx, by = a.x, a.y, b.x, b.y
	local dx, dy = bx - ax, by - ay
	local len = math.sqrt(dx * dx + dy * dy)
	if len == 0 then return nil end
	return ((p.x - ax) * dy - (p.y - ay) * dx) / len -- signed: + to the right of node0 -> node1
end

local vs = api.engine.system.transportVehicleSystem.getLineVehicles(line)
for vi = 1, math.min(2, n(vs)) do
	local mp = g(vs[vi], C.MOVE_PATH)
	local edges = mp and mp.path and mp.path.edges
	P(string.format("vehicle %s: %d path entries", tostring(vs[vi]), n(edges)))
	for j = 1, math.min(25, n(edges)) do
		local pe = edges[j]
		local id = f(pe, "edgeId")
		local ent, idx = id and f(id, "entity"), id and f(id, "index")
		local be = ent and g(ent, C.BASE_EDGE)
		local tn = ent and g(ent, C.TRANSPORT_NETWORK)
		local s = string.format("  %2d %s/%s dir %s %s, TN edges %d", j, tostring(ent), tostring(idx), tostring(f(pe, "dir")),
			be and "BASE_EDGE" or "junction", n(tn and tn.edges))
		if be and tn and type(idx) == "number" then
			for _, k in ipairs({ idx + 1, idx }) do
				local m, why = laneMid(tn, k)
				local d = m and chordDist(be, m)
				s = s .. string.format(" | [%d] %s", k, m and string.format("%.1f,%.1f off %.1f", m.x, m.y, d or 0/0) or why)
			end
			local w = 0
			for k = 1, n(be.laneConfigs) do w = w + (f(be.laneConfigs[k], "width") or 0) end
			s = s .. string.format(" | width %.0f", w)
		end
		P(s)
	end
end
P("done")
