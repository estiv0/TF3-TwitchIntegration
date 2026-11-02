-- Industries the game builds on its own near an adopted town take the town's full name as a prefix:
--   "Szczecin | estiv0 Kopalnia rudy żelaza"
-- We move the owner to the end, the same form as an adopted industry:
--   "Szczecin Kopalnia rudy żelaza | estiv0"
--
-- GUI thread, called from guiUpdate every namesEveryWallSec (tf3export.script.lua).
-- Industries that create.lua is naming right now (a viewer's new industry) are skipped,
-- otherwise our rename could land after the viewer's name and overwrite it.

local util = ug_require "util.lua"
local create = ug_require "create.lua"
local ct = util.ct

local N = {}

local MAX_NAME = 200   -- bytes, as in setName
local RETRY_SEC = 30   -- a rename that didn't show up is sent again after this many seconds

local sent = {}        -- industry id -> { name, wall } of the last rename we sent

-- Adopted towns as { prefix = "Szczecin | estiv0 ", base = "Szczecin", owner = "estiv0" }, longest prefix first
-- (so "Nowe Miasto | x" wins over "Nowe | y" if both exist).
local function adoptedTowns()
	local list = {}
	for _, id in ipairs(util.entities(ct.TOWN)) do
		local name = util.name(id)
		local base, owner = nil, nil
		if name then base, owner = name:match("^%s*(.-)%s*|%s*(.-)%s*$") end
		if base and base ~= "" and owner ~= "" and not owner:find("|", 1, true) then
			list[#list + 1] = { prefix = name .. " ", base = base, owner = owner }
		end
	end
	table.sort(list, function(a, b) return #a.prefix > #b.prefix end)
	return list
end

-- The corrected name, or nil when the name doesn't start with an adopted town's name.
function N.fixed(name, towns)
	for _, t in ipairs(towns) do
		if name:sub(1, #t.prefix) == t.prefix then
			local rest = name:sub(#t.prefix + 1):match("^%s*(.-)%s*$")
			if rest == "" or rest:find("|", 1, true) then return nil end
			local new = t.base .. " " .. rest .. " | " .. t.owner
			return #new <= MAX_NAME and new or nil
		end
	end
	return nil
end

function N.step(wall)
	local towns = adoptedTowns()
	if #towns == 0 then return end
	for _, id in ipairs(util.entities(ct.INDUSTRY)) do
		local name = util.name(id)
		local new = name and N.fixed(name, towns)
		local last = sent[id]
		if new and not create.isNaming(id, wall)
			and not (last and last.name == new and wall - last.wall < RETRY_SEC) then
			sent[id] = { name = new, wall = wall }
			api.cmd.sendCommand(api.cmd.makeEntitySetNameCmd(id, new))
			util.log(string.format("names: industry %d '%s' -> '%s'", id, name, new))
		end
	end
end

-- New session (another game loaded): entity ids no longer mean the same thing.
function N.reset()
	sent = {}
end

return N
