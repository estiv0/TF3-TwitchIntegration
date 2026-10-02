-- Game script eksportu danych.
--
-- Ustalenia z gry (2026-10-02, szczegóły w docs/tf3-api-findings.md):
--   * update()/handleEvent() działają w wielu wątkach symulacji ("Sim Pool 1..N"), każdy
--     z osobnym stanem Lua; nie ma tam io ani app. Wspólny jest tylko state:get()/set().
--   * guiUpdate() działa w JEDNYM wątku GUI (zmienne modułu są trwałe między klatkami),
--     a app.saveUserdata zapisuje pliki do ...\local\tf3export\.
--
-- Podział pracy:
--   * handleEvent (symulacja): po każdej budowie/wyburzeniu (onPostBuildProposal) podbija
--     licznik netVersion we wspólnym stanie.
--   * guiUpdate (GUI): eksport dynamiczny w jednej klatce; sieć przebudowywana porcjami
--     po zmianie netVersion (z debounce) i zapisywana jako pełny zrzut (raz na sesję)
--     albo różnica względem poprzedniej wersji.

local json = ug_require "json.lua"
local util = ug_require "util.lua"
local collectors = ug_require "collectors.lua"
local writer = ug_require "writer.lua"
local bench = ug_require "bench.lua"

local cfg = {
	dynamicEveryDays = 2,          -- eksport miast, przemysłu, linii i pojazdów (czas gry)...
	minDynamicWallSec = 10,        -- ...ale nie częściej niż co tyle sekund czasu rzeczywistego
	networkDebounceSec = 2,        -- po zdarzeniu budowy czekamy, aż gracz skończy klikać
	networkSafetyEveryWallSec = 300, -- pełne porównanie sieci także bez zdarzeń (zabezpieczenie)
	networkBudgetMs = 2,           -- czas pracy nad siecią na klatkę
	formatVersion = 5,
	benchmark = false,             -- jednorazowy pomiar formatu zapisu (bench.lua), wyniki w docs
}

---------------------------------------------------------------- stan GUI
-- Trwały między klatkami wątku GUI, nie trafia do save'a.
local S = {
	session = nil,
	seqDynamic = 0,
	seqNetwork = 0,          -- wspólny licznik dla "network" i "networkDelta"
	lastGameTime = nil,
	lastDynamicTime = nil,
	lastDynamicWall = nil,
	rows = nil,              -- id -> zakodowany wiersz krawędzi z ostatniego zapisu
	net = nil,               -- bieżące zadanie budowy sieci
	netVersionSeen = nil,    -- ostatnio obsłużona wartość netVersion ze stanu wspólnego
	netDirtyWall = nil,      -- kiedy zauważyliśmy zmianę (debounce)
	lastNetworkWall = nil,   -- kiedy ostatnio startowała przebudowa
}

local function wallTime()
	return (os and os.time) and os.time() or nil
end

local function newSession(now)
	local t = wallTime() or now or 0
	S.session = string.format("%08x%04x", t % 0x100000000, math.random(0, 0xffff))
	S.seqDynamic, S.seqNetwork = 0, 0
	S.lastDynamicTime, S.lastDynamicWall = nil, nil
	S.rows, S.net, S.netDirtyWall, S.lastNetworkWall = nil, nil, nil, nil
	util.log(string.format("new session %s (writer mode '%s')", S.session, writer.mode()))
end

