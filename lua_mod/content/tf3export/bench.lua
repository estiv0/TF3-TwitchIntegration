-- Jednorazowy pomiar formatu zapisu (docs/export-format-performance.md).
-- Porównuje na tych samych danych sieci:
--   A) json.encode w Lua + app.saveUserdata({ json = <string> })   (obecny format)
--   B) app.saveUserdata({ fields, edges = <tabele Lua> })           (serializacja w C++ gry)
-- Wynik: linie "[tf3export] bench ..." w logu oraz pliki bench_A/bench_B w local\tf3export\
-- (middle app ma je ignorować; do usunięcia ręcznie po pomiarze).
-- Uruchamia się raz na sesję w wątku GUI; jedna klatka będzie długa (~kilkaset ms).

local json = ug_require "json.lua"
local util = ug_require "util.lua"
local collectors = ug_require "collectors.lua"

local B = { done = false }

local function ms(t) return t * 1000 end

function B.run(ids)
	if B.done then return end
	B.done = true
	if not (type(app) == "table" or type(app) == "userdata") or not app.saveUserdata then
		util.log("bench: app.saveUserdata unavailable, skipped")
		return
	end

	local dir = "tf3export"

	-- zbieranie surowych wierszy (wspólny koszt obu wariantów)
	local t0 = util.clock()
	local rows = {}
	for _, id in ipairs(ids) do
		local row = collectors.edge(id)
		if row then rows[#rows + 1] = row end
	end
	local t1 = util.clock()

	-- A: JSON w Lua
	local parts = {}
	for i, row in ipairs(rows) do parts[i] = json.encode(row) end
	local content = '{"fields":' .. json.encode(collectors.EDGE_FIELDS) .. ',"edges":[' .. table.concat(parts, ",") .. "]}"
	local t2 = util.clock()
	local okA, errA = pcall(function() app.saveUserdata(dir, "bench_A", { json = content }) end)
	local t3 = util.clock()

	-- B: tabela Lua, serializacja po stronie gry
	local okB, errB = pcall(function()
		app.saveUserdata(dir, "bench_B", { fields = collectors.EDGE_FIELDS, edges = rows })
	end)
	local t4 = util.clock()

	-- B2: to samo z liczbami zaokrąglonymi do 0.1 m / 0.01 (wariant "mniejsza precyzja")
	local rounded = {}
	for i, row in ipairs(rows) do
		local r = {}
		for k, v in ipairs(row) do
			if type(v) == "number" and v ~= math.floor(v) then
				r[k] = math.floor(v * 10 + 0.5) / 10
			else
				r[k] = v
			end
		end
		rounded[i] = r
	end
	local t5 = util.clock()
	local okB2, errB2 = pcall(function()
		app.saveUserdata(dir, "bench_B2", { fields = collectors.EDGE_FIELDS, edges = rounded })
	end)
	local t6 = util.clock()

	util.log(string.format("bench: %d edges, collect %.1f ms", #rows, ms(t1 - t0)))
	util.log(string.format("bench A (Lua JSON + saveUserdata string): encode %.1f ms, save %.1f ms, json %d B, ok=%s %s",
		ms(t2 - t1), ms(t3 - t2), #content, tostring(okA), okA and "" or tostring(errA)))
	util.log(string.format("bench B (saveUserdata table): save %.1f ms, ok=%s %s",
		ms(t4 - t3), tostring(okB), okB and "" or tostring(errB)))
	util.log(string.format("bench B2 (rounded 0.1, table): round %.1f ms, save %.1f ms, ok=%s %s",
		ms(t5 - t4), ms(t6 - t5), tostring(okB2), okB2 and "" or tostring(errB2)))
end

return B
