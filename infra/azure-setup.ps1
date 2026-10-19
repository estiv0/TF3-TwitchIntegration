# Creates the place for the website data in Azure: a Storage account with a public "live" container.
# Tf3Bridge uploads latest/*.json there with a SAS token (write only), the website reads anonymously.
#
# Needs the Azure CLI (winget install Microsoft.AzureCLI) and an Azure subscription.
# The script can be run many times: existing resources stay, CORS is set again,
# and every run issues a new SAS token (old ones work until they expire).
#
#   .\infra\azure-setup.ps1 -Name tf3live
#   .\infra\azure-setup.ps1 -Name tf3live -SasOnly        # only a new SAS token
param(
    # Storage account name: 3-24 characters, lower-case letters and digits, unique in all of Azure (it's part of the address).
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9]{3,24}$')][string]$Name,
    [string]$ResourceGroup = "tf3-live",
    [string]$Location = "polandcentral",
    [string]$Container = "live",
    [int]$SasYears = 1,
    [switch]$SasOnly
)

$ErrorActionPreference = "Stop"

function Az {
    # az reports errors through the exit code, not exceptions - turn them into an exception
    $out = & az @args
    if ($LASTEXITCODE -ne 0) { throw "az $($args -join ' ') failed ($LASTEXITCODE)" }
    $out
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI not found. Install it: winget install Microsoft.AzureCLI, then open a new PowerShell window."
}

# Login (opens a browser only if needed)
& az account show --output none 2>$null
if ($LASTEXITCODE -ne 0) { Az login --output none }
$sub = Az account show --query "{name:name, id:id}" -o json | ConvertFrom-Json
Write-Host "Subscription: $($sub.name) ($($sub.id))"

if (-not $SasOnly) {
    Write-Host "Resource group $ResourceGroup ($Location)..."
    Az group create --name $ResourceGroup --location $Location --output none

    $exists = (& az storage account show --name $Name --resource-group $ResourceGroup --query name -o tsv 2>$null)
    if (-not $exists) {
        $available = Az storage account check-name --name $Name --query nameAvailable -o tsv
        if ($available -ne "true") { throw "The name '$Name' is taken in Azure - choose another one (-Name)." }
        Write-Host "Storage account $Name (Standard_LRS)..."
    } else {
        Write-Host "Storage account $Name already exists - updating its settings..."
    }
    # create also works on an existing account (it updates the settings)
    Az storage account create --name $Name --resource-group $ResourceGroup --location $Location `
        --sku Standard_LRS --kind StorageV2 --min-tls-version TLS1_2 --https-only true `
        --allow-blob-public-access true --output none
}

$key = Az storage account keys list --account-name $Name --resource-group $ResourceGroup --query "[0].value" -o tsv
$auth = @("--account-name", $Name, "--account-key", $key)

if (-not $SasOnly) {
    Write-Host "Container '$Container' with public read access to files..."
    Az storage container create --name $Container --public-access blob @auth --output none
    # create doesn't change the access level of an existing container
    Az storage container set-permission --name $Container --public-access blob @auth --output none

    # The data is public and read-only, so CORS from any domain (website, localhost, Twitch extension)
    Write-Host "CORS (GET/HEAD from any domain)..."
    Az storage cors clear --services b @auth --output none
    Az storage cors add --services b --methods GET HEAD OPTIONS --origins "*" `
        --allowed-headers "*" --exposed-headers "*" --max-age 3600 @auth --output none
}

# SAS token for the container: create + write (no read, list or delete), HTTPS only
$expiry = (Get-Date).ToUniversalTime().AddYears($SasYears).ToString("yyyy-MM-ddTHH:mmZ")
$sas = Az storage container generate-sas --name $Container --permissions cw --expiry $expiry --https-only @auth -o tsv

$containerUrl = "https://$Name.blob.core.windows.net/$Container"

Write-Host ""
Write-Host "Done. Enter in Tf3Bridge -> Azure...:" -ForegroundColor Green
Write-Host "  Container address: $containerUrl"
Write-Host "  SAS token:         $sas"
Write-Host "  (valid until $expiry UTC)"
Write-Host ""
Write-Host "Website (web/site.json):" -ForegroundColor Green
Write-Host "  `"dataUrl`": `"$containerUrl/latest/`""
Write-Host ""
Set-Clipboard -Value $sas
Write-Host "The SAS token is in the clipboard. Don't paste it into the repository." -ForegroundColor Yellow
