-- The look of the "Twitch" notification (type: twitch_notification.res.lua). The texts come ready from Tf3Bridge
-- (the notify command in commands.lua): params = { title, text, entity = { entity, revision } | nil }.
-- Clicking moves the camera to the entity (town, industry, line) if it still exists.
local notification_util = ug_require "::/game_mechanics/notifications/notification_util.tl"

local ICON = "estiv0_tf3export::/tf3export/icons/twitch.tga"

local notification = {}

notification.useDataState = function(p)
	p = p or {}
	local target = p.entity and { p.entity } or nil
	return {
		title = p.title or "Twitch",
		description = p.text or "",
		icon = ICON,
		wouldClick = target and notification_util.makeDefaultWouldClick(target) or nil,
		onClick = target and notification_util.makeDefaultOnClick(target) or nil,
	}
end

-- .script.lua files have to define a global data() returning a table of functions (the game calls
-- "<file>.script@useDataState" through util.useFn); "return {...}" only works in .tl files.
function data()
	return notification
end
