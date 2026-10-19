# Local website preview: serves the web/ folder at http://localhost:5080
# The .NET SDK is in the user folder (installed by Rider), so the dotnet-serve tool
# needs DOTNET_ROOT - we set it for this process only.
param([int]$Port = 5080)

$dotnetRoot = Join-Path $env:USERPROFILE ".dotnet"
$serve = Join-Path $dotnetRoot "tools\dotnet-serve.exe"
if (-not (Test-Path $serve)) {
    Write-Host "Installing dotnet-serve..."
    & (Join-Path $dotnetRoot "dotnet.exe") tool install -g dotnet-serve
}

$env:DOTNET_ROOT = $dotnetRoot
Write-Host "Website: http://localhost:$Port  (Ctrl+C stops it)"
& $serve -d $PSScriptRoot -p $Port -h "Cache-Control: no-store"
