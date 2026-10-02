# Zakłada w Azure miejsce na dane strony: konto Storage z publicznym kontenerem "live".
# Tf3Bridge wysyła tam latest/*.json tokenem SAS (tylko zapis), strona czyta anonimowo.
#
# Wymaga Azure CLI (winget install Microsoft.AzureCLI) i subskrypcji Azure.
# Skrypt można uruchamiać wielokrotnie: istniejące zasoby zostają, CORS jest ustawiany od nowa,
# a każde uruchomienie wystawia nowy token SAS (stare działają do swojej daty ważności).
#
#   .\infra\azure-setup.ps1 -Name tf3live
#   .\infra\azure-setup.ps1 -Name tf3live -SasOnly        # tylko nowy token SAS
param(
    # Nazwa konta Storage: 3-24 znaki, małe litery i cyfry, unikalna w całym Azure (jest częścią adresu).
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9]{3,24}$')][string]$Name,
    [string]$ResourceGroup = "tf3-live",
    [string]$Location = "polandcentral",
    [string]$Container = "live",
    [int]$SasYears = 1,
    [switch]$SasOnly
)

$ErrorActionPreference = "Stop"

function Az {
    # az zwraca błędy przez kod wyjścia, nie wyjątki - zamieniamy je na wyjątek
    $out = & az @args
    if ($LASTEXITCODE -ne 0) { throw "az $($args -join ' ') zakończyło się błędem ($LASTEXITCODE)" }
    $out
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Brak Azure CLI. Zainstaluj: winget install Microsoft.AzureCLI, potem otwórz nowe okno PowerShell."
}

# Logowanie (otwiera przeglądarkę tylko, jeśli trzeba)
& az account show --output none 2>$null
if ($LASTEXITCODE -ne 0) { Az login --output none }
$sub = Az account show --query "{name:name, id:id}" -o json | ConvertFrom-Json
Write-Host "Subskrypcja: $($sub.name) ($($sub.id))"

if (-not $SasOnly) {
    Write-Host "Grupa zasobów $ResourceGroup ($Location)..."
    Az group create --name $ResourceGroup --location $Location --output none

    $exists = (& az storage account show --name $Name --resource-group $ResourceGroup --query name -o tsv 2>$null)
    if (-not $exists) {
        $available = Az storage account check-name --name $Name --query nameAvailable -o tsv
        if ($available -ne "true") { throw "Nazwa '$Name' jest zajęta w Azure - wybierz inną (-Name)." }
        Write-Host "Konto Storage $Name (Standard_LRS)..."
    } else {
        Write-Host "Konto Storage $Name już istnieje - aktualizuję ustawienia..."
    }
    # create działa też na istniejącym koncie (aktualizuje ustawienia)
    Az storage account create --name $Name --resource-group $ResourceGroup --location $Location `
        --sku Standard_LRS --kind StorageV2 --min-tls-version TLS1_2 --https-only true `
        --allow-blob-public-access true --output none
}

$key = Az storage account keys list --account-name $Name --resource-group $ResourceGroup --query "[0].value" -o tsv
$auth = @("--account-name", $Name, "--account-key", $key)

if (-not $SasOnly) {
    Write-Host "Kontener '$Container' z publicznym odczytem plików..."
    Az storage container create --name $Container --public-access blob @auth --output none
    # create nie zmienia poziomu dostępu istniejącego kontenera
    Az storage container set-permission --name $Container --public-access blob @auth --output none

    # Dane są publiczne i tylko do odczytu, więc CORS z dowolnej domeny (strona, localhost, rozszerzenie Twitcha)
    Write-Host "CORS (GET/HEAD z dowolnej domeny)..."
    Az storage cors clear --services b @auth --output none
    Az storage cors add --services b --methods GET HEAD OPTIONS --origins "*" `
        --allowed-headers "*" --exposed-headers "*" --max-age 3600 @auth --output none
}

# Token SAS dla kontenera: create + write (bez odczytu, listowania i kasowania), tylko HTTPS
$expiry = (Get-Date).ToUniversalTime().AddYears($SasYears).ToString("yyyy-MM-ddTHH:mmZ")
$sas = Az storage container generate-sas --name $Container --permissions cw --expiry $expiry --https-only @auth -o tsv

$containerUrl = "https://$Name.blob.core.windows.net/$Container"

Write-Host ""
Write-Host "Gotowe. Wpisz w Tf3Bridge -> Azure...:" -ForegroundColor Green
Write-Host "  Adres kontenera: $containerUrl"
Write-Host "  Token SAS:       $sas"
Write-Host "  (ważny do $expiry UTC)"
Write-Host ""
Write-Host "Strona (web/config.js):" -ForegroundColor Green
Write-Host "  dataBaseUrl: `"$containerUrl/latest/`","
Write-Host ""
Set-Clipboard -Value $sas
Write-Host "Token SAS jest w schowku. Nie wklejaj go do repozytorium." -ForegroundColor Yellow
