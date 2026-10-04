-- Komendy od Tf3Bridge (akcje widzów z Twitcha: czat, punkty kanału, przycisk testowy).
--
--   Tf3Bridge -> local\tf3export_cmd\queue.lua    (format app.saveUserdata, pisany przez Tf3Bridge)
--     { commands = { { id = "...", kind = "setName", created = <unix s>, entity = 123, name = "..." }, ... } }
--   mod      -> local\tf3export_cmd\results.lua  (app.saveUserdata)
--     { json = '{"results":[{"id":"...","status":"ok|failed|invalid|expired","error":"..."}]}',
--       done = "id1\nid2\n..." }
--
-- Tylko wątek GUI (app.loadUserdata/saveUserdata). Tf3Bridge usuwa komendę z kolejki, gdy zobaczy
-- jej wynik; do tego czasu mod pomija ją po id (zbiór done, odtwarzany z results.lua po restarcie gry).
--
-- Gra wysyła komendy (api.cmd.sendCommand) ze swoich skryptów w wątkach symulacji; z wątku GUI
-- jeszcze tego nie sprawdzaliśmy. Dlatego wynik nie opiera się tylko na callbacku: po wysłaniu
-- sprawdzamy w kolejnych odczytach, czy zmiana jest widoczna (np. nowa nazwa), z limitem czasu.

local json = ug_require "json.lua"
local util = ug_require "util.lua"

local M = {}

local DIR, QUEUE, RESULTS = "tf3export_cmd", "queue", "results"
local MAX_RESULTS = 100   -- tyle ostatnich wyników trzymamy w results.lua
local MAX_AGE_SEC = 300   -- starszych komend nie wykonujemy (np. kolejka sprzed restartu gry)
local VERIFY_SEC = 15     -- tyle czekamy, aż efekt komendy będzie widoczny

local done = nil          -- id -> true; nil = jeszcze nie wczytane z results.lua
local results = {}        -- { id, status, error } (najnowsze na końcu)
local pending = {}        -- id -> { cmd, sent, verify, callback }

local function save()
	local ids = {}
	for i, r in ipairs(results) do ids[i] = r.id end
	local ok, err = pcall(function()
		app.saveUserdata(DIR, RESULTS, { json = json.encode({ results = results }), done = table.concat(ids, "\n") })
	end)
	if not ok then util.log("commands: saving results failed: " .. tostring(err)) end
end

local function finish(id, status, err)
	pending[id] = nil
	done[id] = true
	results[#results + 1] = { id = id, status = status, error = err }
	while #results > MAX_RESULTS do table.remove(results, 1) end
	util.log(string.format("command %s: %s%s", id, status, err and (" (" .. err .. ")") or ""))
	save()
end

local function loadDone()
	done = {}
	if not util.userdataExists(DIR, RESULTS) then return end
	local ok, t = pcall(function() return app.loadUserdata(DIR, RESULTS) end)
	if not ok or type(t) ~= "table" or type(t.done) ~= "string" then return end
	for id in t.done:gmatch("[^\n]+") do
		done[id] = true
		results[#results + 1] = { id = id, status = "ok" } -- szczegóły poprzednich wyników nie są potrzebne
	end
end

---------------------------------------------------------------- rodzaje komend
-- kind -> function(cmd) -> verify (function() -> true, gdy efekt widoczny) albo nil, błąd
local KINDS = {}

-- Zmiana nazwy encji (miasto, zakład, grupa stacji, linia, pojazd).
function KINDS.setName(c)
	local entity, name = tonumber(c.entity), c.name
	if not entity or type(name) ~= "string" or name == "" or #name > 200 then return nil, "bad arguments" end
	if util.name(entity) == nil then return nil, "entity not found" end
	api.cmd.sendCommand(api.cmd.makeEntitySetNameCmd(entity, name), function(_, success)
		local p = pending[c.id]
		if p then p.callback = success end
	end)
	return function() return util.name(entity) == name end
end

---------------------------------------------------------------- pętla
local function start(c, wall)
	local id = c.id
	if c.created and wall > 0 and wall - c.created > MAX_AGE_SEC then
		return finish(id, "expired", "older than " .. MAX_AGE_SEC .. " s")
	end
	local kind = KINDS[c.kind]
	if not kind then return finish(id, "invalid", "unknown kind " .. tostring(c.kind)) end
	local ok, verify, err = pcall(kind, c)
	if not ok then return finish(id, "failed", tostring(verify)) end
	if not verify then return finish(id, "invalid", err) end
	pending[id] = { sent = wall, verify = verify }
	util.log(string.format("command %s: sent %s", id, c.kind))
end

local function check(wall)
	for id, p in pairs(pending) do
		local ok, visible = pcall(p.verify)
		if ok and visible then
			finish(id, "ok")
		elseif p.callback == false then
			finish(id, "failed", "game rejected the command")
		elseif wall - p.sent >= VERIFY_SEC then
			finish(id, "failed", string.format("no effect after %d s (callback: %s)", VERIFY_SEC, tostring(p.callback)))
		end
	end
end

-- Wołane z guiUpdate co commandsEveryWallSec.
function M.poll(wall)
	if done == nil then loadDone() end
	check(wall)
	if not util.userdataExists(DIR, QUEUE) then return end
	local ok, t = pcall(function() return app.loadUserdata(DIR, QUEUE) end)
	if not ok or type(t) ~= "table" or type(t.commands) ~= "table" then return end -- np. plik w trakcie zapisu
	for _, c in ipairs(t.commands) do
		if type(c) == "table" and type(c.id) == "string" and not done[c.id] and not pending[c.id] then
			start(c, wall)
		end
	end
end

return M
