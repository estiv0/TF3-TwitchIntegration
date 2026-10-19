# Packs the mod into lua_mod\dist\estiv0_tf3export.zip (to unpack into ...\3493540\local\staging_area\).
# First it generates _content.json from the files in content\, so the list doesn't drift from the code.
#   -ContentOnly  only refresh _content.json, no zip
param([switch]$ContentOnly)

$ErrorActionPreference = "Stop"
$modId = (Get-Content (Join-Path $PSScriptRoot "mod.json") -Raw | ConvertFrom-Json).modId
$content = Join-Path $PSScriptRoot "content"

# ---------------------------------------------------------------- _content.json
# Paths relative to content\, with "/"; the descriptor and the main script first (as in the game's own mods).
# Images (.tga, e.g. the notification icon) too, as in the _content.json of the game's own mods.
$files = Get-ChildItem $content -Recurse -File | Where-Object { $_.Extension -in ".lua", ".tga" } |
    ForEach-Object { $_.FullName.Substring($content.Length + 1).Replace("\", "/") } |
    Sort-Object @{ e = { if ($_ -like "*.gs.lua") { 0 } elseif ($_ -like "*.script.lua") { 1 } else { 2 } } }, { $_ }
$list = ($files | ForEach-Object { "        `"$_`"" }) -join ",`n"
$json = "{`n    `"archives`": null,`n    `"files`": [`n$list`n    ]`n}`n"
$contentJson = Join-Path $PSScriptRoot "_content.json"
[IO.File]::WriteAllText($contentJson, $json, [Text.UTF8Encoding]::new($false))
Write-Host "_content.json: $($files.Count) files"
if ($ContentOnly) { return }

# ---------------------------------------------------------------- zip
# The zip has a folder named modId, and in it only what the game needs (no README, scripts or dist\).
$dist = Join-Path $PSScriptRoot "dist"
$stage = Join-Path ([IO.Path]::GetTempPath()) "tf3pack-$([Guid]::NewGuid().ToString('N'))"
$root = Join-Path $stage $modId
try {
    New-Item -ItemType Directory -Force $root | Out-Null
    foreach ($item in "mod.json", "_content.json", "_metadata", "content") {
        Copy-Item (Join-Path $PSScriptRoot $item) $root -Recurse
    }
    New-Item -ItemType Directory -Force $dist | Out-Null
    $zip = Join-Path $dist "$modId.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path $root -DestinationPath $zip
    Write-Host "Done: $zip"
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
