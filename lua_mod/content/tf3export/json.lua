-- Minimalny, poprawny enkoder JSON.
-- Zasady:
--   * wszystko trafia do bufora i jest łączone raz przez table.concat (liniowo, nie kwadratowo)
--   * stringi są escapowane (cudzysłowy, backslash, znaki sterujące)
--   * NaN / inf / nil / userdata / funkcje -> null
--   * pusta tabela -> [] (jeśli potrzebujesz {}, użyj json.EMPTY_OBJECT)
--   * tabela z kluczami 1..n bez dziur -> tablica, wszystko inne -> obiekt

local json = {}

json.EMPTY_OBJECT = setmetatable({}, { __tostring = function() return "{}" end })

local MAX_DEPTH = 32

local escapes = {
	['"'] = '\\"', ['\\'] = '\\\\',
	['\b'] = '\\b', ['\f'] = '\\f', ['\n'] = '\\n', ['\r'] = '\\r', ['\t'] = '\\t',
}

local function escapeString(s)
	local escaped = s:gsub('[%c"\\]', function(c)
		return escapes[c] or string.format("\\u%04x", c:byte())
	end)
	return '"' .. escaped .. '"'
end

local function isArray(t)
	local n = #t
	if n == 0 then
		return next(t) == nil
	end
	local count = 0
	for _ in pairs(t) do
		count = count + 1
		if count > n then return false end
	end
	return count == n
end

local encodeValue

local function encodeTable(t, buf, depth)
	if t == json.EMPTY_OBJECT then
		buf[#buf + 1] = "{}"
		return
	end
	if isArray(t) then
		buf[#buf + 1] = "["
		for i = 1, #t do
			if i > 1 then buf[#buf + 1] = "," end
			encodeValue(t[i], buf, depth + 1)
		end
		buf[#buf + 1] = "]"
	else
		buf[#buf + 1] = "{"
		local first = true
		for k, v in pairs(t) do
			if not first then buf[#buf + 1] = "," end
			first = false
			buf[#buf + 1] = escapeString(tostring(k))
			buf[#buf + 1] = ":"
			encodeValue(v, buf, depth + 1)
		end
		buf[#buf + 1] = "}"
	end
end

encodeValue = function(v, buf, depth)
	local t = type(v)
	if t == "nil" then
		buf[#buf + 1] = "null"
	elseif t == "boolean" then
		buf[#buf + 1] = v and "true" or "false"
	elseif t == "number" then
		if v ~= v or v == math.huge or v == -math.huge then
			buf[#buf + 1] = "null"
		elseif v == math.floor(v) and math.abs(v) < 2^31 then
			buf[#buf + 1] = string.format("%d", v)
		elseif v == math.floor(v) and math.abs(v) < 2^53 then
			-- %d w Lua 5.2 na Windows bierze tylko 32-bitowy long ("not a number in proper range"),
			-- a %.0f wypisuje liczby całkowite do 2^53 dokładnie
			buf[#buf + 1] = string.format("%.0f", v)
		else
			-- %.14g zachowuje precyzję i daje poprawny JSON także dla 1.5e-05
			buf[#buf + 1] = string.format("%.14g", v)
		end
	elseif t == "string" then
		buf[#buf + 1] = escapeString(v)
	elseif t == "table" then
		if depth >= MAX_DEPTH then
			buf[#buf + 1] = "null"
		else
			encodeTable(v, buf, depth)
		end
	else
		-- userdata z API gry nie serializuje się automatycznie:
		-- kolektory muszą przepisać potrzebne pola do zwykłych tabel
		buf[#buf + 1] = "null"
	end
end

function json.encode(value)
	local buf = {}
	encodeValue(value, buf, 0)
	return table.concat(buf)
end

return json