---------------------------------------------------------------- eksport dynamiczny
-- Miasta, przemysł, linie i pojazdy w JEDNEJ klatce, żeby snapshot był spójny.
local function exportDynamic(now)
	local t0 = util.clock()
	S.seqDynamic = S.seqDynamic + 1
	local vehicles = collectors.vehicles()
	local snapshot = {
		v = cfg.formatVersion,
		kind = "dynamic",
		session = S.session,
		seq = S.seqDynamic,
		gameTime = now,
		date = util.date(now),
		player = collectors.player(),
		towns = collectors.towns(),
		industries = collectors.industries(),
		lines = collectors.lines(vehicles),
		vehicles = vehicles,
	}
	local t1 = util.clock()
	local content = json.encode(snapshot)
	local t2 = util.clock()
	writer.write("dynamic", S.session, snapshot.seq, content)
	local t3 = util.clock()
	util.log(string.format("dynamic #%d: collect %.1f ms, encode %.1f ms, write %.1f ms, %d B",
		snapshot.seq, (t1 - t0) * 1000, (t2 - t1) * 1000, (t3 - t2) * 1000, #content))
end

---------------------------------------------------------------- eksport sieci
-- Krawędzie kodowane porcjami (networkBudgetMs na klatkę) do mapy id -> wiersz.
-- Po zakończeniu:
--   * brak poprzedniej mapy -> pełny zrzut "network",
--   * w przeciwnym razie    -> "networkDelta" z dodanymi/zmienionymi wierszami
--                              i ID usuniętych krawędzi (nic, jeśli brak zmian).
-- Snapshot nie jest atomowy: budowa w trakcie może dać mieszany stan.
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
	return json.encode(h):sub(1, -2) -- bez zamykającej klamry
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
		local row = collectors.edge(id) -- encja mogła zniknąć
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

---------------------------------------------------------------- game script
local function due(last, now, everyDays, day)
	return last == nil or now - last >= everyDays * day
end

-- Czy propozycja budowy dotyka sieci? onPostBuildProposal przychodzi też przy budynkach
-- miast itp. (w teście co ~3 s), więc bez filtra sieć byłaby przebudowywana non stop.
-- param = { proposal, proposalData, resultEntities (tylko post), playerInitiated }
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
		-- usuwane konstrukcje jeszcze istnieją: stacja/zajezdnia z własnymi krawędziami?
		for _, e in ipairs(util.list(util.field(proposal, "toRemove"))) do
			if hasFrozenEdges(e) then return true end
		end
	else
		-- dodane konstrukcje już istnieją jako encje wynikowe
		for _, e in ipairs(util.list(util.field(param, 3))) do
			if hasFrozenEdges(e) then return true end
		end
	end
	return false
end

local script = {
	-- Wątki symulacji: tylko subskrypcja zdarzeń budowy (patrz handleEvent).
	-- Subskrypcje są zapisywane w save'ie, więc wersjonujemy je, żeby starsze zapisy
	-- (np. tylko z onPostBuildProposal) dostały nowy zestaw.
	update = function(_userParams, state, _dt)
		local s = state:get() or {}
		if s.subsVersion ~= 2 then
			state:subscribeToEvent("onPreBuildProposal")  -- wyburzenia (encje jeszcze istnieją)
			state:subscribeToEvent("onPostBuildProposal") -- budowa (encje wynikowe już istnieją)
			s.subsVersion = 2
			state:set(s)
		end
	end,

	-- Wątki symulacji: propozycja budowy/wyburzenia dotykająca sieci (gracz albo miasto)
	-- podbija licznik, który guiUpdate odczytuje ze wspólnego stanu.
	handleEvent = function(_userParams, state, _src, _id, name, param)
		if name == "onPreBuildProposal" or name == "onPostBuildProposal" then
			local ok, touches = pcall(touchesNetwork, name, param)
			if not ok or touches then -- przy błędzie lepiej przebudować niż przegapić
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

		-- nowa sesja: pierwszy start albo cofnięcie czasu gry (wczytany starszy save)
		if S.session == nil or (S.lastGameTime and now < S.lastGameTime) then
			newSession(now)
		end
		local paused = S.lastGameTime ~= nil and now == S.lastGameTime
		S.lastGameTime = now

		-- zmiany sieci zgłoszone przez handleEvent
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

		if not paused and due(S.lastDynamicTime, now, cfg.dynamicEveryDays, util.dayLength())
			and (S.lastDynamicWall == nil or wall - S.lastDynamicWall >= cfg.minDynamicWallSec) then
			S.lastDynamicTime = now
			S.lastDynamicWall = wall
			exportDynamic(now)
		end
	end,
}

-- Pliki .script.lua w TF3 muszą definiować globalne data() zwracające tabelę funkcji
-- (wzór: base/base_mod.script.lua). Styl "return {...}" działa tylko w plikach .tl.
function data()
	return script
end
