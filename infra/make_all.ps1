# Full release: publishes Tf3Bridge (publish.ps1), packs middle_app\dist\ into dist\Tf3Bridge.zip
# in the repository root and uploads the website to Azure (deploy-web.ps1).
#   .\infra\make_all.ps1                       # everything, the website to SWA (token remembered)
#   .\infra\make_all.ps1 -Target storage       # the website to the $web container instead of SWA
#   -NoShortcut  no desktop shortcut,  -SkipDeploy  only exe + zip
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
# The zip has a Tf3Bridge\ folder with the exe and appsettings.json - unpacked outside the repository the exe
# finds the mod in Steam itself and publishes to output\ next to itself.
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
    Write-Host "Package: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------- website
if ($SkipDeploy) { return }
& (Join-Path $PSScriptRoot "deploy-web.ps1") -Target $Target
