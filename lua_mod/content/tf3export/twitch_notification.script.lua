-- Wygląd powiadomienia "Twitch" (typ: twitch_notification.res.lua). Teksty są gotowe z Tf3Bridge
-- (komenda notify w commands.lua): params = { title, text, entity = { entity, revision } | nil }.
-- Kliknięcie przenosi kamerę do encji (miasto, zakład, linia), jeśli jeszcze istnieje.
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

-- Pliki .script.lua muszą definiować globalne data() zwracające tabelę funkcji (gra woła
-- "<plik>.script@useDataState" przez util.useFn); "return {...}" działa tylko w plikach .tl.
function data()
	return notification
end
