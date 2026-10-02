-- Deskryptor game scriptu (wzór: base/content/game_mechanics/achievements/achievements.gs.lua).
-- Gra rejestruje pliki *.gs.lua z content/ jako zasoby gameScriptRep.
-- update/handleEvent: wątki symulacji; guiUpdate: wątek GUI (jeden).
function data()
	return {
		updateScript = {
			fileName = "tf3export.script@update",
		},
		handleEventScript = {
			fileName = "tf3export.script@handleEvent",
		},
		guiUpdateScript = {
			fileName = "tf3export.script@guiUpdate",
		},
	}
end
