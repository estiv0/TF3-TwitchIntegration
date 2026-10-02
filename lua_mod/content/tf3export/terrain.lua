-- Eksport kształtu terenu (heightmapa) dla tła mapy na stronie.
--
-- API (api/tealdef/api/engine.d.tl, UtilTerrain):
--   api.engine.terrain.getBaseHeightAt(Vec2f)   wysokość z siatki 4 m, bez wyrównań pod tory/drogi
--   api.engine.terrain.getBoundingBox()         zakres świata (Box2f, min/max)
--   api.engine.terrain.makeMapFromGame(true, ...) GameMap z pełną tablicą heightmap
--   api.engine.terrain.getTerrainEntityRevisions() rewizje kafli -> wykrywanie terraformowania
--   app.writeHeightmap(map, name, stl, 16bit, biomes) natywny zapis obrazka (jak w edytorze map)
--
-- Eksport (metoda A): próbkowanie getBaseHeightAt co stepM metrów, porcjami w kolejnych
-- klatkach (budgetMs), potem kodowanie też porcjami i zapis jednego pliku "terrain".
--
-- Format "terrain" (JSON):
--   { v, kind="terrain", session, seq, gameTime, method="baseHeight",
--     nx, ny, step, x0, y0,        -- próbka (i, j) leży w świecie w (x0 + i*step, y0 + j*step)
--     zMin, zMax, zScale,          -- h = zMin + q * zScale
--     waterLevel,                  -- może być null
--     encoding="b64x3", data }     -- wiersze od j=0 (południe, min y), w wierszu i rosnąco;
--                                     każda próbka q (0..262143) = 3 znaki base64, najstarsze bity pierwsze
--
-- Benchmark (T.bench): jednorazowy pomiar wszystkich trzech metod, wyniki w logu "[tf3export] terrain bench ...".

local json = ug_require "json.lua"
local util = ug_require "util.lua"

local T = {}

local B64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"
local B64C = {}
for i = 1, 64 do B64C[i - 1] = B64:sub(i, i) end

local Q_MAX = 262143 -- 18 bitów = 3 znaki base64
local Z_SCALE = 0.1  -- 10 cm na jednostkę

local function terrainApi()
	local ok, t = pcall(function() return api.engine.terrain end)
	if ok then return t end
	return nil
end

local function vec2(x, y)
	return api.type.Vec2f.new(x, y)
end

function T.boundingBox()
	local t = terrainApi()
	if not t then return nil end
	local ok, bb = pcall(t.getBoundingBox)
	if not ok or bb == nil then
		util.log("terrain: getBoundingBox failed: " .. tostring(bb))
		return nil
	end
	local mn, mx = util.field(bb, "min"), util.field(bb, "max")
	local box = {
		minX = util.field(mn, "x"), minY = util.field(mn, "y"),
		maxX = util.field(mx, "x"), maxY = util.field(mx, "y"),
	}
	for _, v in pairs(box) do
		if type(v) ~= "number" then return nil end
	end
	return box
end

-- Poziom żeglownej wody: komponent TERRAIN encji świata (Engine.Component.Terrain.waterLevel).
function T.waterLevel()
	local c = util.comp(api.engine.util.getWorld(), util.ct.TERRAIN)
	local w = c and util.field(c, "waterLevel")
	if type(w) == "number" then return w end
	return nil
end

-- Podpis rewizji kafli terenu: liczba kafli + największa rewizja. Rewizja (Engine.Revision.num)
-- to rosnąca trójka (tick, komenda, indeks), więc każda zmiana kafla podnosi maksimum.
-- (Wersja z sumą/haszem padała na string.format "%d" - wartości poza zakresem int.)
function T.revisionSignature()
	local t = terrainApi()
	if not t then return nil end
	local ok, revs = pcall(t.getTerrainEntityRevisions)
	if not ok or revs == nil then return nil end
	local count, best = 0, { -1, -1, -1 }
	for _, rev in util.pairs(revs) do
		count = count + 1
		local num = util.list(util.field(rev, "num"))
		local cur = { tonumber(num[1]) or 0, tonumber(num[2]) or 0, tonumber(num[3]) or 0 }
		for k = 1, 3 do
			if cur[k] ~= best[k] then
				if cur[k] > best[k] then best = cur end
				break
			end
		end
	end
	return tostring(count) .. ":" .. tostring(best[1]) .. "." .. tostring(best[2]) .. "." .. tostring(best[3])
end

