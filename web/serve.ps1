# Lokalny podgląd strony: serwuje folder web/ na http://localhost:5080
# .NET SDK jest w katalogu użytkownika (instalacja z Ridera), więc narzędzie dotnet-serve
# potrzebuje DOTNET_ROOT - ustawiamy go tylko dla tego procesu.
param([int]$Port = 5080)

$dotnetRoot = Join-Path $env:USERPROFILE ".dotnet"
$serve = Join-Path $dotnetRoot "tools\dotnet-serve.exe"
if (-not (Test-Path $serve)) {
    Write-Host "Instaluję dotnet-serve..."
    & (Join-Path $dotnetRoot "dotnet.exe") tool install -g dotnet-serve
}

$env:DOTNET_ROOT = $dotnetRoot
Write-Host "Strona: http://localhost:$Port  (Ctrl+C kończy)"
& $serve -d $PSScriptRoot -p $Port -h "Cache-Control: no-store"
