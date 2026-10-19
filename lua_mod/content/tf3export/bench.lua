-- One-off measurement of the write format (docs/export-format-performance.md).
-- Compares on the same network data:
--   A) json.encode in Lua + app.saveUserdata({ json = <string> })   (current format)
--   B) app.saveUserdata({ fields, edges = <Lua tables> })           (serialization in the game's C++)
-- Result: "[tf3export] bench ..." lines in the log and tf3export_bench_A/_B files in local\towns_industries\
-- (the middle app should ignore them; delete them by hand after the measurement).
-- Runs once per session on the GUI thread; one frame will be long (~a few hundred ms).

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

	local dir = util.USERDATA_DIR

	-- collecting raw rows (cost shared by both variants)
	local t0 = util.clock()
	local rows = {}
	for _, id in ipairs(ids) do
		local row = collectors.edge(id)
		if row then rows[#rows + 1] = row end
	end
	local t1 = util.clock()

	-- A: JSON in Lua
	local parts = {}
	for i, row in ipairs(rows) do parts[i] = json.encode(row) end
	local content = '{"fields":' .. json.encode(collectors.EDGE_FIELDS) .. ',"edges":[' .. table.concat(parts, ",") .. "]}"
	local t2 = util.clock()
	local okA, errA = pcall(function() app.saveUserdata(dir, util.FILE_PREFIX .. "bench_A", { json = content }) end)
	local t3 = util.clock()

	-- B: a Lua table, serialized on the game's side
	local okB, errB = pcall(function()
		app.saveUserdata(dir, util.FILE_PREFIX .. "bench_B", { fields = collectors.EDGE_FIELDS, edges = rows })
	end)
	local t4 = util.clock()

	-- B2: the same with numbers rounded to 0.1 m / 0.01 (the "lower precision" variant)
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
		app.saveUserdata(dir, util.FILE_PREFIX .. "bench_B2", { fields = collectors.EDGE_FIELDS, edges = rounded })
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
