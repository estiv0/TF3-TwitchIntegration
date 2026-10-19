# Wgrywa stronę (folder web\) na Azure. Dwa cele:
#
#   swa      (domyślny) Azure Static Web Apps: własna domena + darmowy HTTPS.
#            Token: portal -> Static Web App -> Overview -> Manage deployment token.
#            Wysyła narzędziem "swa" (Node.js: winget install OpenJS.NodeJS.LTS), zawsze całą stronę.
#   storage  "Static website" konta Storage (kontener $web), bez Node i bez Azure CLI.
#            Token: konto Storage -> Containers -> $web -> ... -> Generate SAS, Create + Write,
#            HTTPS only, kopiujesz "Blob SAS URL". Wysyła tylko pliki zmienione od ostatniego razu.
#
#   .\infra\deploy-web.ps1 -DeployToken "<token>"          # SWA, pierwszy raz
#   .\infra\deploy-web.ps1                                 # SWA, kolejne razy (token zapamiętany)
#   .\infra\deploy-web.ps1 -Target storage -SasUrl "<Blob SAS URL `$web>"   # Storage, pierwszy raz
#   .\infra\deploy-web.ps1 -Target storage                 # Storage, kolejne razy
#   -DryRun  tylko pokaż listę plików,  -Force  (storage) wyślij wszystko, nie tylko zmienione
#
# Tokeny są zapamiętywane w %LOCALAPPDATA%\Tf3Bridge, zaszyfrowane dla Twojego konta Windows (DPAPI).
param(
    [ValidateSet("swa", "storage")][string]$Target = "swa",
    [string]$DeployToken,
    [string]$SasUrl,
    [switch]$Force,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$webDir = (Resolve-Path (Join-Path $PSScriptRoot "..\web")).Path
$stateDir = Join-Path $env:LOCALAPPDATA "Tf3Bridge"
$sasFile = Join-Path $stateDir "web-deploy-sas.txt"
$swaTokenFile = Join-Path $stateDir "web-deploy-swa-token.txt"
$manifestFile = Join-Path $stateDir "web-deploy-manifest.json"

# Nie wysyłamy danych z gry (idą przez Tf3Bridge do kontenera "live"), źródeł ikon ani plików roboczych.
$excludeDirs = @("data", "img_res")
$excludeFiles = @("serve.ps1", "README.md", "site.example.json")

$contentTypes = @{
    ".html" = "text/html; charset=utf-8"
    ".js"   = "text/javascript; charset=utf-8"
    ".css"  = "text/css; charset=utf-8"
    ".json" = "application/json; charset=utf-8"
    ".png"  = "image/png"
    ".jpg"  = "image/jpeg"
    ".svg"  = "image/svg+xml"
    ".ico"  = "image/x-icon"
    ".webp" = "image/webp"
    ".woff2" = "font/woff2"
    ".txt"  = "text/plain; charset=utf-8"
}

# ---------------------------------------------------------------- sekrety (DPAPI)
function Save-Secret([string]$path, [string]$value) {
    New-Item -ItemType Directory -Force $stateDir | Out-Null
    # ConvertFrom-SecureString bez klucza = DPAPI: odczyta to tylko to konto Windows na tym komputerze
    ConvertTo-SecureString $value -AsPlainText -Force | ConvertFrom-SecureString | Set-Content $path
}

function Read-Secret([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    $secure = Get-Content $path | ConvertTo-SecureString
    [System.Net.NetworkCredential]::new("", $secure).Password
}

# ---------------------------------------------------------------- lista plików
# Ikonki z gry nie są w repozytorium (prawa Urban Games) - generuje je tools\extract_game_icons.py.
foreach ($dir in "vehicles", "stations", "resources") {
    if (-not (Get-ChildItem (Join-Path $webDir "img\$dir") -Filter *.png -ErrorAction SilentlyContinue)) {
        throw "Brak ikonek w web\img\$dir. Uruchom najpierw: python tools\extract_game_icons.py `"<folder gry>`""
    }
}

$files = Get-ChildItem $webDir -Recurse -File | Where-Object {
    $rel = $_.FullName.Substring($webDir.Length + 1)
    $top = $rel.Split([IO.Path]::DirectorySeparatorChar)[0]
    -not ($excludeDirs -contains $top) -and -not ($excludeFiles -contains $rel) -and $_.Extension -ne ".tmp"
} | ForEach-Object {
    $rel = $_.FullName.Substring($webDir.Length + 1).Replace('\', '/')
    [pscustomobject]@{
        Path = $rel
        File = $_.FullName
        Hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        Type = $($t = $contentTypes[$_.Extension.ToLowerInvariant()]; if ($t) { $t } else { "application/octet-stream" })
    }
}

Write-Host "Strona: $webDir ($($files.Count) plików), cel: $Target"

# ================================================================ Static Web Apps
if ($Target -eq "swa") {
    if ($DeployToken) {
        Save-Secret $swaTokenFile $DeployToken.Trim()
    } else {
        $DeployToken = Read-Secret $swaTokenFile
    }
    if ($DryRun) {
        $files | Format-Table Path, Type -AutoSize | Out-String | Write-Host
        return
    }
    if (-not $DeployToken) {
        throw "Brak tokenu. Uruchom raz z -DeployToken `"<token>`" (portal: Static Web App -> Overview -> Manage deployment token)."
    }

    # Node mógł być właśnie zainstalowany i nie być jeszcze w PATH tego okna
    $npx = (Get-Command npx.cmd -ErrorAction SilentlyContinue).Source
    if (-not $npx -and (Test-Path "$env:ProgramFiles\nodejs\npx.cmd")) {
        $npx = "$env:ProgramFiles\nodejs\npx.cmd"
        $env:Path = "$env:ProgramFiles\nodejs;$env:Path"
    }
    if (-not $npx) { throw "Brak Node.js. Zainstaluj: winget install OpenJS.NodeJS.LTS, potem otwórz nowe okno." }

    # swa wysyła cały folder, więc składamy kopię tylko z plikami strony
    $stage = Join-Path ([IO.Path]::GetTempPath()) "tf3-web-deploy"
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    foreach ($f in $files) {
        $dest = Join-Path $stage $f.Path
        New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
        Copy-Item $f.File $dest
    }

    Write-Host "Wysyłam przez Static Web Apps CLI (pierwszy raz pobiera narzędzie, chwilę to trwa)..."
    # token przez zmienną środowiskową, a nie w linii poleceń (nie widać go na liście procesów)
    $env:SWA_CLI_DEPLOYMENT_TOKEN = $DeployToken
    try {
        & $npx --yes "@azure/static-web-apps-cli@latest" deploy $stage --env production
        if ($LASTEXITCODE -ne 0) { throw "swa deploy zakończył się błędem ($LASTEXITCODE). Sprawdź token (Manage deployment token -> Reset, jeśli trzeba)." }
    }
    finally {
        Remove-Item Env:SWA_CLI_DEPLOYMENT_TOKEN -ErrorAction SilentlyContinue
        Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host ""
    Write-Host "Gotowe. Adres: portal -> Static Web App -> Overview -> URL (albo Twoja domena). Odśwież przez Ctrl+F5." -ForegroundColor Green
    return
}

# ================================================================ Storage ($web)
if ($SasUrl) {
    if ($SasUrl -notmatch '^https://[^/]+/\$web\?.*sig=') {
        throw "To nie wygląda na SAS URL kontenera `$web (oczekiwane: https://<konto>.blob.core.windows.net/`$web?sv=...&sig=...)."
    }
    Save-Secret $sasFile $SasUrl
    Remove-Item $manifestFile -ErrorAction SilentlyContinue  # nowy cel = wyślij wszystko
} else {
    $SasUrl = Read-Secret $sasFile
}
if (-not $SasUrl -and -not $DryRun) {
    throw "Brak tokenu. Uruchom raz z -Target storage -SasUrl `"<Blob SAS URL kontenera `$web>`"."
}

$previous = @{}
if (-not $Force -and (Test-Path $manifestFile)) {
    (Get-Content $manifestFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $previous[$_.Name] = $_.Value }
}
$changed = @($files | Where-Object { $previous[$_.Path] -ne $_.Hash })
$removed = @($previous.Keys | Where-Object { $_ -notin $files.Path })

Write-Host "Do wysłania: $($changed.Count)"
if ($DryRun) {
    $changed | Format-Table Path, Type -AutoSize | Out-String | Write-Host
    return
}
if ($changed.Count -eq 0) {
    Write-Host "Nic się nie zmieniło. (-Force wysyła wszystko)" -ForegroundColor Green
}

$base, $query = $SasUrl.Split('?', 2)
$client = [System.Net.Http.HttpClient]::new()
$sent = @{} + $previous
try {
    foreach ($f in $changed) {
        $segments = $f.Path.Split('/') | ForEach-Object { [Uri]::EscapeDataString($_) }
        $uri = "$base/$($segments -join '/')?$query"

        # HTML, JS i CSS bez cache, żeby nowa wersja była widoczna od razu; obrazki mogą poleżeć dobę
        $cache = if ($f.Type -like "image/*") { "public, max-age=86400" } else { "no-cache" }

        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Put, $uri)
        $request.Content = [System.Net.Http.ByteArrayContent]::new([IO.File]::ReadAllBytes($f.File))
        $request.Content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse($f.Type)
        $request.Headers.Add("x-ms-blob-type", "BlockBlob")
        $request.Headers.Add("x-ms-version", "2021-08-06")
        $request.Headers.Add("x-ms-blob-content-type", $f.Type)
        $request.Headers.Add("x-ms-blob-cache-control", $cache)

        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            $hint = switch ([int]$response.StatusCode) {
                403 { " (token SAS nieprawidłowy, wygasł albo bez prawa Create/Write)" }
                404 { " (nie ma kontenera `$web - czy Static website jest włączone?)" }
                default { "" }
            }
            throw "Błąd przy $($f.Path): $([int]$response.StatusCode) $($response.ReasonPhrase)$hint"
        }
        $sent[$f.Path] = $f.Hash
        Write-Host "  ↑ $($f.Path)"
    }
}
finally {
    $client.Dispose()
    # zapisujemy także po błędzie: następne uruchomienie dośle tylko to, czego brakuje
    New-Item -ItemType Directory -Force $stateDir | Out-Null
    $sent | ConvertTo-Json | Set-Content $manifestFile
}

if ($removed.Count) {
    Write-Host ""
    Write-Host "Tych plików nie ma już lokalnie, ale zostały na Azure (usuń w portalu, jeśli trzeba):" -ForegroundColor Yellow
    $removed | ForEach-Object { Write-Host "  $_" }
    $removed | ForEach-Object { $sent.Remove($_) }
    $sent | ConvertTo-Json | Set-Content $manifestFile
}

$site = if ($base -match '^https://([^.]+)\.blob\.') { "konto $($Matches[1]), Static website (Primary endpoint w portalu)" } else { $base }
Write-Host ""
Write-Host "Gotowe: $site. Odśwież stronę przez Ctrl+F5." -ForegroundColor Green
