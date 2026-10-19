-- Timed effects from channel points rewards: town boost, industry productivity (boost), shipping
-- block from industries (strike, tax raid), the speed of a line's vehicles (premium / watered-down fuel),
-- line stop.
-- Each effect lasts X game months and undoes itself (game time stands still while paused, so does the effect).
-- Months, not days: a game day lasts only 2-3 s, so an effect of a few days was over before you could see it.
--
-- Runs on the GUI thread (that's where we read the command queue). The list of effects is copied to the game script state
-- (state, stored in the save) with the "tf3x_effects" script event handled in handleEvent
-- (a simulation thread; only it can write state). After a game is loaded the effects come back from the state.
--
-- An effect on the same target (e.g. a second boost of the same town) extends the time instead of stacking;
-- when the new redemption has a stronger multiplier (a settings change), the effect gets the stronger one.
-- Fuel (premium / watered-down) is one queue per line: other fuel bought in the meantime doesn't multiply
-- with the current one, it just waits and starts working when the current one ends (queue below).
--
-- Industry productivity and vehicle speed are set by one controller per object (Controller below):
-- different effects on the same object multiply instead of fighting over the value. A strike and a raid on the same
-- industry multiply too (the share of new production that stays; freezeSync below).

local util = ug_require "util.lua"
local ct = util.ct

local E = {}

E.EVENT = "tf3x_effects"
E.SCRIPT_ID = "tf3export"

local MIN_MONTHS, MAX_MONTHS = 1, 60
local SETTLE_POLLS = 3 -- how many reads (~s) we wait for the game to accept the value we set

local active = nil -- key -> effect (data only, no functions); nil = not yet loaded from the state
local lastNow = nil -- the last known game time (controllers skip effects waiting in a queue)

---------------------------------------------------------------- helpers
local function send(cmd)
	api.cmd.sendCommand(cmd)
end

local function copyList(v)
	local out = {}
	for i, x in ipairs(util.list(v)) do out[i] = x end
	return out
end

local function near(a, b) return a ~= nil and b ~= nil and math.abs(a - b) < 1e-3 end

-- A game month in gameTime units (a year = 365 days).
local function monthLength()
	return util.dayLength() * 365 / 12
end

local function fmt(x) return x and string.format("%.3f", x) or "?" end

---------------------------------------------------------------- value controller
-- Keeps one number on many objects (industry productivity, vehicle speed): base (the value
-- from before the effects) × the product of the active effects' multipliers. The game may change this number itself (the
-- workers script sets productivity), so after setting it we wait a few reads for the game to accept it,
-- and remember what it actually accepted (applied; the game may have its own cap). Only a change relative to
-- applied means someone else changed it: then the new value becomes the base. Without this every difference
-- from the target would recompute the base over and over. After the effects end the base comes back.
local function Controller(name, read, write)
	local C = { entries = {} } -- id -> { baseline, factor, target, applied, settle, rebases }

	local function setTarget(id, en)
		en.target = en.baseline * en.factor
		en.applied, en.settle = nil, SETTLE_POLLS
		write(id, en.target)
	end

	local function maintain(id, en)
		local v = read(id)
		if v == nil then return false end
		if en.applied == nil then
			en.settle = (en.settle or 1) - 1
			if not near(v, en.target) and en.settle > 0 then return false end
			en.applied, en.settle = v, nil
			if not near(v, en.target) then
				util.log(string.format("%s %s: game keeps %s instead of %s", name, tostring(id), fmt(v), fmt(en.target)))
			end
			return true
		end
		if near(v, en.applied) then return false end
		if near(v, en.target) then -- the game accepted our target late: that's not a new base (otherwise the multiplier twice)
			en.applied = v
			return true
		end
		en.rebases = (en.rebases or 0) + 1
		if en.rebases <= 3 then
			util.log(string.format("%s %s: changed by the game %s -> %s, new base (x%s)",
				name, tostring(id), fmt(en.applied), fmt(v), fmt(en.factor)))
		end
		en.baseline = v
		setTarget(id, en)
		return true
	end

	-- wanted: id -> multiplier. Returns true when data to be stored in the state changed.
	function C.sync(wanted)
		local changed = false
		for id, f in pairs(wanted) do
			local en = C.entries[id]
			if en == nil then
				local v = read(id)
				if v ~= nil then
					en = { baseline = v, factor = f }
					C.entries[id] = en
					setTarget(id, en)
					changed = true
				end
			elseif not near(en.factor, f) then
				en.factor = f
				setTarget(id, en)
				changed = true
			elseif maintain(id, en) then
				changed = true
			end
		end
		for id, en in pairs(C.entries) do
			if wanted[id] == nil then
				if read(id) ~= nil then
					local ok, err = pcall(write, id, en.baseline)
					if not ok then util.log(string.format("%s %s: restoring %s failed: %s", name, tostring(id), fmt(en.baseline), tostring(err))) end
				end
				C.entries[id] = nil
				changed = true
			end
		end
		return changed
	end

	-- Did the game accept the target: the value equals the target or moved from the base towards it (the game may have a cap).
	function C.reached(id)
		local en, v = C.entries[id], read(id)
		if en == nil or v == nil then return false end
		return near(v, en.target) or (v - en.baseline) * (en.target - en.baseline) > 0
	end

	function C.describe(id)
		local en = C.entries[id]
		return en and string.format("%s -> %s (x%s)", fmt(en.baseline), fmt(en.target), fmt(en.factor)) or "?"
	end

	function C.save()
		local out = {}
		for id, en in pairs(C.entries) do
			out[#out + 1] = { id = id, baseline = en.baseline, factor = en.factor, target = en.target, applied = en.applied }
		end
		return out
	end

	function C.load(list)
		C.entries = {}
		for _, x in ipairs(util.list(list)) do
			if type(x) == "table" and tonumber(x.id) and x.baseline and x.factor and x.target then
				-- settle again: after loading we don't know whether the game managed to accept the last target
				C.entries[tonumber(x.id)] = { baseline = x.baseline, factor = x.factor, target = x.target, applied = x.applied,
					settle = x.applied == nil and SETTLE_POLLS or nil }
			end
		end
	end

	return C
end

-- Industry productivity: the stock list modifiers (on the industry entity or in ind.stockList).
local function productivity(industry)
	local ind = util.comp(industry, ct.INDUSTRY)
	local sl = (ind and util.comp(ind.stockList, ct.STOCK_LIST)) or util.comp(industry, ct.STOCK_LIST)
	local m = sl and util.field(sl, "modifiers")
	return m and tonumber(m.productivity) or nil
end

local function setProductivity(industry, p)
	local m = api.type.StockList.Modifiers.new()
	m.productivity = p
	send(api.cmd.makeStockListSetModifiersCmd(industry, m))
end

-- A vehicle's top speed: modifiers.topSpeedScale of the TRANSPORT_VEHICLE component.
local VEHICLE_SCALES = { "noiseScale", "pollutionScale", "comfortScale" }

local function vehicleModifiers(vehicle)
	local tv = util.comp(vehicle, ct.TRANSPORT_VEHICLE)
	return tv and util.field(tv, "modifiers")
end

local function speedScale(vehicle)
	local m = vehicleModifiers(vehicle)
	return m and tonumber(util.field(m, "topSpeedScale")) or nil
end

local function setSpeedScale(vehicle, scale)
	local cur = vehicleModifiers(vehicle)
	local m = api.type.TransportVehicle.Modifiers.new()
	for _, k in ipairs(VEHICLE_SCALES) do -- the command sets all scales at once: the others unchanged
		local x = cur and tonumber(util.field(cur, k))
		m[k] = x or 1.0
	end
	m.topSpeedScale = scale
	send(api.cmd.makeVehicleSetModifiersCmd(vehicle, m))
end

local industries = Controller("productivity", productivity, setProductivity)
local vehicles = Controller("speed", speedScale, setSpeedScale)

---------------------------------------------------------------- shipping block (strike, raid)
-- A strike doesn't touch productivity: the game computes a rule's cycle length once, when it starts
-- (ruleDatas[i].applyHistory[].interval), so a cycle started at ×0.01 lasted years after the effect ended.
-- So the industry produces normally, and we take the new items off the output stocks (a debug
-- command that sets the stock level; it works outside debug mode). Vehicles can pick up what was already there.
-- New items are recognized by arrival time (getStockEntities: {time, entity}), so a pickup
-- and production in the same second don't get mixed up. factor = the share of new items that stays.
local freezes = {} -- industry -> { sl = stockList entity, stocks = { [stockId] = { newest, target, carry } } }
local freezeFailed = false

local function stockListOf(industry)
	local ind = util.comp(industry, ct.INDUSTRY)
	return (ind and ind.stockList) or industry
end

local function outputStockIds(sl)
	local out = {}
	local list = util.comp(sl, ct.STOCK_LIST)
	local enums = api.type and api.type["enum"]
	local outType = enums and util.field(util.field(enums, "StockListType"), "OutputStock")
	if not list or outType == nil then return out end
	for i, s in ipairs(util.list(list.stocks)) do
		if s.type == outType then out[#out + 1] = i - 1 end
	end
	return out
end

-- Arrival times of the items in a stock; nil on error.
local function arrivals(sl, stockId)
	local ok, list = pcall(api.engine.system.simEntityAtStockSystem.getStockEntities, sl, stockId)
	if not ok then return nil end
	local out = {}
	for i, x in ipairs(util.list(list)) do out[i] = tonumber(util.field(x, 1)) or 0 end
	return out
end

local function newFreeze(industry)
	local sl = stockListOf(industry)
	local st = { sl = sl, stocks = {} }
	for _, id in ipairs(outputStockIds(sl)) do
		local newest = 0
		for _, t in ipairs(arrivals(sl, id) or {}) do newest = math.max(newest, t) end
		st.stocks[id] = { newest = newest, carry = 0 }
	end
	return st
end

local function freezeStep(st, keep)
	for id, s in pairs(st.stocks) do
		local times = arrivals(st.sl, id)
		if times then
			local count, fresh, newest = #times, 0, s.newest
			for _, t in ipairs(times) do
				if t > s.newest then fresh = fresh + 1 end
				newest = math.max(newest, t)
			end
			s.newest = newest
			local base = count - fresh
			-- the previous command hasn't landed yet: it must not be overwritten with a higher value
			if s.target and base > s.target then base = s.target end
			local kept = fresh * keep + s.carry
			local whole = math.floor(kept)
			s.carry = kept - whole
			local target = base + whole
			if target < count then
				local ok, err = pcall(function() send(api.cmd.debug.makeStockSetCargoAmountCmd(st.sl, id, target)) end)
				if not ok and not freezeFailed then
					freezeFailed = true
					util.log("effects: setting stock amount failed: " .. tostring(err))
				end
			end
			s.target = target
		end
	end
end

-- wanted: industry -> the share of new items that stays (the product of the effects' factors).
local function freezeSync(wanted)
	for id, keep in pairs(wanted) do
		if util.comp(id, ct.INDUSTRY) then
			local st = freezes[id]
			if st == nil then
				freezes[id] = newFreeze(id)
			else
				freezeStep(st, keep)
			end
		end
	end
	for id in pairs(freezes) do
		if wanted[id] == nil then freezes[id] = nil end -- nothing to undo: production ran normally
	end
end

---------------------------------------------------------------- effect kinds
-- Two styles:
--  * group = "industry" | "speed": the effect only gives a multiplier (range = allowed range), the controller
--    does the rest; group = "freeze": shipping block (freezeSync), factor = the share of new production that
--    stays; multi = an effect on many industries at once (e.entities), keyed by c.target;
--  * apply(e) -> verify or nil, error; revert(e) undoes it (lineStop, lineHold, townBoost).
-- queue = queue name: effects with the same queue on the same target run one after another, not at once.
local TYPES = {}

-- Town: we multiply sizeFactors (residential, commercial, industrial). Buildings grow into the new
-- size gradually (updateAllBuildings = false); after the effect ends the old factors come back.
TYPES.townBoost = {
	range = { 1.0, 5.0 },
	apply = function(e)
		local town = util.comp(e.entity, ct.TOWN)
		if not town then return nil, "not a town" end
		local original = copyList(town.sizeFactors)
		if #original == 0 then return nil, "town has no size factors" end
		local boosted = {}
		for i, v in ipairs(original) do boosted[i] = v * e.factor end
		e.original = original
		send(api.cmd.makeTownUpdateSizeCmd(e.entity, boosted, false))
		return function()
			local t = util.comp(e.entity, ct.TOWN)
			return t ~= nil and near(copyList(t.sizeFactors)[1], boosted[1])
		end
	end,
	upgrade = function(e)
		local boosted = {}
		for i, v in ipairs(e.original) do boosted[i] = v * e.factor end
		send(api.cmd.makeTownUpdateSizeCmd(e.entity, boosted, false))
	end,
	revert = function(e)
		if util.comp(e.entity, ct.TOWN) then send(api.cmd.makeTownUpdateSizeCmd(e.entity, e.original, false)) end
	end,
	describe = function(e)
		return string.format("factor %s, size factors %s -> x%s", fmt(e.factor), table.concat(e.original or {}, "/"), fmt(e.factor))
	end,
}

-- Industry: boost (productivity, "Productivity" on the website); strike and tax raid (all
-- industries of one viewer) are a shipping block: factor = the share of new production that reaches the stock.
TYPES.industryBoost = { group = "industry", range = { 1.0, 5.0 } }
TYPES.industryStrike = { group = "freeze", range = { 0.0, 1.0 } }
TYPES.industryTaxRaid = { group = "freeze", range = { 0.0, 1.0 }, multi = true }

-- Line: the top speed of all its vehicles (including ones bought during the effect).
TYPES.lineFuelPremium = { group = "speed", range = { 1.0, 3.0 }, queue = "fuel" }
TYPES.lineFuelBad = { group = "speed", range = { 0.1, 1.0 }, queue = "fuel" }

-- Line, "stop" variant: like the in-game stop button; a vehicle stops where it is (also mid-route).
-- Vehicles the player stopped earlier are left alone (we don't touch them when undoing either).
TYPES.lineStop = {
	apply = function(e)
		local vs = util.lineVehicles(e.entity)
		if #vs == 0 then return nil, "line has no vehicles" end
		local stopped = {}
		for _, v in ipairs(vs) do
			local tv = util.comp(v, ct.TRANSPORT_VEHICLE)
			if tv and not tv.userStopped then
				send(api.cmd.makeVehicleSetStoppedByUserCmd(v, true))
				stopped[#stopped + 1] = v
			end
		end
		if #stopped == 0 then return nil, "all vehicles already stopped" end
		util.log(string.format("effect lineStop:%s: stopping %d of %d vehicles", tostring(e.entity), #stopped, #vs))
		e.vehicles = stopped
		return function()
			for _, v in ipairs(stopped) do
				local tv = util.comp(v, ct.TRANSPORT_VEHICLE)
				if tv and not tv.userStopped then return false end
			end
			return true
		end
	end,
	revert = function(e)
		for _, v in ipairs(e.vehicles or {}) do
			local tv = util.comp(v, ct.TRANSPORT_VEHICLE)
			if tv and tv.userStopped then send(api.cmd.makeVehicleSetStoppedByUserCmd(v, false)) end
		end
	end,
}

-- Line, "hold" variant: manual departure; vehicles drive to the stop and wait there.
-- The API doesn't expose this mode, so the effect can't be checked (sending counts as success).
TYPES.lineHold = {
	apply = function(e)
		local vs = util.lineVehicles(e.entity)
		if #vs == 0 then return nil, "line has no vehicles" end
		for _, v in ipairs(vs) do send(api.cmd.makeVehicleSetManualDepartureCmd(v, true)) end
		util.log(string.format("effect lineHold:%s: manual departure for %d vehicles", tostring(e.entity), #vs))
		e.vehicles = vs
		return function() return true end
	end,
	revert = function(e)
		for _, v in ipairs(e.vehicles or {}) do
			if util.comp(v, ct.TRANSPORT_VEHICLE) then
				send(api.cmd.makeVehicleSetManualDepartureCmd(v, false))
				pcall(send, api.cmd.makeVehicleTryToDepartCmd(v))
			end
		end
	end,
}

-- The effect's targets: a list of industries (multi) or one entity.
local function targets(e)
	return e.entities or { e.entity }
end

-- The effect waits in a queue (its time hasn't come yet).
local function waiting(e, now)
	return now ~= nil and e.startTime ~= nil and now < e.startTime
end

-- Other effects from the same queue on the same target.
local function queueMates(e)
	local out = {}
	local q = TYPES[e.type].queue
	if not q then return out end
	for _, o in pairs(active) do
		if o ~= e and TYPES[o.type].queue == q and o.entity == e.entity then out[#out + 1] = o end
	end
	return out
end

-- Shifts in time the queued effects that start after e ends (after e is extended or interrupted).
local function shiftQueue(e, from, delta)
	for _, o in ipairs(queueMates(e)) do
		if o.startTime and o.startTime >= from - 1e-6 then
			o.startTime = o.startTime + delta
			o.untilTime = o.untilTime + delta
		end
	end
end

---------------------------------------------------------------- controllers
-- Multipliers from all active effects -> controllers. True = a change to store in the state.
local function syncControllers()
	local prod, freeze, lineSpeed, speed = {}, {}, {}, {}
	for _, e in pairs(active) do
		local t = TYPES[e.type]
		if waiting(e, lastNow) then
			-- still queued: no effect on the game
		elseif t.group == "industry" then
			for _, id in ipairs(targets(e)) do prod[id] = (prod[id] or 1.0) * e.factor end
		elseif t.group == "freeze" then
			for _, id in ipairs(targets(e)) do freeze[id] = (freeze[id] or 1.0) * e.factor end
		elseif t.group == "speed" then
			lineSpeed[e.entity] = (lineSpeed[e.entity] or 1.0) * e.factor
		end
	end
	for line, f in pairs(lineSpeed) do
		for _, v in ipairs(util.lineVehicles(line)) do speed[v] = f end
	end
	freezeSync(freeze) -- no state in the save: after loading the block starts from the current stock levels
	local a = industries.sync(prod)
	local b = vehicles.sync(speed)
	return a or b
end

---------------------------------------------------------------- state
local function persist()
	local list = {}
	for _, e in pairs(active) do list[#list + 1] = e end
	local ctl = { industries = industries.save(), vehicles = vehicles.save() }
	local ok, err = pcall(send, api.cmd.makeScriptingSendEventCmd("", E.SCRIPT_ID, E.EVENT, { effects = list, ctl = ctl }))
	if not ok then util.log("effects: saving to game state failed: " .. tostring(err)) end
end

-- New session (a game loaded): effects come back from the game script state on the next update.
function E.reset()
	active = nil
end

local function load(shared)
	active = {}
	local ctl = shared and shared.effectCtl or {}
	industries.load(ctl.industries)
	vehicles.load(ctl.vehicles)
	local n = 0
	for _, e in ipairs(util.list(shared and shared.effects)) do
		if type(e) == "table" and e.key and TYPES[e.type] then
			-- a save from before the controller: the industry boost kept its base in the effect itself
			if e.type == "industryBoost" and e.baseline and industries.entries[e.entity] == nil then
				industries.entries[e.entity] = { baseline = e.baseline, factor = e.factor, target = e.target or e.baseline * e.factor,
					applied = e.applied }
			end
			e.baseline, e.target, e.applied, e.settle, e.rebases = nil, nil, nil, nil, nil
			active[e.key] = e
			n = n + 1
		end
	end
	if n > 0 then util.log(string.format("effects: %d active effect(s) restored from the save", n)) end
end

-- Every second from guiUpdate: expiring and maintaining the effects.
function E.update(now, shared)
	if active == nil then load(shared) end
	lastNow = now
	local changed = false
	for key, e in pairs(active) do
		if now >= e.untilTime then
			local t = TYPES[e.type]
			local ok, err = true, nil
			if t.revert then ok, err = pcall(t.revert, e) end
			util.log(string.format("effect %s ended%s", key, ok and "" or (" (revert failed: " .. tostring(err) .. ")")))
			active[key] = nil
			changed = true
		end
	end
	local ok, res = pcall(syncControllers)
	if not ok then util.log("effects: controllers failed: " .. tostring(res)) end
	if changed or (ok and res) then persist() end
end

local function keyOf(c)
	if type(c.target) == "string" and c.target ~= "" then return tostring(c.effect) .. ":" .. c.target end
	return tostring(c.effect) .. ":" .. tostring(tonumber(c.entity))
end

-- An effect that couldn't be confirmed (commands.lua then refunds the points): undo and remove it.
function E.abort(c)
	local key = keyOf(c)
	local e = active and active[key]
	if not e then return end
	local t = TYPES[e.type]
	if t.revert then pcall(t.revert, e) end
	active[key] = nil
	if t.queue then -- the next ones in the queue start when the interrupted one was due to start
		local from = math.max(e.startTime or 0, lastNow or 0)
		shiftQueue(e, e.untilTime, from - e.untilTime)
	end
	pcall(syncControllers)
	persist()
	util.log("effect " .. key .. " aborted")
end

-- "123,456" -> { 123, 456 } (existing industries only)
local function parseIndustries(s)
	local out = {}
	if type(s) ~= "string" then return out end
	for x in s:gmatch("[^,%s]+") do
		local id = tonumber(x)
		if id and util.comp(id, ct.INDUSTRY) then out[#out + 1] = id end
	end
	return out
end

local function union(a, b)
	local seen, out = {}, {}
	for _, l in ipairs({ a, b }) do
		for _, x in ipairs(l) do
			if not seen[x] then seen[x] = true; out[#out + 1] = x end
		end
	end
	return out
end

-- Group (industries / speed): target check and verify once the controller has set it.
local function startGroup(t, e)
	if t.group == "industry" or t.group == "freeze" then
		for _, id in ipairs(targets(e)) do
			if not util.comp(id, ct.INDUSTRY) then return nil, "not an industry" end
		end
		if #targets(e) == 0 then return nil, "no industries" end
		if t.group == "freeze" then
			return function()
				for _, id in ipairs(targets(e)) do
					if freezes[id] == nil then return false end
				end
				return true
			end
		end
		return function()
			for _, id in ipairs(targets(e)) do
				if not industries.reached(id) then return false end
			end
			return true
		end
	end
	local vs = util.lineVehicles(e.entity)
	if #vs == 0 then return nil, "line has no vehicles" end
	return function()
		for _, v in ipairs(vs) do
			if util.comp(v, ct.TRANSPORT_VEHICLE) and not vehicles.reached(v) then return false end
		end
		return true
	end
end

local function describeGroup(t, e)
	if t.group == "freeze" then
		local st, n = freezes[targets(e)[1]], 0
		for _ in pairs(st and st.stocks or {}) do n = n + 1 end
		return string.format("keeps x%s of new output on %d industr(y/ies), %d output stock(s) on the first",
			fmt(e.factor), #targets(e), n)
	end
	local ids = t.group == "industry" and targets(e) or util.lineVehicles(e.entity)
	local ctl = t.group == "industry" and industries or vehicles
	return string.format("x%s on %d object(s), e.g. %s", fmt(e.factor), #ids, ids[1] and ctl.describe(ids[1]) or "-")
end

-- The "effect" command from the queue: { effect, entity, months, factor, user } (+ entities = "1,2,3", target = key
-- for multi effects). Returns verify or nil, error (like the command kinds in commands.lua), and at the end info
-- { until = end date in the game calendar } for the chat message.
function E.start(c, now)
	if active == nil then return nil, "effects not loaded yet" end
	if now ~= nil then lastNow = now end
	local t = TYPES[c.effect]
	local entity, months = tonumber(c.entity), tonumber(c.months)
	if not t or not entity or not months or now == nil then return nil, "bad arguments" end
	if t.multi and (type(c.target) ~= "string" or c.target == "") then return nil, "bad arguments" end
	months = math.max(MIN_MONTHS, math.min(MAX_MONTHS, months))
	local factor = nil -- only effects with a multiplier (range)
	if t.range then
		factor = tonumber(c.factor)
		if not factor then return nil, "bad arguments" end
		factor = math.max(t.range[1], math.min(t.range[2], factor))
	end
	local list = t.multi and parseIndustries(c.entities) or nil
	if t.multi and #list == 0 then return nil, "no industries" end
	local key = keyOf(c)

	local existing = active[key]
	if existing then -- extension; strength only goes up (e.g. after the multiplier changes in the settings)
		local oldUntil = existing.untilTime
		existing.untilTime = math.max(existing.untilTime, now) + months * monthLength()
		if t.queue then shiftQueue(existing, oldUntil, existing.untilTime - oldUntil) end -- the next ones in the queue later
		local stronger = factor and existing.factor and math.abs(factor - 1) > math.abs(existing.factor - 1) + 1e-6
		if stronger then
			existing.factor = factor
			if t.upgrade then t.upgrade(existing) end
		end
		if list then existing.entities = union(existing.entities or {}, list) end -- the victim's new industries too
		if type(c.user) == "string" and c.user ~= "" then existing.user = c.user end
		if t.group then syncControllers() end
		persist()
		util.log(string.format("effect %s extended by %d months%s", key, months, stronger and (", now x" .. fmt(factor)) or ""))
		return function() return true end, nil, { ["until"] = util.date(existing.untilTime) }
	end

	-- queue (fuel): another effect from this queue is still running -> this one starts after it ends
	local startAt = now
	if t.queue then
		for _, o in ipairs(queueMates({ type = c.effect, entity = entity })) do startAt = math.max(startAt, o.untilTime) end
	end
	local e = { key = key, type = c.effect, entity = entity, entities = list, untilTime = startAt + months * monthLength(),
		startTime = startAt, factor = factor, user = type(c.user) == "string" and c.user or nil }
	local verify, err
	if t.group then
		verify, err = startGroup(t, e)
		if verify then
			active[key] = e
			if waiting(e, now) then
				verify = function() return true end -- we set nothing yet, it starts on its own
				persist()
				util.log(string.format("effect %s queued for %d months, starts in %.1f months", key, months, (startAt - now) / monthLength()))
				return verify, nil, { ["until"] = util.date(e.untilTime) }
			end
			syncControllers()
		end
	else
		verify, err = t.apply(e)
		if verify then active[key] = e end
	end
	if not verify then return nil, err end
	persist()
	local desc = t.group and describeGroup(t, e) or (t.describe and t.describe(e))
	util.log(string.format("effect %s started for %d months%s", key, months, desc and (": " .. desc) or ""))
	return verify, nil, { ["until"] = util.date(e.untilTime) }
end

-- Active effects for the dynamic export (the website shows them next to the town, industry and line);
-- an effect on many industries (tax raid) is a separate entry for each of them.
function E.list()
	local out = {}
	for _, e in pairs(active or {}) do
		for _, id in ipairs(targets(e)) do
			out[#out + 1] = {
				type = e.type, entity = id, user = e.user, factor = e.factor,
				startTime = e.startTime, untilTime = e.untilTime, ["until"] = util.date(e.untilTime),
			}
		end
	end
	table.sort(out, function(a, b) return a.untilTime < b.untilTime end)
	return out
end

return E
