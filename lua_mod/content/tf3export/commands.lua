-- Commands from Tf3Bridge (Twitch viewer actions: chat, channel points, the test button).
--
--   Tf3Bridge -> local\towns_industries\tf3export_queue.lua    (app.saveUserdata format, written by Tf3Bridge)
--     { commands = { { id = "...", kind = "setName", created = <unix s>, entity = 123, name = "..." }, ... } }
--     kinds: setName { entity, name }, adopt { entity, owner },
--              effect { effect = townBoost|industryBoost|industryStrike|industryTaxRaid|lineFuelPremium|lineFuelBad
--                       |lineStop|lineHold, entity, months, factor, user, entities?, target? } (effects.lua)
--              wreck { entity = line, share } (brake sabotage: some of the line's vehicles disappear)
--              subsidy { amount, user } (subsidy), loan { amount, months, percent, user } (a loan in the game's loan system)
--              createTown { x, y, name, owner }, createIndustry { x, y, file, name, owner } (create.lua)
--              notify { title, text, entity?, dismissMs? } (in-game notification, twitch_notification.script.lua)
--   an effect's result also has until = { year, month, day } (the end in the game calendar, for the chat message),
--   and the result of createTown / createIndustry: entity = id of the new entity (link in chat);
--   the result of wreck: count = vehicles destroyed, total = the line's vehicles before the sabotage
--   mod      -> local\towns_industries\tf3export_results.lua  (app.saveUserdata)
--     { json = '{"results":[{"id":"...","status":"ok|failed|invalid|expired","error":"..."}]}',
--       done = "id1\nid2\n..." }
--
-- GUI thread only (app.loadUserdata/saveUserdata). Tf3Bridge removes a command from the queue once it sees
-- its result; until then the mod skips it by id (the done set, rebuilt from results.lua after a game restart).
--
-- The game sends commands (api.cmd.sendCommand) from its scripts on simulation threads; from the GUI thread
-- we haven't checked this yet. So the result doesn't rely only on the callback: after sending
-- we check on the following reads whether the change is visible (e.g. the new name), with a time limit.

local json = ug_require "json.lua"
local util = ug_require "util.lua"
local effects = ug_require "effects.lua"
local create = ug_require "create.lua"

local M = {}

local DIR, QUEUE, RESULTS = util.USERDATA_DIR, util.FILE_PREFIX .. "queue", util.FILE_PREFIX .. "results"
local MAX_RESULTS = 100   -- how many of the latest results we keep in results.lua
local MAX_AGE_SEC = 300   -- older commands aren't executed (e.g. a queue from before a game restart)
local VERIFY_SEC = 15     -- how long we wait for a command's effect to become visible
local VERIFY_CREATE_SEC = 45 -- new town / industry: the game builds longer, and the industry still gets its name afterwards

local done = nil          -- id -> true; nil = not yet loaded from results.lua
local results = {}        -- { id, status, error } (newest last)
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
		results[#results + 1] = { id = id, status = "ok" } -- details of earlier results aren't needed
	end
end

---------------------------------------------------------------- command kinds
-- kind -> function(cmd) -> verify (function() -> true when the effect is visible) or nil, error;
-- optionally a third value: info added to the result (e.g. until)
local KINDS = {}

-- Renaming an entity (town, industry, station group, line, vehicle).
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

-- Adoption: "Name" -> "Name | owner", only when the object has no owner yet.
-- Checked here, at execution time, so that two adoptions right after each other don't overwrite each other.
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

local now, shared = nil, nil -- game time and game script state from the current poll

function KINDS.effect(c)
	return effects.start(c, now)
end

-- Brake sabotage: randomly 1 to share (default 50%) of the line's vehicles, but always at least one,
-- disappear as after an accident. The game has no accidents, so we sell the vehicles and take the sale amount (value
-- after depreciation) back with a journal entry: the streamer loses the vehicle and gains nothing from it.
-- Can't be undone; the journal entry only comes once the vehicles are gone (a failed sale costs nothing).
local seeded = false

-- The vehicle still runs on the line. entityExists alone isn't enough: the game may give a sold vehicle's id
-- to a new entity, and then the sale would look failed and the viewer would get the points back.
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
	for i = #vs, 2, -1 do -- Fisher-Yates shuffle, we take the first count
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
		-- callback true = the game accepted the sale, even if the vehicles only disappear in the next simulation step
		local p = pending[c.id]
		if not (p and p.callback == true) and remaining() > 0 then return false end
		if not booked and value > 0 then
			booked = true -- sale confirmed: a booking error must not refund the points
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

-- Money for the company from a viewer: a subsidy (a SUBSIDY journal entry) or a loan in the game's loan system.
-- With unlimited money (getPlayersBalance == nil) neither makes sense, and the game freezes loans then.
local function limitedMoney()
	return api.engine.util.finance.getPlayersBalance(api.engine.util.getPlayer()) ~= nil
end

local function moneyArg(c)
	local amount = tonumber(c.amount)
	if not amount or amount < 1 or amount > 1e12 then return nil end
	return math.floor(amount)
end

function KINDS.subsidy(c)
	local amount = moneyArg(c)
	if not amount then return nil, "bad arguments" end
	if not limitedMoney() then return nil, "unlimited money" end
	local entry = api.type.JournalEntry.new()
	entry.amount = amount
	entry.time = -1
	entry.category.type = api.type.JournalEntry.Type.SUBSIDY
	api.cmd.sendCommand(api.cmd.makeJournalBookAssetCmd(api.engine.util.getPlayer(), entry, api.type.Vec3f.new(0, 0, 0)),
		function(_, success)
			local p = pending[c.id]
			if p then p.callback = success end
		end)
	-- the balance changes all the time, so the game's callback decides the result
	return function()
		local p = pending[c.id]
		return p ~= nil and p.callback == true
	end
end

-- Loan: the same event the Finances → Loans window sends for a custom loan ({ nil, loan }); the game's game script
-- (game_mechanics/finance/loan.script.tl) pays out the amount, adds the loan to obtainedLoans and every month collects
-- an installment: amount / months + interest (amount × % / months). The loan is visible and can be repaid in the Finances window.
local LOAN_SCRIPT = "::/game_mechanics/finance/loan.gs"
local MAX_LOANS = 4 -- loan_util.maximalObtainableLoans

local function loanTable()
	local entity = api.engine.system.gameScriptSystem.getEntityForGameScript(LOAN_SCRIPT)
	local gs = entity and api.engine.getComponent(entity, api.type.ComponentType.GAME_SCRIPT)
	return gs and gs.state or nil
end

function KINDS.loan(c)
	local amount, months, percent = moneyArg(c), math.floor(tonumber(c.months) or 0), tonumber(c.percent)
	if not amount or months < 1 or months > 360 or not percent or percent < 0 or percent > 100 then return nil, "bad arguments" end
	if not limitedMoney() then return nil, "unlimited money" end
	local t = loanTable()
	if type(t) ~= "table" then return nil, "loan script not found" end
	local count = #util.list(t.obtainedLoans)
	if count >= MAX_LOANS then return nil, "loan limit " .. count end
	local freeId = tonumber(t.freeId) or 0
	local loan = {
		type = "Custom",
		amount = amount,
		duration = months * api.util.getDefaultMonthDuration(),
		percentage = percent / 100,
	}
	api.cmd.sendCommand(api.cmd.makeScriptingSendEventCmd("", "Loan", "Obtain", { nil, loan }))
	util.log(string.format("loan: %d for %d months at %s%% (%d of %d loans)", amount, months, tostring(percent), count, MAX_LOANS))
	-- the game script adds the loan only after a successful payout (freeId grows by 1)
	return function()
		local now = loanTable()
		return type(now) == "table" and (tonumber(now.freeId) or 0) > freeId
	end, nil, { holdOnPause = true }
end

-- In-game notification (after a settled channel points reward). The texts come ready from Tf3Bridge.
-- We don't check the notification state (it lives in the game's game script), so sending = success.
local NOTIFICATION_TYPE = "estiv0_tf3export::/tf3export/twitch_notification.script"
-- In bytes (Tf3Bridge already cuts by characters: 100 / 300); here only a safeguard that doesn't split UTF-8 letters.
local MAX_TITLE, MAX_TEXT = 400, 1200

local function clip(s, max)
	if #s <= max then return s end
	s = s:sub(1, max)
	-- a cut-off multibyte character at the end: step back over continuation bytes (10xxxxxx) to the lead byte
	local lead = #s
	while lead > 1 and s:byte(lead) >= 128 and s:byte(lead) < 192 do lead = lead - 1 end
	local b = s:byte(lead)
	if b >= 192 then
		local need = b >= 240 and 4 or b >= 224 and 3 or 2
		if #s - lead + 1 < need then s = s:sub(1, lead - 1) end
	end
	return s
end

-- { entity, revision } like the game's entity_util.makeEntityAndRevision: clicking works as long as the entity doesn't change
-- (e.g. isn't removed); nil when the entity is gone.
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
		autoDismissDuration = math.floor(tonumber(c.dismissMs) or 10000), -- only hides the popup; the entry stays in the log
	}))
	return function() return true end
