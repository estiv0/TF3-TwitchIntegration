-- Game script descriptor (pattern: base/content/game_mechanics/achievements/achievements.gs.lua).
-- The game registers *.gs.lua files from content/ as gameScriptRep resources.
-- update/handleEvent: simulation threads; guiUpdate: the GUI thread (one).
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
