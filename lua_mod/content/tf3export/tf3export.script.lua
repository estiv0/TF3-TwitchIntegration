-- Data export game script.
--
-- Findings from the game (2026-10-02, details in docs/tf3-api-findings.md):
--   * update()/handleEvent() run on many simulation threads ("Sim Pool 1..N"), each
--     with its own Lua state; there's no io or app there. Only state:get()/set() is shared.
--   * guiUpdate() runs on ONE GUI thread (module variables persist between frames),
--     and app.saveUserdata writes files to ...\local\tf3export\.
--
-- Division of work:
--   * handleEvent (simulation): after every build/demolition (onPostBuildProposal) bumps
--     the netVersion counter in the shared state.
--   * guiUpdate (GUI): the dynamic export in chunks (dynamicBudgetMs per frame); the network rebuilt in chunks
--     after netVersion changes (with debounce) and written as a full dump (once per session)
--     or a diff against the previous version; the terrain (terrain.lua) in chunks after the network
--     and again after the terrain tile revisions change.

local json = ug_require "json.lua"
local util = ug_require "util.lua"
local collectors = ug_require "collectors.lua"
local writer = ug_require "writer.lua"
local bench = ug_require "bench.lua"
local terrain = ug_require "terrain.lua"
local commands = ug_require "commands.lua"
local effects = ug_require "effects.lua"