---------------------------------------------------------------- zadanie eksportu
-- Tworzy zadanie; nil, jeśli API terenu jest niedostępne.
function T.start(opts)
	local box = T.boundingBox()
	if not box then return nil end
	local step = opts.stepM
	-- limit rozmiaru: najwyżej maxSide próbek na bok
	local side = math.max(box.maxX - box.minX, box.maxY - box.minY)
	if side / step + 1 > opts.maxSide then
		step = math.ceil(side / (opts.maxSide - 1))
	end
	local nx = math.floor((box.maxX - box.minX) / step) + 1
	local ny = math.floor((box.maxY - box.minY) / step) + 1
	return {
		phase = "sample",
		box = box, step = step, nx = nx, ny = ny,
		x0 = box.minX, y0 = box.minY,
		h = {}, j = 0, zMin = math.huge, zMax = -math.huge,
		rows = {}, ej = 0,
		t0 = util.clock(), sampleWork = 0, encodeWork = 0, calls = 0,
		revSig = T.revisionSignature(),
		gameTime = opts.gameTime, reason = opts.reason,
	}
end

local function sampleRow(job, t)
	local h, nx, step = job.h, job.nx, job.step
	local y = job.y0 + job.j * step
	local base = job.j * nx
	local zMin, zMax = job.zMin, job.zMax
	local get = t.getBaseHeightAt
	for i = 0, nx - 1 do
		local z = get(vec2(job.x0 + i * step, y))
		h[base + i + 1] = z
		if z < zMin then zMin = z end
		if z > zMax then zMax = z end
	end
	job.zMin, job.zMax = zMin, zMax
	job.calls = job.calls + nx
	job.j = job.j + 1
end

local function encodeRow(job)
	local h, nx, zMin = job.h, job.nx, job.zMin
	local base = job.ej * nx
	local out = {}
	for i = 1, nx do
		local q = math.floor((h[base + i] - zMin) / Z_SCALE + 0.5)
		if q < 0 then q = 0 elseif q > Q_MAX then q = Q_MAX end
		local a = math.floor(q / 4096)
		local r = q - a * 4096
		local b = math.floor(r / 64)
		out[i] = B64C[a] .. B64C[b] .. B64C[r - b * 64]
	end
	job.rows[job.ej + 1] = table.concat(out)
	job.ej = job.ej + 1
end

-- Jedna porcja pracy. Zwraca true, gdy zadanie jest gotowe do zapisu (T.content).
function T.step(job, budgetMs)
	local t = terrainApi()
	local start = util.clock()
	local deadline = start + budgetMs / 1000
	if job.phase == "sample" then
		while job.j < job.ny do
			sampleRow(job, t)
			if util.clock() >= deadline then break end
		end
		job.sampleWork = job.sampleWork + (util.clock() - start)
		if job.j >= job.ny then job.phase = "encode" end
		return false
	end
	while job.ej < job.ny do
		encodeRow(job)
		if util.clock() >= deadline then break end
	end
	job.encodeWork = job.encodeWork + (util.clock() - start)
	return job.ej >= job.ny
end

function T.content(job, header)
	local zMax = job.zMin + math.min(job.zMax - job.zMin, Q_MAX * Z_SCALE)
	header.method = "baseHeight"
	header.nx, header.ny, header.step = job.nx, job.ny, job.step
	header.x0, header.y0 = util.round(job.x0), util.round(job.y0)
	header.zMin, header.zMax, header.zScale = util.round(job.zMin), util.round(zMax), Z_SCALE
	header.waterLevel = T.waterLevel()
	header.encoding = "b64x3"
	return json.encode(header):sub(1, -2) .. ',"data":"' .. table.concat(job.rows) .. '"}'
end

---------------------------------------------------------------- benchmark
-- Jednorazowo, w jednej klatce (zatnie grę na chwilę). Porównuje:
--   A) getBaseHeightAt w pętli (n próbek; czas przeliczany na pełną siatkę),
--   B) makeMapFromGame(true,...) + odczyt tablicy heightmap z Lua,
--   C) makeMapFromGame + app.writeHeightmap (natywny obrazek 16-bit w userdata\heightmaps).
-- Dodatkowo: koszt getTerrainEntityRevisions i porównanie wartości A vs B w kilku punktach.
local function ms(t) return t * 1000 end

local function try(label, fn)
	local ok, err = pcall(fn)
	if not ok then util.log("terrain bench " .. label .. " failed: " .. tostring(err)) end
	return ok
end

