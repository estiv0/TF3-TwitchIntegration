-- Zapis snapshotów. Wyniki sondy w grze (2026-10-02):
--   wątki symulacji (update):  brak io, os.rename i app -> zostaje tylko log
--   wątek GUI (guiUpdate):     app.saveUserdata działa
--
-- Tryb "userdata" (wątek GUI):
--   app.saveUserdata("tf3export", "<kind>_<session>_<seq>", { json = "<JSON>" })
--   -> ...\3493540\local\tf3export\<kind>_<session>_<seq>.lua o treści:
--        function data()
--        return {
--            json = "<JSON jako literał stringa Lua>",
--        }
--        end
--   Middle app wyciąga literał json = "..." i odwraca escapowanie Lua (\" \\ \n \r \t \ddd).
--   Nie wiadomo, czy zapis jest atomowy - middle app ponawia odczyt, jeśli plik jest niekompletny.
--
-- Tryb "log" (zapas, np. gdy app jest niedostępne):
--   [tf3x] <kind> <session> <seq> <part>/<total> <fragment JSON-a>  w crash_dump\stdout.txt

local util = ug_require "util.lua"

local W = {}

W.userdataDir = "tf3export"
W.chunkSize = 8000

function W.mode()
	if type(app) == "table" or type(app) == "userdata" then
		local ok, fn = pcall(function() return app.saveUserdata end)
		if ok and fn then return "userdata" end
	end
	return "log"
end

-- Dzieli tekst na kawałki <= size bajtów, nie przecinając znaków UTF-8.
local function utf8Chunks(content, size)
	local chunks, from, n = {}, 1, #content
	while from <= n do
		local to = math.min(from + size - 1, n)
		-- cofnij koniec, jeśli następny bajt to kontynuacja znaku (10xxxxxx)
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
	local name = string.format("%s_%s_%06d", kind, session, seq)
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