end

-- New town / industry at the spot the viewer picked on the website map.
function KINDS.createTown(c)
	return create.town(c, pending)
end

function KINDS.createIndustry(c)
	return create.industry(c, pending)
end

---------------------------------------------------------------- loop
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
	local p = pending[id] or {} -- create.lua may already have stored the callback result
	p.sent, p.verify, p.cmd, p.info = wall, verify, c, info
	p.timeout = (c.kind == "createTown" or c.kind == "createIndustry") and VERIFY_CREATE_SEC or VERIFY_SEC
	pending[id] = p
	util.log(string.format("command %s: sent %s", id, c.kind))
end

-- paused: game time hasn't moved since the previous poll. Commands with info.holdOnPause (wreck) then wait longer,
-- because the change may only land after the simulation resumes, and points for a sale already made can't be taken back.
local function check(wall, paused)
	for id, p in pairs(pending) do
		local ok, visible = pcall(p.verify)
		local dt = wall - (p.checked or p.sent)
		p.checked = wall
		if not (paused and p.info and p.info.holdOnPause) then p.waited = (p.waited or 0) + dt end
		if ok and visible then
			finish(id, "ok", nil, p.info)
		elseif p.callback == false or (p.waited or 0) >= p.timeout then
			-- a failed effect is undone, because Tf3Bridge will refund the viewer's points
			if p.cmd.kind == "effect" then pcall(effects.abort, p.cmd) end
			local why = not ok and ("verify error: " .. tostring(visible))
				or p.info and p.info.diag and select(2, pcall(p.info.diag))
			finish(id, "failed", (p.callback == false and "game rejected the command"
				or string.format("no effect after %d s (callback: %s)", p.timeout, tostring(p.callback)))
				.. (why and ("; " .. tostring(why)) or ""))
		end
	end
end

-- New session (another game loaded): effects have to be reloaded from its state.
function M.reset()
	effects.reset()
end

-- Called from guiUpdate every commandsEveryWallSec; gameTime and the game script state (state:get()) for the effects.
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
	if not ok or type(t) ~= "table" or type(t.commands) ~= "table" then return end -- e.g. the file is being written
	for _, c in ipairs(t.commands) do
		if type(c) == "table" and type(c.id) == "string" and not done[c.id] and not pending[c.id] then
			start(c, wall)
		end
	end
end

return M
