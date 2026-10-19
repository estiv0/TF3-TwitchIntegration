-- Writing snapshots. Probe results in the game (2026-10-02):
--   simulation threads (update):  no io, os.rename or app -> only the log is left
--   GUI thread (guiUpdate):       app.saveUserdata works
--
-- "userdata" mode (GUI thread):
--   app.saveUserdata("towns_industries", "tf3export_<kind>_<session>_<seq>", { json = "<JSON>" })
--   -> ...\3493540\local\towns_industries\tf3export_<kind>_<session>_<seq>.lua with the content:
--        function data()
--        return {
--            json = "<JSON as a Lua string literal>",
--        }
--        end
--   The middle app extracts the json = "..." literal and reverses Lua escaping (\" \\ \n \r \t \ddd).
--   It's unknown whether the write is atomic - the middle app retries the read if the file is incomplete.
--
-- "log" mode (fallback, e.g. when app is unavailable):
--   [tf3x] <kind> <session> <seq> <part>/<total> <JSON fragment>  in crash_dump\stdout.txt

local util = ug_require "util.lua"

local W = {}

W.userdataDir = util.USERDATA_DIR
W.chunkSize = 8000

function W.mode()
	if type(app) == "table" or type(app) == "userdata" then
		local ok, fn = pcall(function() return app.saveUserdata end)
		if ok and fn then return "userdata" end
	end
	return "log"
end

-- Splits text into chunks of <= size bytes without splitting UTF-8 characters.
local function utf8Chunks(content, size)
	local chunks, from, n = {}, 1, #content
	while from <= n do
		local to = math.min(from + size - 1, n)
		-- step the end back if the next byte is a character continuation (10xxxxxx)
		while to < n and to > from do
			local b = content:byte(to + 1)
			if b < 0x80 or b >= 0xC0 then break end
			to = to - 1
		end
		chunks[#chunks + 1] = content:sub(from, to)
		from = to + 1
	end
	if #chunks == 0 then chunks[1] = "" end
	return chunks
end

local function writeLog(kind, session, seq, content)
	local chunks = utf8Chunks(content, W.chunkSize)
	for part, chunk in ipairs(chunks) do
		util.print(string.format("[tf3x] %s %s %d %d/%d %s",
			kind, session, seq, part, #chunks, chunk))
	end
	return true
end

local function writeUserdata(kind, session, seq, content)
	local name = string.format("%s%s_%s_%06d", util.FILE_PREFIX, kind, session, seq)
	local ok, err = pcall(function()
		app.saveUserdata(W.userdataDir, name, { json = content })
	end)
	if not ok then
		util.log("saveUserdata failed: " .. tostring(err))
	end
	return ok
end

function W.write(kind, session, seq, content)
	if W.mode() == "userdata" and writeUserdata(kind, session, seq, content) then
		return true
	end
	return writeLog(kind, session, seq, content)
end

return W
