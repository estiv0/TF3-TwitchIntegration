-- Komendy od Tf3Bridge (akcje widzów z Twitcha: czat, punkty kanału, przycisk testowy).
--
--   Tf3Bridge -> local\towns_industries\tf3export_queue.lua    (format app.saveUserdata, pisany przez Tf3Bridge)
--     { commands = { { id = "...", kind = "setName", created = <unix s>, entity = 123, name = "..." }, ... } }
--     rodzaje: setName { entity, name }, adopt { entity, owner },
--              effect { effect = townBoost|industryBoost|industryStrike|industryTaxRaid|lineFuelPremium|lineFuelBad
--                       |lineStop|lineHold, entity, months, factor, user, entities?, target? } (effects.lua)
--              wreck { entity = linia, share } (sabotaż hamulców: część pojazdów linii znika)
--              createTown { x, y, name, owner }, createIndustry { x, y, file, name, owner } (create.lua)
--              notify { title, text, entity?, dismissMs? } (powiadomienie w grze, twitch_notification.script.lua)
--   wynik efektu ma też until = { year, month, day } (koniec w kalendarzu gry, do wiadomości na czacie),
--   a wynik createTown / createIndustry: entity = id nowej encji (link na czacie);
--   wynik wreck: count = zniszczone pojazdy, total = pojazdy linii przed sabotażem
--   mod      -> local\towns_industries\tf3export_results.lua  (app.saveUserdata)
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

local DIR, QUEUE, RESULTS = util.USERDATA_DIR, util.FILE_PREFIX .. "queue", util.FILE_PREFIX .. "results"
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
	results[#results + 1] = { id = id, status = status, error = err, ["until"] = info and info["until"] or nil,
		entity = info and info.entity or nil, count = info and info.count or nil, total = info and info.total or nil }
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

-- Sabotaż hamulców: losowo od 1 do share (domyślnie 50%) pojazdów linii, ale zawsze co najmniej jeden,
-- znika jak po wypadku. Gra nie ma wypadków, więc pojazdy sprzedajemy, a kwotę ze sprzedaży (wartość
-- po amortyzacji) zabieramy wpisem w księgach: streamer traci pojazd i nic na tym nie zarabia.
-- Nie do cofnięcia; wpis w księgach dopiero, gdy pojazdów już nie ma (nieudana sprzedaż nic nie kosztuje).
local seeded = false

-- Pojazd wciąż jeździ na linii. Samo entityExists nie wystarcza: gra może oddać id sprzedanego
-- pojazdu nowej encji, a wtedy sprzedaż wyglądałaby na nieudaną i widz dostałby punkty z powrotem.
local function stillOnLine(v, line)
	if not api.engine.entityExists(v) then return false end
	local tv = util.comp(v, util.ct.TRANSPORT_VEHICLE)
	return tv ~= nil and util.field(tv, "line") == line
end

