# Deploy a skin directly to the Jellyfin server's FinSkin skins directory.
# Usage: .\deploy-skin.ps1 -Skin FinModern [-RestartJellyfin]

param(
    [Parameter(Mandatory)][string]$Skin,
    [string]$ServerUser = "henny",
    [string]$ServerHost = "192.168.1.5",
    [string]$SkinsDir   = "/home/henny/docker/cloud/config/data/data/skins",
    [switch]$RestartJellyfin
)

$ErrorActionPreference = "Stop"

$SkinDir = Join-Path $PSScriptRoot "skins"
$CssFile  = Join-Path $SkinDir "$Skin.css"
$JsonFile = Join-Path $SkinDir "$Skin.json"

if (-not (Test-Path $CssFile)) {
    throw "Skin '$Skin' nicht gefunden: $CssFile"
}

Write-Host "Deploye Skin '$Skin' nach ${ServerHost}:${SkinsDir} ..." -ForegroundColor Cyan

ssh "${ServerUser}@${ServerHost}" "mkdir -p `"${SkinsDir}`""
if ($LASTEXITCODE -ne 0) { throw "SSH-Verbindung fehlgeschlagen." }

scp $CssFile "${ServerUser}@${ServerHost}:${SkinsDir}/"
if ($LASTEXITCODE -ne 0) { throw "Kopieren von $Skin.css fehlgeschlagen." }

if (Test-Path $JsonFile) {
    scp $JsonFile "${ServerUser}@${ServerHost}:${SkinsDir}/"
    if ($LASTEXITCODE -ne 0) { throw "Kopieren von $Skin.json fehlgeschlagen." }
}

if ($RestartJellyfin) {
    Write-Host "Starte Jellyfin-Container neu..." -ForegroundColor Cyan
    ssh "${ServerUser}@${ServerHost}" "docker restart jellyfin"
    Write-Host "Jellyfin neugestartet." -ForegroundColor Green
}

Write-Host ""
Write-Host "==================================================" -ForegroundColor Green
Write-Host " Skin '$Skin' deployed!" -ForegroundColor Green
Write-Host "==================================================" -ForegroundColor Green
