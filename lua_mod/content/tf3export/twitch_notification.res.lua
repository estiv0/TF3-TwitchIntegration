-- Typ powiadomienia w grze dla nagród za punkty kanału (docs/custom-notifications.md).
-- Gra szuka go po nazwie typu z ".script" zamienionym na ".res" (api.res.genericRep.find).
function data()
	return {
		type = "notification",
		data = {
			guiType = "Opportunity", -- fioletowy akcent (notification_colors: Opportunity = grape), jak Twitch
			label = "Twitch",        -- nazwa w filtrze dziennika powiadomień
			initiallyIgnoredType = false,
		},
	}
end
