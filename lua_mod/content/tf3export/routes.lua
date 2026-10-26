-- The real routes of lines, assembled from the paths of their vehicles (MOVE_PATH.path.edges).
-- Checked in the game (docs/probes/route-probe.lua, 2026-10-26): a path entry is { edgeId = { entity, index }, dir },
-- entity = one of our network edges (BASE_EDGE) or the lanes through a junction (no BASE_EDGE), index = the lane.
-- A path only covers the current leg - from the previous stop to the stop the vehicle heads to (stopIndex) - so a
-- line is assembled leg by leg from its vehicles, and a leg is kept until a vehicle drives it differently.
-- The legs of a line are dropped when its stops change. Lines rarely change, so tf3export.script.lua samples every
-- few minutes and writes a network delta only when a route row changed.

local util = ug_require "util.lua"
local json = ug_require "json.lua"

local ct = util.ct
local field = util.field

local R = {}

local lines = {} -- line id -> { sig = stops signature, n = stop count, legs = { [stop index from 0] = { edge ids } } }

function R.reset()
	lines = {}
end

-- The stops of a line as a string (station group, station, terminal of each), to notice a changed line.
local function signature(l)
	local parts = {}
	for _, s in ipairs(util.list(l.stops)) do
		parts[#parts + 1] = tostring(s.stationGroup) .. ":" .. tostring(s.station) .. ":" .. tostring(s.terminal)
	end
	return table.concat(parts, ","), #parts
end

-- Starts a sampling pass: the player's lines (a changed line loses its legs, a removed one disappears) and the
-- vehicles to read. Returns the job for R.step.
function R.start()
	local ok, ids = pcall(function()
		return api.engine.system.lineSystem.getLinesForPlayer(api.engine.util.getPlayer())
	end)
	local current, items = {}, {}
	for _, id in ipairs(ok and util.list(ids) or {}) do
		local l = util.comp(id, ct.LINE)
		if l then
			current[id] = true
			local sig, n = signature(l)
			if not lines[id] or lines[id].sig ~= sig then lines[id] = { sig = sig, n = n, legs = {} } end
			local okV, vs = pcall(function() return api.engine.system.transportVehicleSystem.getLineVehicles(id) end)
			for _, v in ipairs(okV and util.list(vs) or {}) do items[#items + 1] = { id, v } end
		end
	end
	for id in pairs(lines) do
		if not current[id] then lines[id] = nil end
	end
	return { items = items, i = 1, legs = 0 }
end

-- edgeId.index is the lane's index in its entity's TRANSPORT_NETWORK.edges, counted from 0
local LANE_INDEX_BASE = 1

-- The middle of the lane edgeId.index of edge entity e (world x, y rounded to 0.1 m), or nil. Only .geometry and
-- calcPosition are read: a debugPrint of a TN edge crashed the game (docs/tf3-api-findings.md).
local function laneMid(e, index)
	if type(index) ~= "number" then return nil end
	local tn = util.comp(e, ct.TRANSPORT_NETWORK)
	local te = tn and field(field(tn, "edges"), index + LANE_INDEX_BASE)
	local geom = te and field(te, "geometry")
	if not geom then return nil end
	local ok, p = pcall(api.engine.util.transport.calcPosition, geom, 0.5)
	if not ok or not p then
		local ok2, r = pcall(api.type.EdgeGeometry.calcPos, geom, 0.5) -- position, tangent, second derivative
		if not ok2 or not r then return nil end
		p = field(r, 1) or r
	end
	local x, y = field(p, "x"), field(p, "y")
	if type(x) ~= "number" or type(y) ~= "number" then return nil end
	return math.floor(x * 10 + 0.5) / 10, math.floor(y * 10 + 0.5) / 10
end

-- The leg a vehicle drives now: stop index, { edges in driving order } (lanes through junctions and repeated
-- edges - a lane change - left out), or nil. An edge is { id, x, y } with the middle of the lane the vehicle drives
-- on there (the first edge: the lane it starts on, the others: the lane it leaves on - at a stop, the stop's lane),
-- or just the id when the lane can't be read. isEdge(id) says whether id is an exported edge. The path entry's dir is
-- the direction of the lane, not of our edge (true also when driving against the edge), so it isn't exported; the
-- website orients the edges by continuity.
local function vehicleLeg(v, isEdge)
	local tv = util.comp(v, ct.TRANSPORT_VEHICLE)
	-- going to / leaving a depot or standing at a stop: the path isn't the line's leg
	if not tv or util.enumName("TransportVehicleState", tv.state, util.VEHICLE_STATES) ~= "EN_ROUTE" then return nil end
	local mp = util.comp(v, ct.MOVE_PATH)
	local path = mp and field(mp, "path")
	local out, last = {}, nil
	for _, pe in ipairs(util.list(path and field(path, "edges"))) do
		local id = field(pe, "edgeId")
		local e = id and field(id, "entity")
		if e and isEdge(e) and (e ~= last or #out > 1) then
			local x, y = laneMid(e, field(id, "index"))
			local entry = x and { e, x, y } or e
			if e == last then out[#out] = entry else out[#out + 1] = entry end
			last = e
		end
	end
	if #out == 0 or type(tv.stopIndex) ~= "number" then return nil end
	return tv.stopIndex, out
end

-- Reads vehicles within the budget; true when the pass is done.
function R.step(job, deadline, isEdge)
	while job.i <= #job.items do
		local lineId, v = job.items[job.i][1], job.items[job.i][2]
		job.i = job.i + 1
		local line = lines[lineId]
		local ok, stop, leg = pcall(vehicleLeg, v, isEdge)
		if ok and stop and line and stop < line.n then
			line.legs[stop] = leg
			job.legs = job.legs + 1
		end
		if job.i % 10 == 0 and util.clock() >= deadline then return false end
	end
	return true
end

-- Encoded route rows: line id -> '[lineId, legs]', legs = one entry per stop (from 0), the leg arriving at that stop
-- as edge ids in driving order, or false when no vehicle has driven it yet. Lines without any known leg are left out.
function R.rows()
	local out = {}
	for id, line in pairs(lines) do
		local legs, any = {}, false
		for k = 0, line.n - 1 do
			legs[k + 1] = line.legs[k] or false
			if line.legs[k] then any = true end
		end
		if any then out[id] = json.encode({ id, legs }) end
	end
	return out
end

return R
