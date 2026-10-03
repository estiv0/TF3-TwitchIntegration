# Pakuje mod do lua_mod\dist\estiv0_tf3export.zip (do rozpakowania w ...\3493540\local\mods\).
# Najpierw generuje _content.json z plików w content\, żeby lista nie rozjechała się z kodem.
#   -ContentOnly  tylko odśwież _content.json, bez zipa
param([switch]$ContentOnly)

$ErrorActionPreference = "Stop"
$modId = (Get-Content (Join-Path $PSScriptRoot "mod.json") -Raw | ConvertFrom-Json).modId
$content = Join-Path $PSScriptRoot "content"

# ---------------------------------------------------------------- _content.json
# Ścieżki względem content\, z "/"; deskryptor i główny skrypt na początku (jak w modach z gry).
$files = Get-ChildItem $content -Recurse -File -Filter *.lua |
    ForEach-Object { $_.FullName.Substring($content.Length + 1).Replace("\", "/") } |
    Sort-Object @{ e = { if ($_ -like "*.gs.lua") { 0 } elseif ($_ -like "*.script.lua") { 1 } else { 2 } } }, { $_ }
$list = ($files | ForEach-Object { "        `"$_`"" }) -join ",`n"
$json = "{`n    `"archives`": null,`n    `"files`": [`n$list`n    ]`n}`n"
$contentJson = Join-Path $PSScriptRoot "_content.json"
[IO.File]::WriteAllText($contentJson, $json, [Text.UTF8Encoding]::new($false))
Write-Host "_content.json: $($files.Count) plików"
if ($ContentOnly) { return }

# ---------------------------------------------------------------- zip
# W zipie folder o nazwie modId, a w nim tylko to, czego potrzebuje gra (bez README, skryptów, dist\).
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
    Write-Host "Gotowe: $zip"
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