local cfg = {
	dynamicEveryDays = 2,          -- export of towns, industry, stations, lines and vehicles (game time)...
	minDynamicWallSec = 10,        -- ...but no more often than every this many seconds of real time
	dynamicBudgetMs = 3,           -- work time on the dynamic export per frame (a single collector,
	                               -- e.g. towns, isn't split - step times are in the "dynamic #N" log line)
	networkDebounceSec = 2,        -- after a build event we wait for the player to stop clicking
	networkSafetyEveryWallSec = 300, -- a full network comparison even without events (safety net)
	networkBudgetMs = 2,           -- work time on the network per frame
	formatVersion = 5,
	financeYears = 5,              -- columns of the finance table (years, as in the Finances window)
	historyMaxPoints = 300,        -- at most this many points per history series (balance, company value, residents)
	vehicles = true,               -- vehicles (list, positions, count per line); false = no vehicles in the export
	vehiclePositions = true,       -- vehicle position and speed (smooth movement on the website); false = off
	vehicleDetails = true,         -- vehicle model, age, maintenance state and capacity; false = off
	vehiclePositionsMaxMs = 30,    -- positions are collected in chunks within the frame budget; if together they take longer
	                               -- (averaged over 3 exports), they're turned off until the end of the session (log: "vehicle positions")
	benchmark = false,             -- a one-off measurement of the write format (bench.lua), results in docs
	terrainStepM = 20,             -- heightmap sample spacing (m)
	terrainMaxSide = 2048,         -- at most this many samples per side (a bigger map -> a bigger step);
	                               -- a 20x41 km map -> a 21 m step, 976 x 1951 (~2M samples, ~4 s of CPU in chunks)
	terrainBudgetMs = 4,           -- work time on the terrain per frame
	terrainCheckEveryWallSec = 30, -- how often to check the terrain tile revisions (terraforming)
	terrainBench = false,          -- a one-off measurement of terrain read methods (terrain.lua); stutters the game for ~6 s, results in docs
	terrain = true,                -- terrain export; false = off
	settingsEveryWallSec = 5,      -- how often to read the settings from Tf3Bridge (see SETTINGS)
	commandsEveryWallSec = 1,      -- how often to read the command queue from Tf3Bridge (commands.lua)
}

-- Settings from the "Mod settings" window in Tf3Bridge: local\towns_industries\tf3export_settings.lua
-- (the app.saveUserdata format). Read with app.loadUserdata every settingsEveryWallSec, so
-- changes apply without restarting the game. Key -> { type, min, max }; ranges as in ModSettings.cs.
-- A missing file or a bad value = the default from cfg above.
local SETTINGS_DIR, SETTINGS_FILE = util.USERDATA_DIR, util.FILE_PREFIX .. "settings"
local SETTINGS = {
	dynamicEveryDays = { "number", 1, 30 },
	minDynamicWallSec = { "number", 2, 300 },
	dynamicBudgetMs = { "number", 1, 10 },
	vehicles = { "boolean" },
	vehicleDetails = { "boolean" },
	terrain = { "boolean" },
	terrainStepM = { "number", 5, 100 },
}
local DEFAULTS = {}
for key in pairs(SETTINGS) do DEFAULTS[key] = cfg[key] end

---------------------------------------------------------------- GUI state
-- Persistent between frames of the GUI thread, not stored in the save.
local S = {
	session = nil,
	seqDynamic = 0,
	seqNetwork = 0,          -- a shared counter for "network" and "networkDelta"
	lastGameTime = nil,
	lastDynamicTime = nil,
	lastDynamicWall = nil,
	rows = nil,              -- id -> encoded edge row from the last write
	net = nil,               -- the current network build task
	netVersionSeen = nil,    -- the last handled netVersion value from the shared state
	netDirtyWall = nil,      -- when we noticed the change (debounce)
	lastNetworkWall = nil,   -- when the last rebuild started
	seqTerrain = 0,
	terrain = nil,           -- the current terrain export task
	terrainSig = nil,        -- the terrain revision signature from the last export
	lastTerrainCheckWall = nil,
	lineStats = {},          -- line id -> statistics (collectors.lineStats), refreshed every game month
	lineStatsKey = nil,      -- the calendar month the cache comes from
	prevYear = nil,          -- { year, from, to } of the previous calendar year, or nil
	profitTotals = {},       -- line id -> profit over finished years (collectors.lineProfitTotal), once per game year
	profitTotalsYear = nil,  -- the prevYear.year the cache comes from
	vehiclePos = true,       -- vehicle positions in this session (turned off when over budget)
	vehiclePosMs = {},       -- position collection times from the last exports (ms)
	dyn = nil,               -- the current dynamic export task
	company = nil,           -- finances and history (collectors.financeTable / worldHistory), every game month
	companyKey = nil,        -- the month company comes from
	settingsWall = nil,      -- when we last read the settings (doesn't depend on the session)
	commandsWall = nil,      -- when we last read the command queue
}
local terrainBenchDone = false -- once per game start, not per session

local function wallTime()
	return (os and os.time) and os.time() or nil
end

local function newSession(now)
	local t = wallTime() or now or 0
	S.session = string.format("%08x%04x", t % 0x100000000, math.random(0, 0xffff))
	S.seqDynamic, S.seqNetwork = 0, 0
	S.lastDynamicTime, S.lastDynamicWall = nil, nil
	S.rows, S.net, S.netDirtyWall, S.lastNetworkWall = nil, nil, nil, nil
	S.seqTerrain, S.terrain, S.terrainSig, S.lastTerrainCheckWall = 0, nil, nil, nil
	S.lineStats, S.lineStatsKey, S.prevYear = {}, nil, nil
	S.profitTotals, S.profitTotalsYear = {}, nil
	S.vehiclePos, S.vehiclePosMs, S.dyn = cfg.vehiclePositions, {}, nil
	S.company, S.companyKey = nil, nil
	commands.reset()
	util.log(string.format("new session %s (writer mode '%s')", S.session, writer.mode()))
end

---------------------------------------------------------------- settings
-- Loads SETTINGS into cfg; returns a list of changed keys ("key=value").
-- No file = defaults; a failed read (e.g. the file is being written) = no change.
local function loadSettings()
	local t = {}
	if util.userdataExists(SETTINGS_DIR, SETTINGS_FILE) then
		local ok, loaded = pcall(function() return app.loadUserdata(SETTINGS_DIR, SETTINGS_FILE) end)
		if not ok or type(loaded) ~= "table" then return {} end
		t = loaded
	end
	local changed = {}
	for key, spec in pairs(SETTINGS) do
		local v = t[key]
		if type(v) ~= spec[1] or v ~= v then -- wrong type or NaN
			v = DEFAULTS[key]
		elseif spec[1] == "number" then
			v = math.max(spec[2], math.min(spec[3], math.floor(v)))
		end
		if v ~= cfg[key] then
			cfg[key] = v
			changed[#changed + 1] = key .. "=" .. tostring(v)
		end
	end
	table.sort(changed)
	return changed
end

local function applySettings()
	local changed = loadSettings()
	if #changed == 0 then return end
	util.log("settings: " .. table.concat(changed, ", "))
	for _, c in ipairs(changed) do
		local key = c:match("^(%w+)=")
		if key == "vehicles" then
			S.vehiclePos, S.vehiclePosMs = cfg.vehiclePositions, {} -- after turning on, positions start over
		elseif key == "terrain" or key == "terrainStepM" then
			S.terrain, S.terrainSig = nil, nil -- abort the current export; when on, it starts over
		end
	end
end

---------------------------------------------------------------- line statistics
-- Finances and transport change slowly, so we compute them once per game calendar month
-- (a new line gets its statistics right away), and the other exports send the cache.
local function lineStatsMonth(now)
	local d = util.date(now)
	local key = d and d.year and d.month and (d.year * 12 + d.month)
		or math.floor(now / (30 * util.dayLength())) -- without a calendar: every ~30 days
	if key ~= S.lineStatsKey then
		S.lineStatsKey, S.lineStats, S.prevYear = key, {}, nil
		local okY, ys = pcall(util.yearStart, now)
		if okY and ys and ys > 0 then
			local okP, ps = pcall(util.yearStart, ys - 1)
			if okP and ps then S.prevYear = { year = d.year - 1, from = ps, to = ys } end
		end
		local year = S.prevYear and S.prevYear.year
		if year ~= S.profitTotalsYear then S.profitTotals, S.profitTotalsYear = {}, year end
	end
end

-- Statistics of one line (from the cache or computed); true = computed now.
local function attachLineStats(l, now)
	local fresh = S.lineStats[l.id] == nil
	if fresh then
		local ok, st = pcall(collectors.lineStats, l.id, now, S.prevYear)
		S.lineStats[l.id] = ok and st or false
		if not ok then util.log("lineStats failed for " .. tostring(l.id) .. ": " .. tostring(st)) end
	end
	l.stats = S.lineStats[l.id] or nil
	-- profit over finished years: the line's whole accounting history, so only once per game year
	if l.stats and S.prevYear then
		if S.profitTotals[l.id] == nil then
			S.profitTotals[l.id] = collectors.lineProfitTotal(l.id, S.prevYear.to) or false
			fresh = true
		end
		l.stats.profitTotal = S.profitTotals[l.id] or nil
	end
	return fresh
end

---------------------------------------------------------------- vehicle positions
-- Averaged over the last 3 exports longer than vehiclePositionsMaxMs -> turned off until the end of the session
-- (the website goes back to guessing from the stops).
local function trackVehiclePositions(count, ms)
	local h = S.vehiclePosMs
	h[#h + 1] = ms
	if #h > 3 then table.remove(h, 1) end
	if #h < 3 then return end
	local avg = (h[1] + h[2] + h[3]) / 3
	if avg > cfg.vehiclePositionsMaxMs then
		S.vehiclePos = false
		util.log(string.format("vehicle positions: %.1f ms for %d vehicles (max %d ms) - disabled for this session",
			avg, count, cfg.vehiclePositionsMaxMs))
	end
end

---------------------------------------------------------------- dynamic export
-- Spread over frames like the network: the S.dyn task goes through the steps in DYN_STEPS in order, at most
-- dynamicBudgetMs of work per frame. Loops (vehicle positions, line statistics, JSON encoding
-- item by item) are interrupted midway; a single collector (e.g. towns) runs in one piece.
-- The snapshot is built over several to a dozen frames (a fraction of a second of game time) - irrelevant for the website.
-- Step: function(job, deadline) -> true when finished; job.i is the step's loop counter (from 1 in every step).

local DYN_ARRAYS = { "towns", "industries", "warehouses", "stations", "lines", "vehicles" }
local CHECK_EVERY = 20 -- check the clock every this many loop items

-- The next loop item; true = the budget for this frame is used up.
local function nextItem(job, deadline)
	job.i = job.i + 1
	return job.i % CHECK_EVERY == 0 and util.clock() >= deadline
end

local DYN_STEPS = {
	{ "vehicles", function(job)
		-- without vehicles snap.vehicles stays nil: no key in the JSON and no vehicleCount for lines (the website shows "–")
		if cfg.vehicles then job.snap.vehicles = collectors.vehicles() end
		return true
	end },
	{ "positions", function(job, deadline)
		local vs = job.snap.vehicles
		if not vs or not S.vehiclePos then return true end
		while job.i <= #vs do
			if not collectors.vehiclePosition(vs[job.i]) then
				S.vehiclePos = false
				util.log("vehicle positions: api.engine.util.vehicle unavailable, disabled for this session")
				return true
			end
			if nextItem(job, deadline) then return false end
		end
		return true
	end },
	{ "details", function(job, deadline)
		local vs = job.snap.vehicles
		if not vs or not cfg.vehicleDetails then return true end
		if job.i == 1 then job.models, job.nowYear = {}, collectors.fracYear(job.now) end
		while job.i <= #vs do
			-- new API: an error with one vehicle doesn't block the export
			local ok, err = pcall(collectors.vehicleDetails, vs[job.i], job.now, job.nowYear, job.models)
			if not ok and not job.detailsError then
				job.detailsError = true
				util.log("vehicle details failed for " .. tostring(vs[job.i].id) .. ": " .. tostring(err))
			end
			if nextItem(job, deadline) then return false end
		end
		return true
	end },
	{ "stations", function(job)
		-- stations are a new, unverified API: an error must not block the rest of the export
		local ok, stations, kinds = pcall(collectors.stations)
		if not ok then
			util.log("stations failed: " .. tostring(stations))
			stations, kinds = {}, {}
		end
		job.snap.stations, job.stationKinds = stations, kinds
		return true
	end },
	{ "lines", function(job)
		job.snap.lines = collectors.lines(job.snap.vehicles, job.stationKinds)
		lineStatsMonth(job.now)
		return true
	end },
	{ "lineStats", function(job, deadline)
		local lines = job.snap.lines
		while job.i <= #lines do
			-- new API: an error doesn't block the export
			local ok, fresh = pcall(attachLineStats, lines[job.i], job.now)
			job.i = job.i + 1
			if ok and fresh then
				job.statsCount = job.statsCount + 1
				if util.clock() >= deadline then return false end -- computing is expensive, the cache isn't
			end
		end
		return true
	end },
	{ "towns", function(job)
		job.snap.towns = collectors.towns()
		return true
	end },
	{ "industries", function(job)
		job.snap.industries = collectors.industries()
		return true
	end },
	{ "warehouses", function(job)
		-- new API: an error doesn't block the rest of the export
		local ok, list = pcall(collectors.warehouses)
		if not ok then
			util.log("warehouses failed: " .. tostring(list))
			list = {}
		end
		job.snap.warehouses = list
		return true
	end },
	{ "player", function(job)
		job.snap.player = collectors.player()
		return true
	end },
	-- Company: the finance table (years) and the history from the world logbook. It changes slowly, and computing it
	-- (the whole accounting history) may be expensive, so once per game month (the key from the "lines" step).
	{ "company", function(job)
		if S.company == nil or S.companyKey ~= S.lineStatsKey then
			S.companyKey = S.lineStatsKey
			local company = {}
			local ok, fin = pcall(collectors.financeTable, cfg.financeYears)
			if ok then company.finance = fin else util.log("finance table failed: " .. tostring(fin)) end
			local okH, hist = pcall(collectors.worldHistory,
				{ "companyBalance", "companyTotalValue", "townResidentialCount", "townCommercialCount", "townIndustrialCount" }, cfg.historyMaxPoints)
			if okH then company.history = hist else util.log("world history failed: " .. tostring(hist)) end
			S.company = company
			job.companyFresh = true
		end
		job.snap.company = S.company
		return true
	end },
	{ "encode", function(job, deadline)
		while job.k <= #DYN_ARRAYS do
			local key = DYN_ARRAYS[job.k]
			local list, parts = job.snap[key] or {}, job.parts[key]
			while job.i <= #list do
				parts[job.i] = json.encode(list[job.i])
				if nextItem(job, deadline) then return false end
			end
			job.k, job.i = job.k + 1, 1
		end
		return true
	end },
	{ "write", function(job)
		local head = json.encode({
			v = cfg.formatVersion,
			kind = "dynamic",
			session = S.session,
			seq = job.seq,
			gameTime = job.now,
			date = util.date(job.now),
			player = job.snap.player,
			company = job.snap.company,
			effects = effects.list(), -- active effects from channel points rewards (effects.lua)
			models = job.models and (next(job.models) and job.models or json.EMPTY_OBJECT), -- model id -> { name, icon } (vehicles[].model)
		}):sub(1, -2) -- without the closing brace
		local out = { head }
		for _, key in ipairs(DYN_ARRAYS) do
			if job.snap[key] ~= nil then -- skip the ones turned off (vehicles)
				out[#out + 1] = ',"' .. key .. '":[' .. table.concat(job.parts[key], ",") .. "]"
			end
		end
		out[#out + 1] = "}"
		job.content = table.concat(out)
		writer.write("dynamic", S.session, job.seq, job.content)
		return true
	end },
}

local function startDynamic(now)
	S.seqDynamic = S.seqDynamic + 1
	local parts = {}
	for _, key in ipairs(DYN_ARRAYS) do parts[key] = {} end
	S.dyn = { now = now, seq = S.seqDynamic, step = 1, i = 1, k = 1, snap = {}, parts = parts,
		stationKinds = {}, statsCount = 0, times = {}, work = 0, frames = 0, maxFrame = 0, t0 = util.clock() }
end

local function finishDynamic(job)
	local ms = function(name) return (job.times[name] or 0) * 1000 end
	local nVehicles = #(job.snap.vehicles or {})
	if S.vehiclePos and job.snap.vehicles then trackVehiclePositions(nVehicles, ms("positions")) end
	util.log(string.format("dynamic #%d: work %.0f ms in %d frames (max %.0f ms/frame), %.2f s wall; "
		.. "vehicles %.0f, positions %.0f, details %.0f (%d vehicles), stations %.0f, lines %.0f, line stats %.0f (%d lines), "
		.. "towns %.0f, industries %.0f, warehouses %.0f, player %.0f, company %.0f%s, encode %.0f, write %.0f ms; %d B",
		job.seq, job.work * 1000, job.frames, job.maxFrame * 1000, util.clock() - job.t0,
		ms("vehicles"), ms("positions"), ms("details"), nVehicles, ms("stations"), ms("lines"), ms("lineStats"), job.statsCount,
		ms("towns"), ms("industries"), ms("warehouses"), ms("player"), ms("company"), job.companyFresh and " (refreshed)" or "",
		ms("encode"), ms("write"), #(job.content or "")))
end

local function stepDynamic()
	local job = S.dyn
	local start = util.clock()
	local deadline = start + cfg.dynamicBudgetMs / 1000
	repeat -- at least one step per frame
		local name, fn = DYN_STEPS[job.step][1], DYN_STEPS[job.step][2]
		local s = util.clock()
		local done = fn(job, deadline)
		job.times[name] = (job.times[name] or 0) + (util.clock() - s)
		if done then job.step, job.i = job.step + 1, 1 end
	until job.step > #DYN_STEPS or util.clock() >= deadline
	local frame = util.clock() - start
	job.work, job.frames = job.work + frame, job.frames + 1
	if frame > job.maxFrame then job.maxFrame = frame end
	if job.step > #DYN_STEPS then
		S.dyn = nil
		finishDynamic(job)
	end
end

---------------------------------------------------------------- network export
-- Edges encoded in chunks (networkBudgetMs per frame) into an id -> row map.
-- When done:
--   * no previous map -> a full "network" dump,
--   * otherwise         -> a "networkDelta" with added/changed rows
--                          and the IDs of removed edges (nothing if there are no changes).
-- The snapshot isn't atomic: building in the meantime can give a mixed state.
local function startNetwork(now, reason)
	S.net = { ids = collectors.edgeIds(), i = 1, rows = {}, count = 0, skipped = 0,
		gameTime = now, t0 = util.clock(), work = 0, reason = reason }
end

local function header(kind, net, extra)
	local h = {
		v = cfg.formatVersion,
		kind = kind,
		session = S.session,
		seq = S.seqNetwork,
		gameTime = net.gameTime,
		fields = collectors.EDGE_FIELDS,
	}
	for k, v in pairs(extra or {}) do h[k] = v end
	return json.encode(h):sub(1, -2) -- without the closing brace
end

local function finishNetwork(net)
	local t = util.clock()
	local kind, content, summary

	if S.rows == nil then
		local parts = {}
		for _, row in pairs(net.rows) do parts[#parts + 1] = row end
		S.seqNetwork = S.seqNetwork + 1
		kind = "network"
		content = header(kind, net) .. ',"edges":[' .. table.concat(parts, ",") .. "]}"
		summary = string.format("network #%d (full): %d edges", S.seqNetwork, #parts)
	else
		local changed, removed = {}, {}
		for id, row in pairs(net.rows) do
			if S.rows[id] ~= row then changed[#changed + 1] = row end
		end
		for id in pairs(S.rows) do
			if net.rows[id] == nil then removed[#removed + 1] = id end
		end
		if #changed == 0 and #removed == 0 then
			S.rows = net.rows
			if net.reason == "periodic" then
				util.log(string.format("network: no change (%s, %d edges)", net.reason, net.count))
			end
			return
		end
		S.seqNetwork = S.seqNetwork + 1
		table.sort(removed)
		kind = "networkDelta"
		content = header(kind, net, { base = S.seqNetwork - 1 })
			.. ',"changed":[' .. table.concat(changed, ",") .. ']'
			.. ',"removed":' .. json.encode(removed) .. "}"
		summary = string.format("networkDelta #%d: %d changed, %d removed", S.seqNetwork, #changed, #removed)
	end

	S.rows = net.rows
	writer.write(kind, S.session, S.seqNetwork, content)
	util.log(string.format("%s (%s, %d skipped), work %.1f ms over %.1f s, write %.1f ms, %d B",
		summary, net.reason, net.skipped, net.work * 1000, util.clock() - net.t0,
		(util.clock() - t) * 1000, #content))

	if cfg.benchmark and kind == "network" then
		bench.run(net.ids)
	end
end

local function stepNetwork()
	local net = S.net
	local start = util.clock()
	local deadline = start + cfg.networkBudgetMs / 1000
	while net.i <= #net.ids do
		local id = net.ids[net.i]
		local row = collectors.edge(id) -- the entity may have disappeared
		if row then
			net.rows[id] = json.encode(row)
			net.count = net.count + 1
		else
			net.skipped = net.skipped + 1
		end
		net.i = net.i + 1
		if util.clock() >= deadline then
			net.work = net.work + (util.clock() - start)
			return
		end
	end
	net.work = net.work + (util.clock() - start)
	S.net = nil
	finishNetwork(net)
end

---------------------------------------------------------------- terrain export
-- The heightmap in chunks (terrainBudgetMs per frame), only while no network work is going on.
-- Again after the terrain tile revisions change (checked every terrainCheckEveryWallSec).
local function startTerrain(now, reason)
	S.terrain = terrain.start({ stepM = cfg.terrainStepM, maxSide = cfg.terrainMaxSide,
		gameTime = now, reason = reason })
	if S.terrain == nil then
		S.terrainSig = "unavailable"
		util.log("terrain: api.engine.terrain unavailable, terrain export disabled for this session")
	end
end

local function stepTerrain()
	local job = S.terrain
	if not terrain.step(job, cfg.terrainBudgetMs) then return end
	S.terrain = nil
	S.terrainSig = job.revSig or "?"
	S.seqTerrain = S.seqTerrain + 1
	local t = util.clock()
	local content = terrain.content(job, {
		v = cfg.formatVersion, kind = "terrain", session = S.session, seq = S.seqTerrain, gameTime = job.gameTime,
	})
	local t1 = util.clock()
	writer.write("terrain", S.session, S.seqTerrain, content)
	local t2 = util.clock()
	util.log(string.format("terrain #%d (%s): %d x %d @ %d m, z %.1f..%.1f, sample %.0f ms (%d calls), "
		.. "encode %.0f ms + concat %.0f ms, write %.0f ms, total %.1f s wall, %d B",
		S.seqTerrain, job.reason, job.nx, job.ny, job.step, job.zMin, job.zMax,
		job.sampleWork * 1000, job.calls, job.encodeWork * 1000, (t1 - t) * 1000,
		(t2 - t1) * 1000, t2 - job.t0, #content))
end

local function updateTerrain(now, wall)
	if not cfg.terrain then return end
	if S.terrain then
		stepTerrain()
	elseif S.net == nil and S.rows ~= nil then -- the network first
		if cfg.terrainBench and not terrainBenchDone then
			terrainBenchDone = true
			terrain.bench({ budgetMs = cfg.terrainBudgetMs })
		elseif S.terrainSig == nil then
			S.lastTerrainCheckWall = wall
			startTerrain(now, "initial")
		elseif S.terrainSig ~= "unavailable" and wall - (S.lastTerrainCheckWall or 0) >= cfg.terrainCheckEveryWallSec then
			S.lastTerrainCheckWall = wall
			local sig = terrain.revisionSignature()
			if sig ~= nil and sig ~= S.terrainSig then startTerrain(now, "terrain changed") end
		end
	end
end

---------------------------------------------------------------- game script
local function due(last, now, everyDays, day)
	return last == nil or now - last >= everyDays * day
end

-- Does the build proposal touch the network? onPostBuildProposal also arrives for town
-- buildings etc. (every ~3 s in a test), so without a filter the network would be rebuilt nonstop.
-- param = { proposal, proposalData, resultEntities (post only), playerInitiated }
local function hasItems(v) return #util.list(v) > 0 end

local function hasFrozenEdges(constructionEntity)
	local c = util.comp(constructionEntity, util.ct.CONSTRUCTION)
	return c ~= nil and hasItems(util.field(c, "frozenEdges"))
end

local function touchesNetwork(name, param)
	local proposal = util.field(param, 1)
	if proposal == nil then return true end
	local street = util.field(proposal, "proposal")
	if street and (hasItems(util.field(street, "addedSegments")) or hasItems(util.field(street, "removedSegments"))) then
		return true
	end
	if name == "onPreBuildProposal" then
		-- removed constructions still exist: a station/depot with its own edges?
		for _, e in ipairs(util.list(util.field(proposal, "toRemove"))) do
			if hasFrozenEdges(e) then return true end
		end
	else
		-- added constructions already exist as result entities
		for _, e in ipairs(util.list(util.field(param, 3))) do
			if hasFrozenEdges(e) then return true end
		end
	end
	return false
end

local script = {
	-- Simulation threads: only the subscription to build events (see handleEvent).
	-- Subscriptions are stored in the save, so we version them, so that older saves
	-- (e.g. only with onPostBuildProposal) get the new set.
	update = function(_userParams, state, _dt)
		local s = state:get() or {}
		if s.subsVersion ~= 3 then
			state:subscribeToEvent("onPreBuildProposal")  -- demolitions (entities still exist)
			state:subscribeToEvent("onPostBuildProposal") -- building (result entities already exist)
			state:subscribeToEvent(effects.EVENT)         -- effects from rewards (effects.lua), sent from the GUI thread
			s.subsVersion = 3
			state:set(s)
		end
	end,

	-- Simulation threads: a build/demolition proposal touching the network (by the player or a town)
	-- bumps a counter that guiUpdate reads from the shared state.
	handleEvent = function(_userParams, state, _src, id, name, param)
		if name == effects.EVENT and id == effects.SCRIPT_ID then
			-- the GUI thread can't write the state: the list of active effects arrives as an event and goes into the save
			local s = state:get() or {}
			s.effects = (type(param) == "table" and param.effects) or {}
			s.effectCtl = (type(param) == "table" and param.ctl) or {} -- productivity / speed bases (effects.lua)
			state:set(s)
			return
		end
		if name == "onPreBuildProposal" or name == "onPostBuildProposal" then
			local ok, touches = pcall(touchesNetwork, name, param)
			if not ok or touches then -- on an error better rebuild than miss something
				local s = state:get() or {}
				s.netVersion = (s.netVersion or 0) + 1
				state:set(s)
			end
		end
	end,

	guiUpdate = function(_userParams, state, _guiState)
		local now = util.gameTime()
		if now == nil then return end
		local wall = wallTime() or 0

		if S.settingsWall == nil or wall - S.settingsWall >= cfg.settingsEveryWallSec then
			S.settingsWall = wall
			applySettings()
		end

		-- commands from Tf3Bridge: an error must not stop the export
		if S.commandsWall == nil or wall - S.commandsWall >= cfg.commandsEveryWallSec then
			S.commandsWall = wall
			local okCmd, errCmd = pcall(commands.poll, wall, now, state:get() or {})
			if not okCmd then util.log("commands failed: " .. tostring(errCmd)) end
		end

		-- new session: the first start or game time rolled back (an older save loaded)
		if S.session == nil or (S.lastGameTime and now < S.lastGameTime) then
			newSession(now)
		end
		local paused = S.lastGameTime ~= nil and now == S.lastGameTime
		S.lastGameTime = now

		-- network changes reported by handleEvent
		local shared = state:get() or {}
		local netVersion = shared.netVersion or 0
		if netVersion ~= S.netVersionSeen then
			S.netVersionSeen = netVersion
			S.netDirtyWall = wall
		end

		if S.net then
			stepNetwork()
		elseif S.rows == nil then
			S.lastNetworkWall, S.netDirtyWall = wall, nil
			startNetwork(now, "initial")
		elseif S.netDirtyWall and wall - S.netDirtyWall >= cfg.networkDebounceSec then
			S.lastNetworkWall, S.netDirtyWall = wall, nil
			startNetwork(now, "build event")
		elseif not paused and wall - (S.lastNetworkWall or 0) >= cfg.networkSafetyEveryWallSec then
			S.lastNetworkWall = wall
			startNetwork(now, "periodic")
		end

		-- terrain is a new, unverified API: an error turns it off for this session instead of breaking guiUpdate
		local okTerrain, errTerrain = pcall(updateTerrain, now, wall)
		if not okTerrain then
			S.terrain, S.terrainSig = nil, "unavailable"
			util.log("terrain export failed, disabled for this session: " .. tostring(errTerrain))
		end

		if S.dyn then
			stepDynamic()
		elseif not paused and due(S.lastDynamicTime, now, cfg.dynamicEveryDays, util.dayLength())
			and (S.lastDynamicWall == nil or wall - S.lastDynamicWall >= cfg.minDynamicWallSec) then
			S.lastDynamicTime = now
			S.lastDynamicWall = wall
			startDynamic(now)
			stepDynamic()
		end
	end,
}

-- .script.lua files in TF3 have to define a global data() returning a table of functions
-- (pattern: base/base_mod.script.lua). The "return {...}" style only works in .tl files.
function data()
	return script
end
