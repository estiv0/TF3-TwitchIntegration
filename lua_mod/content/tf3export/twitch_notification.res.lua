-- The in-game notification type for channel points rewards (docs/custom-notifications.md).
-- The game looks it up by the type name with ".script" replaced by ".res" (api.res.genericRep.find).
function data()
	return {
		type = "notification",
		data = {
			guiType = "Opportunity", -- purple accent (notification_colors: Opportunity = grape), like Twitch
			label = "Twitch",        -- name in the notification log filter
			initiallyIgnoredType = false,
		},
	}
end
