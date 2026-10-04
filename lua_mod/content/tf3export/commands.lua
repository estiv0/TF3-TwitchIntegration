-- Komendy od Tf3Bridge (akcje widzów z Twitcha: czat, punkty kanału, przycisk testowy).
--
--   Tf3Bridge -> local\tf3export_cmd\queue.lua    (format app.saveUserdata, pisany przez Tf3Bridge)
--     { commands = { { id = "...", kind = "setName", created = <unix s>, entity = 123, name = "..." }, ... } }
--     rodzaje: setName { entity, name }, adopt { entity, owner },
--              effect { effect = townBoost|industryBoost|lineStop|lineHold, entity, months, factor, user } (effects.lua)
--              createTown { x, y, name, owner }, createIndustry { x, y, file, name, owner } (create.lua)
--   wynik efektu ma też until = { year, month, day } (koniec w kalendarzu gry, do wiadomości na czacie)
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
local effects = ug_require "effects.lua"
local create = ug_require "create.lua"

local M = {}

local DIR, QUEUE, RESULTS = "tf3export_cmd", "queue", "results"
local MAX_RESULTS = 100   -- tyle ostatnich wyników trzymamy w results.lua
local MAX_AGE_SEC = 300   -- starszych komend nie wykonujemy (np. kolejka sprzed restartu gry)
local VERIFY_SEC = 15     -- tyle czekamy, aż efekt komendy będzie widoczny
local VERIFY_CREATE_SEC = 45 -- nowe miasto / zakład: gra buduje dłużej, a zakład potem jeszcze dostaje nazwę

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

local function finish(id, status, err, info)
	pending[id] = nil
	done[id] = true
	results[#results + 1] = { id = id, status = status, error = err, ["until"] = info and info["until"] or nil }
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
-- kind -> function(cmd) -> verify (function() -> true, gdy efekt widoczny) albo nil, błąd;
-- opcjonalnie trzecia wartość: info dołączane do wyniku (np. until)
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

-- Adopcja: "Nazwa" -> "Nazwa | właściciel", tylko gdy obiekt nie ma jeszcze właściciela.
-- Sprawdzane tutaj, w chwili wykonania, żeby dwie adopcje tuż po sobie nie nadpisały się nawzajem.
function KINDS.adopt(c)
	local entity, owner = tonumber(c.entity), c.owner
	if not entity or type(owner) ~= "string" or owner == "" or owner:find("|", 1, true) then return nil, "bad arguments" end
	local name = util.name(entity)
	if name == nil then return nil, "entity not found" end
	local current = name:match("|%s*(.-)%s*$")
	if current and current ~= "" then return nil, "already adopted by " .. current end
	local base = (name:match("^(.-)%s*|") or name):match("^%s*(.-)%s*$")
	local newName = base .. " | " .. owner
	if #newName > 200 then return nil, "name too long" end
	api.cmd.sendCommand(api.cmd.makeEntitySetNameCmd(entity, newName))
	return function() return util.name(entity) == newName end
end

local now, shared = nil, nil -- czas gry i stan game scriptu z bieżącego poll

function KINDS.effect(c)
	return effects.start(c, now)
end

-- Nowe miasto / zakład w miejscu wskazanym przez widza na mapie strony.
function KINDS.createTown(c)
	return create.town(c, pending)
end

function KINDS.createIndustry(c)
	return create.industry(c, pending)
end

---------------------------------------------------------------- pętla
local function start(c, wall)
	local id = c.id
	if c.created and wall > 0 and wall - c.created > MAX_AGE_SEC then
		return finish(id, "expired", "older than " .. MAX_AGE_SEC .. " s")
	end
	local kind = KINDS[c.kind]
	if not kind then return finish(id, "invalid", "unknown kind " .. tostring(c.kind)) end
	local ok, verify, err, info = pcall(kind, c)
	if not ok then return finish(id, "failed", tostring(verify)) end
	if not verify then return finish(id, "invalid", err) end
	local p = pending[id] or {} -- create.lua mógł już zapisać wynik callbacku
	p.sent, p.verify, p.cmd, p.info = wall, verify, c, info
	p.timeout = (c.kind == "createTown" or c.kind == "createIndustry") and VERIFY_CREATE_SEC or VERIFY_SEC
	pending[id] = p
	util.log(string.format("command %s: sent %s", id, c.kind))
end

local function check(wall)
	for id, p in pairs(pending) do
		local ok, visible = pcall(p.verify)
		if ok and visible then
			finish(id, "ok", nil, p.info)
		elseif p.callback == false or wall - p.sent >= p.timeout then
			-- nieudany efekt cofamy, bo Tf3Bridge zwróci widzowi punkty
			if p.cmd.kind == "effect" then pcall(effects.abort, p.cmd) end
			finish(id, "failed", p.callback == false and "game rejected the command"
				or string.format("no effect after %d s (callback: %s)", p.timeout, tostring(p.callback)))
		end
	end
end

-- Nowa sesja (wczytana inna gra): efekty trzeba wczytać od nowa z jej stanu.
function M.reset()
	effects.reset()
end

-- Wołane z guiUpdate co commandsEveryWallSec; gameTime i stan game scriptu (state:get()) dla efektów.
function M.poll(wall, gameTime, sharedState)
	now, shared = gameTime, sharedState
	if done == nil then loadDone() end
	local okE, errE = pcall(effects.update, now, shared)
	if not okE then util.log("effects update failed: " .. tostring(errE)) end
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
