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
--   * guiUpdate (GUI): eksport dynamiczny porcjami (dynamicBudgetMs na klatkę); sieć przebudowywana porcjami
--     po zmianie netVersion (z debounce) i zapisywana jako pełny zrzut (raz na sesję)
--     albo różnica względem poprzedniej wersji; teren (terrain.lua) porcjami po sieci
--     i ponownie po zmianie rewizji kafli terenu.

local json = ug_require "json.lua"
local util = ug_require "util.lua"
local collectors = ug_require "collectors.lua"
local writer = ug_require "writer.lua"
local bench = ug_require "bench.lua"
local terrain = ug_require "terrain.lua"

local cfg = {
	dynamicEveryDays = 2,          -- eksport miast, przemysłu, stacji, linii i pojazdów (czas gry)...
	minDynamicWallSec = 10,        -- ...ale nie częściej niż co tyle sekund czasu rzeczywistego
	dynamicBudgetMs = 3,           -- czas pracy nad eksportem dynamicznym na klatkę (pojedynczy kolektor,
	                               -- np. miasta, nie jest dzielony - czasy kroków są w logu "dynamic #N")
	networkDebounceSec = 2,        -- po zdarzeniu budowy czekamy, aż gracz skończy klikać
	networkSafetyEveryWallSec = 300, -- pełne porównanie sieci także bez zdarzeń (zabezpieczenie)
	networkBudgetMs = 2,           -- czas pracy nad siecią na klatkę
	formatVersion = 5,
	financeYears = 5,              -- kolumny tabeli finansów (lata, jak w oknie Finanse)
	historyMaxPoints = 300,        -- najwyżej tyle punktów na serię historii (saldo, wartość firmy, mieszkańcy)
	vehicles = true,               -- pojazdy (lista, pozycje, liczba na linii); false = bez pojazdów w eksporcie
	vehiclePositions = true,       -- pozycja i prędkość pojazdów (płynny ruch na stronie); false = wyłączone
	vehiclePositionsMaxMs = 30,    -- pozycje liczone porcjami w budżecie klatki; jeśli łącznie trwają dłużej
	                               -- (średnio z 3 eksportów), wyłączamy je do końca sesji (log: "vehicle positions")
	benchmark = false,             -- jednorazowy pomiar formatu zapisu (bench.lua), wyniki w docs
	terrainStepM = 20,             -- odstęp próbek heightmapy (m)
	terrainMaxSide = 2048,         -- najwyżej tyle próbek na bok (większa mapa -> większy krok);
	                               -- mapa 20x41 km -> krok 21 m, 976 x 1951 (~2 mln próbek, ~4 s CPU w porcjach)
	terrainBudgetMs = 4,           -- czas pracy nad terenem na klatkę
	terrainCheckEveryWallSec = 30, -- jak często sprawdzać rewizje kafli terenu (terraformowanie)
	terrainBench = false,          -- jednorazowy pomiar metod odczytu terenu (terrain.lua); zacina grę na ~6 s, wyniki w docs
	terrain = true,                -- eksport terenu; false = wyłączony
	settingsEveryWallSec = 5,      -- jak często czytać ustawienia z Tf3Bridge (patrz SETTINGS)
}