function T.bench(opts)
	local t = terrainApi()
	if not t then
		util.log("terrain bench: api.engine.terrain unavailable")
		return
	end
	local box = T.boundingBox()
	if not box then return end
	local w, d = box.maxX - box.minX, box.maxY - box.minY
	util.log(string.format("terrain bench: bbox x %.0f..%.0f, y %.0f..%.0f (%.0f x %.0f m), waterLevel %s",
		box.minX, box.maxX, box.minY, box.maxY, w, d, tostring(T.waterLevel())))

	-- rewizje
	try("revisions", function()
		local c = util.clock()
		local sig = T.revisionSignature()
		util.log(string.format("terrain bench revisions: %.2f ms, signature %s", ms(util.clock() - c), tostring(sig)))
	end)

	-- A: getBaseHeightAt i getHeightAt
	local n = 20000
	try("A", function()
		local c = util.clock()
		for k = 0, n - 1 do
			t.getBaseHeightAt(vec2(box.minX + (k * 37.3) % w, box.minY + (k * 91.7) % d))
		end
		local base = util.clock() - c
		c = util.clock()
		for k = 0, n - 1 do
			t.getHeightAt(vec2(box.minX + (k * 37.3) % w, box.minY + (k * 91.7) % d))
		end
		local full = util.clock() - c
		local per = base / n
		local grid = function(step) return (math.floor(w / step) + 1) * (math.floor(d / step) + 1) end
		util.log(string.format("terrain bench A: getBaseHeightAt %.2f us/call, getHeightAt %.2f us/call (%d calls)",
			per * 1e6, full / n * 1e6, n))
		for _, step in ipairs({ 64, 32, 16, 8 }) do
			util.log(string.format("terrain bench A: step %d m -> %d samples, ~%.0f ms total (%.0f frames at %d ms)",
				step, grid(step), ms(grid(step) * per), grid(step) * per * 1000 / opts.budgetMs, opts.budgetMs))
		end
	end)

	-- B: makeMapFromGame
	local map
	try("B", function()
		local c = util.clock()
		map = t.makeMapFromGame(true, false, false, false)
		local tMake = util.clock() - c
		c = util.clock()
		local hm = map.heightmap
		local tField = util.clock() - c
		c = util.clock()
		local len = #hm
		local tLen = util.clock() - c
		local res = map.resolution
		util.log(string.format("terrain bench B: makeMapFromGame %.0f ms, .heightmap %.0f ms (%s), #heightmap %d (%.2f ms)",
			ms(tMake), ms(tField), type(hm), len, ms(tLen)))
		util.log(string.format("terrain bench B: numTiles %s x %s, levels %s, offsetZ %s, waterLevel %s, resolution %s,%s,%s",
			tostring(map.numTilesX), tostring(map.numTilesY), tostring(map.levels), tostring(map.offsetZ),
			tostring(map.waterLevel), tostring(res and res.x), tostring(res and res.y), tostring(res and res.z)))

		-- odczyt co 8. próbki (32 m przy siatce 4 m) - realny koszt zmniejszania w Lua
		c = util.clock()
		local s = 0
		for k = 1, len, 8 do s = s + (hm[k] or 0) end
		util.log(string.format("terrain bench B: read every 8th of %d values %.0f ms", len, ms(util.clock() - c)))

		-- układ tablicy: szerokość = numTilesX * 256 / res.x (+1?); porównanie z getBaseHeightAt
		local rx = (res and res.x) or 4
		for _, extra in ipairs({ 0, 1 }) do
			local cols = math.floor((map.numTilesX or 0) * 256 / rx + 0.5) + extra
			local rowsN = math.floor((map.numTilesY or 0) * 256 / rx + 0.5) + extra
			if cols * rowsN == len then
				util.log(string.format("terrain bench B: layout %d x %d (+%d) matches", cols, rowsN, extra))
				for _, f in ipairs({ { 0.25, 0.25 }, { 0.5, 0.5 }, { 0.75, 0.3 }, { 0.1, 0.9 } }) do
					local ci, cj = math.floor(f[1] * (cols - 1)), math.floor(f[2] * (rowsN - 1))
					local raw = hm[cj * cols + ci + 1]
					local wx = box.minX + ci / (cols - 1) * w
					local wy = box.minY + cj / (rowsN - 1) * d
					util.log(string.format("terrain bench B: [%d,%d] raw %s vs getBaseHeightAt(%.0f,%.0f) %.2f",
						ci, cj, tostring(raw), wx, wy, t.getBaseHeightAt(vec2(wx, wy))))
				end
			end
		end
	end)

	-- C: natywny zapis obrazka
	if map and (type(app) == "table" or type(app) == "userdata") then
		try("C", function()
			local c = util.clock()
			local ok = app.writeHeightmap(map, "tf3export_bench", false, true, false)
			util.log(string.format("terrain bench C: writeHeightmap %.0f ms, ok=%s", ms(util.clock() - c), tostring(ok)))
			local list = app.getAllHeightmaps()
			util.log("terrain bench C: userdata " .. tostring(app.getUserDataFolder())
				.. ", heightmaps: " .. table.concat(util.list(list), ", "))
		end)
	end
end

return T