function KINDS.wreck(c)
	local line, share = tonumber(c.entity), tonumber(c.share) or 0.5
	if not line then return nil, "bad arguments" end
	local vs = util.lineVehicles(line)
	if #vs == 0 then return nil, "line has no vehicles" end
	if not seeded then
		math.randomseed(math.floor((os and os.time and os.time() or 0) + util.clock() * 1000))
		seeded = true
	end
	share = math.max(0, math.min(1, share))
	local count = math.random(1, math.max(1, math.floor(#vs * share)))
	for i = #vs, 2, -1 do -- tasowanie Fishera-Yatesa, bierzemy pierwsze count
		local j = math.random(1, i)
		vs[i], vs[j] = vs[j], vs[i]
	end
	local picked, value = {}, 0
	for i = 1, count do
		picked[i] = vs[i]
		local ok, v = pcall(function() return api.engine.util.vehicle.getDepreciatedValue(vs[i]) end)
		if ok and tonumber(v) then value = value + tonumber(v) end
	end
	api.cmd.sendCommand(api.cmd.makeVehicleSellCmd(picked), function(_, success)
		local p = pending[c.id]
		if p then p.callback = success end
	end)
	util.log(string.format("wreck line %s: selling %d of %d vehicles, value %d", tostring(line), count, #vs, value))
	local booked = false
	local function remaining()
		local n = 0
		for _, v in ipairs(picked) do
			if stillOnLine(v, line) then n = n + 1 end
		end
		return n
	end
	return function()
		-- callback true = gra przyjęła sprzedaż, nawet jeśli pojazdy znikną dopiero w następnym kroku symulacji
		local p = pending[c.id]
		if not (p and p.callback == true) and remaining() > 0 then return false end
		if not booked and value > 0 then
			booked = true -- sprzedaż potwierdzona: błąd księgowania nie może zwrócić punktów
			local ok, err = pcall(function()
				local entry = api.type.JournalEntry.new()
				entry.amount = -value
				entry.time = -1
				entry.category.type = api.type.JournalEntry.Type.OTHER
				api.cmd.sendCommand(api.cmd.makeJournalBookAssetCmd(api.engine.util.getPlayer(), entry, api.type.Vec3f.new(0, 0, 0)))
			end)
			if not ok then util.log("wreck: booking the loss failed: " .. tostring(err)) end
		end
		return true
	end, nil, { count = count, total = #vs,
		diag = function() return string.format("%d of %d sold vehicles still on the line", remaining(), count) end,
		holdOnPause = true }
end

-- Powiadomienie w grze (po rozliczonej nagrodzie za punkty kanału). Teksty są gotowe z Tf3Bridge.
-- Stanu powiadomień nie sprawdzamy (żyje w game scripcie gry), więc wysłanie = sukces.
local NOTIFICATION_TYPE = "estiv0_tf3export::/tf3export/twitch_notification.script"
-- W bajtach (Tf3Bridge tnie już po znakach: 100 / 300); tu tylko zabezpieczenie, które nie rozcina liter UTF-8.
local MAX_TITLE, MAX_TEXT = 400, 1200

local function clip(s, max)
	if #s <= max then return s end
	s = s:sub(1, max)
	-- urwany znak wielobajtowy na końcu: cofamy się po bajtach kontynuacji (10xxxxxx) do bajtu początkowego
	local lead = #s
	while lead > 1 and s:byte(lead) >= 128 and s:byte(lead) < 192 do lead = lead - 1 end
	local b = s:byte(lead)
	if b >= 192 then
		local need = b >= 240 and 4 or b >= 224 and 3 or 2
		if #s - lead + 1 < need then s = s:sub(1, lead - 1) end
	end
	return s
end

-- { entity, revision } jak entity_util.makeEntityAndRevision z gry: kliknięcie działa, póki encja się nie zmieni
-- (np. nie zostanie usunięta); nil, gdy encji nie ma.
local function entityAndRevision(entity)
	local ok, res = pcall(function()
		if not api.engine.entityExists(entity) then return nil end
		local rev = api.engine.getRevision(entity)
		return { entity = entity, revision = { num = { rev.num[1], rev.num[2], rev.num[3] } } }
	end)
	if not ok then util.log("notify: no revision for " .. tostring(entity) .. ": " .. tostring(res)) end
	return ok and res or nil
end

function KINDS.notify(c)
	local title, text = c.title, c.text
	if type(title) ~= "string" or title == "" or type(text) ~= "string" then return nil, "bad arguments" end
	local params = { title = clip(title, MAX_TITLE), text = clip(text, MAX_TEXT) }
	local entity = tonumber(c.entity)
	if entity then params.entity = entityAndRevision(entity) end
	api.cmd.sendCommand(api.cmd.makeScriptingSendEventCmd("", "Notifications", "add", {
		type = NOTIFICATION_TYPE,
		params = params,
		autoDismissDuration = math.floor(tonumber(c.dismissMs) or 10000), -- tylko chowa okienko; wpis zostaje w dzienniku
	}))
	return function() return true end
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

-- paused: czas gry stoi od poprzedniego poll. Komendy z info.holdOnPause (wreck) czekają wtedy dłużej,
-- bo zmiana może wejść dopiero po wznowieniu symulacji, a punktów za wykonaną już sprzedaż nie da się odebrać.
local function check(wall, paused)
	for id, p in pairs(pending) do
		local ok, visible = pcall(p.verify)
		local dt = wall - (p.checked or p.sent)
		p.checked = wall
		if not (paused and p.info and p.info.holdOnPause) then p.waited = (p.waited or 0) + dt end
		if ok and visible then
			finish(id, "ok", nil, p.info)
		elseif p.callback == false or (p.waited or 0) >= p.timeout then
			-- nieudany efekt cofamy, bo Tf3Bridge zwróci widzowi punkty
			if p.cmd.kind == "effect" then pcall(effects.abort, p.cmd) end
			local why = not ok and ("verify error: " .. tostring(visible))
				or p.info and p.info.diag and select(2, pcall(p.info.diag))
			finish(id, "failed", (p.callback == false and "game rejected the command"
				or string.format("no effect after %d s (callback: %s)", p.timeout, tostring(p.callback)))
				.. (why and ("; " .. tostring(why)) or ""))
		end
	end
end

-- Nowa sesja (wczytana inna gra): efekty trzeba wczytać od nowa z jej stanu.
function M.reset()
	effects.reset()
end

-- Wołane z guiUpdate co commandsEveryWallSec; gameTime i stan game scriptu (state:get()) dla efektów.
local lastGameTime = nil

function M.poll(wall, gameTime, sharedState)
	local paused = lastGameTime ~= nil and gameTime == lastGameTime
	lastGameTime = gameTime
	now, shared = gameTime, sharedState
	if done == nil then loadDone() end
	local okE, errE = pcall(effects.update, now, shared)
	if not okE then util.log("effects update failed: " .. tostring(errE)) end
	check(wall, paused)
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
