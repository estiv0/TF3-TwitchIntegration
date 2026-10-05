# Pełne wydanie: publikuje Tf3Bridge (publish.ps1), pakuje middle_app\dist\ do dist\Tf3Bridge.zip
# w katalogu głównym repozytorium i wgrywa stronę na Azure (deploy-web.ps1).
#   .\infra\make_all.ps1                       # wszystko, strona na SWA (token zapamiętany)
#   .\infra\make_all.ps1 -Target storage       # strona do kontenera $web zamiast SWA
#   -NoShortcut  bez skrótu na pulpicie,  -SkipDeploy  tylko exe + zip
param(
    [ValidateSet("swa", "storage")][string]$Target = "swa",
    [switch]$NoShortcut,
    [switch]$SkipDeploy
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# ---------------------------------------------------------------- publish
& (Join-Path $PSScriptRoot "publish.ps1") -NoShortcut:$NoShortcut

# ---------------------------------------------------------------- zip
# W zipie folder Tf3Bridge\ z exe i appsettings.json - po rozpakowaniu poza repozytorium exe sam
# znajdzie mod w Steam i publikuje do output\ obok siebie.
$dist = Join-Path $repo "middle_app\dist"
$outDir = Join-Path $repo "dist"
$zip = Join-Path $outDir "Tf3Bridge.zip"
$stage = Join-Path ([IO.Path]::GetTempPath()) "tf3bridge-$([Guid]::NewGuid().ToString('N'))"
try {
    Copy-Item $dist (Join-Path $stage "Tf3Bridge") -Recurse
    New-Item -ItemType Directory -Force $outDir | Out-Null
    if (Test-Path $zip) { Remove-Item $zip }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    Write-Host "Paczka: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------- strona
if ($SkipDeploy) { return }
& (Join-Path $PSScriptRoot "deploy-web.ps1") -Target $Target
