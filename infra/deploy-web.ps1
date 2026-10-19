# Uploads the website (the web\ folder) to Azure. Two targets:
#
#   swa      (default) Azure Static Web Apps: custom domain + free HTTPS.
#            Token: portal -> Static Web App -> Overview -> Manage deployment token.
#            Uploads with the "swa" tool (Node.js: winget install OpenJS.NodeJS.LTS), always the whole site.
#   storage  Storage account "Static website" (the $web container), no Node and no Azure CLI.
#            Token: Storage account -> Containers -> $web -> ... -> Generate SAS, Create + Write,
#            HTTPS only, copy the "Blob SAS URL". Uploads only the files changed since the last time.
#
#   .\infra\deploy-web.ps1 -DeployToken "<token>"          # SWA, the first time
#   .\infra\deploy-web.ps1                                 # SWA, next times (token remembered)
#   .\infra\deploy-web.ps1 -Target storage -SasUrl "<Blob SAS URL `$web>"   # Storage, the first time
#   .\infra\deploy-web.ps1 -Target storage                 # Storage, next times
#   -DryRun  only show the file list,  -Force  (storage) upload everything, not just the changed files
#
# Tokens are remembered in %LOCALAPPDATA%\Tf3Bridge, encrypted for your Windows account (DPAPI).
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

# We don't upload game data (it goes through Tf3Bridge to the "live" container), icon sources or working files.
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

# ---------------------------------------------------------------- secrets (DPAPI)
function Save-Secret([string]$path, [string]$value) {
    New-Item -ItemType Directory -Force $stateDir | Out-Null
    # ConvertFrom-SecureString without a key = DPAPI: only this Windows account on this computer can read it
    ConvertTo-SecureString $value -AsPlainText -Force | ConvertFrom-SecureString | Set-Content $path
}

function Read-Secret([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    $secure = Get-Content $path | ConvertTo-SecureString
    [System.Net.NetworkCredential]::new("", $secure).Password
}

# ---------------------------------------------------------------- file list
# The game icons aren't in the repository (Urban Games' rights) - tools\extract_game_icons.py generates them.
foreach ($dir in "vehicles", "stations", "resources") {
    if (-not (Get-ChildItem (Join-Path $webDir "img\$dir") -Filter *.png -ErrorAction SilentlyContinue)) {
        throw "No icons in web\img\$dir. Run first: python tools\extract_game_icons.py `"<game folder>`""
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

Write-Host "Website: $webDir ($($files.Count) files), target: $Target"

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
        throw "No token. Run once with -DeployToken `"<token>`" (portal: Static Web App -> Overview -> Manage deployment token)."
    }

    # Node may have just been installed and not be on this window's PATH yet
    $npx = (Get-Command npx.cmd -ErrorAction SilentlyContinue).Source
    if (-not $npx -and (Test-Path "$env:ProgramFiles\nodejs\npx.cmd")) {
        $npx = "$env:ProgramFiles\nodejs\npx.cmd"
        $env:Path = "$env:ProgramFiles\nodejs;$env:Path"
    }
    if (-not $npx) { throw "Node.js not found. Install it: winget install OpenJS.NodeJS.LTS, then open a new window." }

    # swa uploads the whole folder, so we assemble a copy with only the website files
    $stage = Join-Path ([IO.Path]::GetTempPath()) "tf3-web-deploy"
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    foreach ($f in $files) {
        $dest = Join-Path $stage $f.Path
        New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
        Copy-Item $f.File $dest
    }

    Write-Host "Uploading with the Static Web Apps CLI (the first time it downloads the tool, which takes a moment)..."
    # the token through an environment variable, not the command line (it isn't visible in the process list)
    $env:SWA_CLI_DEPLOYMENT_TOKEN = $DeployToken
    try {
        & $npx --yes "@azure/static-web-apps-cli@latest" deploy $stage --env production
        if ($LASTEXITCODE -ne 0) { throw "swa deploy failed ($LASTEXITCODE). Check the token (Manage deployment token -> Reset, if needed)." }
    }
    finally {
        Remove-Item Env:SWA_CLI_DEPLOYMENT_TOKEN -ErrorAction SilentlyContinue
        Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host ""
    Write-Host "Done. Address: portal -> Static Web App -> Overview -> URL (or your domain). Refresh with Ctrl+F5." -ForegroundColor Green
    return
}

# ================================================================ Storage ($web)
if ($SasUrl) {
    if ($SasUrl -notmatch '^https://[^/]+/\$web\?.*sig=') {
        throw "This doesn't look like a SAS URL of the `$web container (expected: https://<account>.blob.core.windows.net/`$web?sv=...&sig=...)."
    }
    Save-Secret $sasFile $SasUrl
    Remove-Item $manifestFile -ErrorAction SilentlyContinue  # a new target = upload everything
} else {
    $SasUrl = Read-Secret $sasFile
}
if (-not $SasUrl -and -not $DryRun) {
    throw "No token. Run once with -Target storage -SasUrl `"<Blob SAS URL of the `$web container>`"."
}

$previous = @{}
if (-not $Force -and (Test-Path $manifestFile)) {
    (Get-Content $manifestFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $previous[$_.Name] = $_.Value }
}
$changed = @($files | Where-Object { $previous[$_.Path] -ne $_.Hash })
$removed = @($previous.Keys | Where-Object { $_ -notin $files.Path })

Write-Host "To upload: $($changed.Count)"
if ($DryRun) {
    $changed | Format-Table Path, Type -AutoSize | Out-String | Write-Host
    return
}
if ($changed.Count -eq 0) {
    Write-Host "Nothing has changed. (-Force uploads everything)" -ForegroundColor Green
}

$base, $query = $SasUrl.Split('?', 2)
$client = [System.Net.Http.HttpClient]::new()
$sent = @{} + $previous
try {
    foreach ($f in $changed) {
        $segments = $f.Path.Split('/') | ForEach-Object { [Uri]::EscapeDataString($_) }
        $uri = "$base/$($segments -join '/')?$query"

        # HTML, JS and CSS without caching, so a new version shows right away; images may stay cached for a day
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
                403 { " (the SAS token is invalid, expired or lacks the Create/Write right)" }
                404 { " (there's no `$web container - is Static website enabled?)" }
                default { "" }
            }
            throw "Error for $($f.Path): $([int]$response.StatusCode) $($response.ReasonPhrase)$hint"
        }
        $sent[$f.Path] = $f.Hash
        Write-Host "  ↑ $($f.Path)"
    }
}
finally {
    $client.Dispose()
    # we save also after an error: the next run uploads only what's missing
    New-Item -ItemType Directory -Force $stateDir | Out-Null
    $sent | ConvertTo-Json | Set-Content $manifestFile
}

if ($removed.Count) {
    Write-Host ""
    Write-Host "These files no longer exist locally, but stayed on Azure (delete them in the portal if needed):" -ForegroundColor Yellow
    $removed | ForEach-Object { Write-Host "  $_" }
    $removed | ForEach-Object { $sent.Remove($_) }
    $sent | ConvertTo-Json | Set-Content $manifestFile
}

$site = if ($base -match '^https://([^.]+)\.blob\.') { "account $($Matches[1]), Static website (Primary endpoint in the portal)" } else { $base }
Write-Host ""
Write-Host "Done: $site. Refresh the page with Ctrl+F5." -ForegroundColor Green