-- Ustawienia z okna "Ustawienia moda" w Tf3Bridge: local\tf3export_settings\settings.lua
-- (format jak z app.saveUserdata). Czytane przez app.loadUserdata co settingsEveryWallSec, więc
-- zmiany działają bez restartu gry. Klucz -> { typ, min, max }; zakresy jak w ModSettings.cs.
-- Brak pliku albo złej wartości = domyślna z cfg powyżej.
local SETTINGS_DIR, SETTINGS_FILE = "tf3export_settings", "settings"
local SETTINGS = {
	dynamicEveryDays = { "number", 1, 30 },
	minDynamicWallSec = { "number", 2, 300 },
	dynamicBudgetMs = { "number", 1, 10 },
	vehicles = { "boolean" },
	terrain = { "boolean" },
	terrainStepM = { "number", 5, 100 },
}
local DEFAULTS = {}
for key in pairs(SETTINGS) do DEFAULTS[key] = cfg[key] end

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
	seqTerrain = 0,
	terrain = nil,           -- bieżące zadanie eksportu terenu
	terrainSig = nil,        -- podpis rewizji terenu z ostatniego eksportu
	lastTerrainCheckWall = nil,
	lineStats = {},          -- id linii -> statystyki (collectors.lineStats), odświeżane co miesiąc gry
	lineStatsKey = nil,      -- miesiąc kalendarza, z którego pochodzi cache
	prevYear = nil,          -- { year, from, to } poprzedniego roku kalendarzowego albo nil
	vehiclePos = true,       -- pozycje pojazdów w tej sesji (wyłączane po przekroczeniu budżetu)
	vehiclePosMs = {},       -- czasy zbierania pozycji z ostatnich eksportów (ms)
	dyn = nil,               -- bieżące zadanie eksportu dynamicznego
	company = nil,           -- finanse i historia (collectors.financeTable / worldHistory), co miesiąc gry
	companyKey = nil,        -- miesiąc, z którego pochodzi company
	settingsWall = nil,      -- kiedy ostatnio czytaliśmy ustawienia (nie zależy od sesji)
}
local terrainBenchDone = false -- raz na uruchomienie gry, nie na sesję

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
	S.vehiclePos, S.vehiclePosMs, S.dyn = cfg.vehiclePositions, {}, nil
	S.company, S.companyKey = nil, nil
	util.log(string.format("new session %s (writer mode '%s')", S.session, writer.mode()))
end

---------------------------------------------------------------- ustawienia
-- Czy plik ustawień istnieje? app.loadUserdata na brakującym pliku zgłasza błąd gry
-- ("Failed to load userdata", z oknem błędu) mimo pcall, więc najpierw sprawdzamy listę plików.
-- getAllUserdata zwraca nazwy; na wszelki wypadek akceptujemy też wariant z ".lua".
local function settingsFileExists()
	local ok, names = pcall(function() return app.getAllUserdata(SETTINGS_DIR) end)
	if not ok then return false end
	for _, name in ipairs(util.list(names)) do
		if name == SETTINGS_FILE or name == SETTINGS_FILE .. ".lua" then return true end
	end
	return false
end

-- Wczytuje SETTINGS do cfg; zwraca listę zmienionych kluczy ("klucz=wartość").
-- Brak pliku = wartości domyślne; nieudany odczyt (np. plik w trakcie zapisu) = bez zmian.
local function loadSettings()
	local t = {}
	if settingsFileExists() then
		local ok, loaded = pcall(function() return app.loadUserdata(SETTINGS_DIR, SETTINGS_FILE) end)
		if not ok or type(loaded) ~= "table" then return {} end
		t = loaded
	end
	local changed = {}
	for key, spec in pairs(SETTINGS) do
		local v = t[key]
		if type(v) ~= spec[1] or v ~= v then -- zły typ albo NaN
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
			S.vehiclePos, S.vehiclePosMs = cfg.vehiclePositions, {} -- po włączeniu pozycje od nowa
		elseif key == "terrain" or key == "terrainStepM" then
			S.terrain, S.terrainSig = nil, nil -- przerwij bieżący eksport; przy włączonym zacznie się od nowa
		end
	end
end

---------------------------------------------------------------- statystyki linii
-- Finanse i przewozy zmieniają się wolno, więc liczymy je raz na miesiąc kalendarza gry
-- (nowa linia dostaje statystyki od razu), a w pozostałych eksportach wysyłamy cache.
local function lineStatsMonth(now)
	local d = util.date(now)
	local key = d and d.year and d.month and (d.year * 12 + d.month)
		or math.floor(now / (30 * util.dayLength())) -- bez kalendarza: co ~30 dni
	if key ~= S.lineStatsKey then
		S.lineStatsKey, S.lineStats, S.prevYear = key, {}, nil
		local okY, ys = pcall(util.yearStart, now)
		if okY and ys and ys > 0 then
			local okP, ps = pcall(util.yearStart, ys - 1)
			if okP and ps then S.prevYear = { year = d.year - 1, from = ps, to = ys } end
		end
	end
end

-- Statystyki jednej linii (z cache albo policzone); true = liczone teraz.
local function attachLineStats(l, now)
	local fresh = S.lineStats[l.id] == nil
	if fresh then
		local ok, st = pcall(collectors.lineStats, l.id, now, S.prevYear)
		S.lineStats[l.id] = ok and st or false
		if not ok then util.log("lineStats failed for " .. tostring(l.id) .. ": " .. tostring(st)) end
	end
	l.stats = S.lineStats[l.id] or nil
	return fresh
