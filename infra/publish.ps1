# Builds Tf3Bridge.exe into middle_app\dist\ and creates a desktop shortcut.
# The exe is self-contained single-file (win-x64): it needs no installed .NET, next to it there's only
# appsettings.json (Development and Local settings are not published). So it can be given to someone else (it finds the mod folder in Steam itself).
# The exe looks for web\data upwards from its folder, so in your checkout dist\ has to stay in the repository;
# outside the repository it publishes to output\ next to itself and serves the website built into it.
param([switch]$NoShortcut)

$ErrorActionPreference = "Stop"
$dotnetRoot = Join-Path $env:USERPROFILE ".dotnet"
$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { "dotnet" } else { Join-Path $dotnetRoot "dotnet.exe" }
$env:DOTNET_ROOT = $dotnetRoot

$appDir = (Resolve-Path (Join-Path $PSScriptRoot "..\middle_app")).Path
$project = Join-Path $appDir "src\Tf3Bridge\Tf3Bridge.csproj"
$dist = Join-Path $appDir "dist"

$running = Get-Process Tf3Bridge -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dist*" }
if ($running) { throw "Tf3Bridge from dist\ is running - close it before publishing." }

# old DLLs from a framework-dependent publish must not stay next to the single file
if (Test-Path $dist) { Remove-Item (Join-Path $dist "*") -Recurse -Force }

& $dotnet publish $project -c Release -o $dist --nologo -v q `
    -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw "Publishing failed." }

$exe = Join-Path $dist "Tf3Bridge.exe"
Write-Host "Done: $exe"

if (-not $NoShortcut) {
    $lnk = Join-Path ([Environment]::GetFolderPath("Desktop")) "Tf3Bridge.lnk"
    $shell = New-Object -ComObject WScript.Shell
    $s = $shell.CreateShortcut($lnk)
    $s.TargetPath = $exe
    $s.WorkingDirectory = $dist
    $s.Description = "TF3 -> website: data bridge"
    $s.Save()
    Write-Host "Shortcut: $lnk"
}
