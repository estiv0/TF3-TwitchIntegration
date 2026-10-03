# Buduje Tf3Bridge.exe do middle_app\dist\ i tworzy skrót na pulpicie.
# Exe jest self-contained single-file (win-x64): nie wymaga zainstalowanego .NET, obok leży tylko
# appsettings.json. Można go więc przekazać komuś innemu (folder moda sam znajdzie w Steam).
# Exe szuka web\data w górę od swojego folderu, więc u Ciebie dist\ musi zostać w repozytorium;
# poza repozytorium publikuje do output\ obok siebie.
param([switch]$NoShortcut)

$ErrorActionPreference = "Stop"
$dotnetRoot = Join-Path $env:USERPROFILE ".dotnet"
$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { "dotnet" } else { Join-Path $dotnetRoot "dotnet.exe" }
$env:DOTNET_ROOT = $dotnetRoot

$project = Join-Path $PSScriptRoot "src\Tf3Bridge\Tf3Bridge.csproj"
$dist = Join-Path $PSScriptRoot "dist"

$running = Get-Process Tf3Bridge -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dist*" }
if ($running) { throw "Tf3Bridge z dist\ jest uruchomiony - zamknij go przed publikacją." }

# stare DLL-ki z publikacji framework-dependent nie mogą zostać obok single-file
if (Test-Path $dist) { Remove-Item (Join-Path $dist "*") -Recurse -Force }

& $dotnet publish $project -c Release -o $dist --nologo -v q `
    -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw "Publikacja nie powiodła się." }

$exe = Join-Path $dist "Tf3Bridge.exe"
Write-Host "Gotowe: $exe"

if (-not $NoShortcut) {
    $lnk = Join-Path ([Environment]::GetFolderPath("Desktop")) "Tf3Bridge.lnk"
    $shell = New-Object -ComObject WScript.Shell
    $s = $shell.CreateShortcut($lnk)
    $s.TargetPath = $exe
    $s.WorkingDirectory = $dist
    $s.Description = "TF3 -> strona: most danych"
    $s.Save()
    Write-Host "Skrót: $lnk"
}