end

---------------------------------------------------------------- pozycje pojazdów
-- Średnio z 3 ostatnich eksportów dłużej niż vehiclePositionsMaxMs -> wyłączamy je do końca sesji
-- (strona wraca do zgadywania z przystanków).
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

---------------------------------------------------------------- eksport dynamiczny
-- Rozłożony na klatki tak jak sieć: zadanie S.dyn przechodzi po kolei kroki z DYN_STEPS, najwyżej
-- dynamicBudgetMs pracy na klatkę. Pętle (pozycje pojazdów, statystyki linii, kodowanie JSON
-- element po elemencie) przerywamy w połowie; pojedynczy kolektor (np. miasta) idzie w całości.
-- Snapshot powstaje przez kilka-kilkanaście klatek (ułamek sekundy czasu gry) - dla strony bez znaczenia.
-- Krok: function(job, deadline) -> true, gdy skończony; job.i to licznik pętli kroku (od 1 w każdym kroku).

local DYN_ARRAYS = { "towns", "industries", "stations", "lines", "vehicles" }
local CHECK_EVERY = 20 -- co tyle elementów pętli sprawdzamy zegar

-- Następny element pętli; true = koniec budżetu na tę klatkę.
local function nextItem(job, deadline)
	job.i = job.i + 1
	return job.i % CHECK_EVERY == 0 and util.clock() >= deadline
end

local DYN_STEPS = {
	{ "vehicles", function(job)
		-- bez pojazdów snap.vehicles zostaje nil: brak klucza w JSON i vehicleCount linii (strona pokazuje "–")
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
	{ "stations", function(job)
		-- stacje to nowe, niesprawdzone API: błąd nie może zablokować reszty eksportu
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
			-- nowe API: błąd nie blokuje eksportu
			local ok, fresh = pcall(attachLineStats, lines[job.i], job.now)
			job.i = job.i + 1
			if ok and fresh then
				job.statsCount = job.statsCount + 1
				if util.clock() >= deadline then return false end -- liczenie jest drogie, cache nie
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
	{ "player", function(job)
		job.snap.player = collectors.player()
		return true
	end },
	-- Firma: tabela finansów (lata) i historia z logbooka świata. Zmienia się wolno, a liczenie
	-- (cała historia księgowa) może być drogie, więc raz na miesiąc gry (klucz z kroku "lines").
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
		}):sub(1, -2) -- bez zamykającej klamry
		local out = { head }
		for _, key in ipairs(DYN_ARRAYS) do
			if job.snap[key] ~= nil then -- pomijamy wyłączone (pojazdy)
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
		.. "vehicles %.0f, positions %.0f (%d vehicles), stations %.0f, lines %.0f, line stats %.0f (%d lines), "
		.. "towns %.0f, industries %.0f, player %.0f, company %.0f%s, encode %.0f, write %.0f ms; %d B",
		job.seq, job.work * 1000, job.frames, job.maxFrame * 1000, util.clock() - job.t0,
		ms("vehicles"), ms("positions"), nVehicles, ms("stations"), ms("lines"), ms("lineStats"), job.statsCount,
		ms("towns"), ms("industries"), ms("player"), ms("company"), job.companyFresh and " (refreshed)" or "",
		ms("encode"), ms("write"), #(job.content or "")))
end

local function stepDynamic()
	local job = S.dyn
	local start = util.clock()
	local deadline = start + cfg.dynamicBudgetMs / 1000
	repeat -- co najmniej jeden krok na klatkę
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

---------------------------------------------------------------- eksport terenu
-- Heightmapa porcjami (terrainBudgetMs na klatkę), tylko gdy nie trwa praca nad siecią.
-- Ponownie po zmianie rewizji kafli terenu (sprawdzane co terrainCheckEveryWallSec).
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
	elseif S.net == nil and S.rows ~= nil then -- najpierw sieć
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

		if S.settingsWall == nil or wall - S.settingsWall >= cfg.settingsEveryWallSec then
			S.settingsWall = wall
			applySettings()
		end

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

		-- teren to nowe, niesprawdzone API: błąd wyłącza go na tę sesję zamiast psuć guiUpdate
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

-- Pliki .script.lua w TF3 muszą definiować globalne data() zwracające tabelę funkcji
-- (wzór: base/base_mod.script.lua). Styl "return {...}" działa tylko w plikach .tl.
function data()
	return script
end
