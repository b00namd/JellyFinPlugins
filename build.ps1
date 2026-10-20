# Build script for JellyTube + JellyTubbing plugins
# Creates plugin ZIPs and a combined manifest.json for installation via Jellyfin dashboard.

param(
    [string]$Version = "1.0.0.0",
    [string]$BaseUrl = "https://raw.githubusercontent.com/b00namd/JellyFinPlugins/master/dist",
    [string]$RepoUrl = "https://raw.githubusercontent.com/b00namd/JellyFinPlugins/master"
)

$ErrorActionPreference = "Stop"
$OutputDir    = Join-Path $PSScriptRoot "dist"
$ManifestPath = Join-Path $OutputDir "manifest.json"
$timestamp    = (Get-Date -Format "yyyy-MM-ddTHH:mm:ssZ")

# Jellyfin DLLs that must NOT be in plugin ZIPs
$jellyfinDlls = @(
    "MediaBrowser.Common.dll",
    "MediaBrowser.Controller.dll",
    "MediaBrowser.Model.dll",
    "Microsoft.Extensions.*",
    "Newtonsoft.Json.dll"
)

# --- Clean dist ---
Write-Host "Cleaning output directory..." -ForegroundColor Cyan
if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDir | Out-Null

$manifestEntries = @()

# ============================================================
# Helper: build one plugin project
# ============================================================
function Build-Plugin {
    param(
        [string]$ProjectDir,
        [string]$AssemblyName,
        [string]$PluginName,
        [string]$Description,
        [string]$Overview,
        [string]$Guid,
        [string]$PluginVersion = $Version,
        [string]$Changelog = "Initiale Version"
    )

    $publishDir = Join-Path $OutputDir "publish-$AssemblyName"
    $zipName    = "${AssemblyName}_${PluginVersion}.zip"
    $zipPath    = Join-Path $OutputDir $zipName

    Write-Host ""
    Write-Host "Building $PluginName ..." -ForegroundColor Cyan
    dotnet publish "$ProjectDir" -c Release -o "$publishDir" --no-self-contained | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $PluginName" }

    # Remove Jellyfin framework DLLs
    foreach ($pattern in $jellyfinDlls) {
        Get-ChildItem $publishDir -Filter $pattern | Remove-Item -Force
    }

    Write-Host "Creating ZIP: $zipName ..." -ForegroundColor Cyan
    Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath -Force

    $md5  = (Get-FileHash $zipPath -Algorithm MD5).Hash.ToLower()
    $size = (Get-Item $zipPath).Length
    Write-Host "MD5: $md5  ($size bytes)" -ForegroundColor Gray

    return @{
        category    = "General"
        guid        = $Guid
        name        = $PluginName
        description = $Description
        overview    = $Overview
        owner       = "local"
        imageUrl    = "$RepoUrl/$AssemblyName/thumb.png"
        versions    = @(
            @{
                version    = $PluginVersion
                changelog  = $Changelog
                targetAbi  = "10.9.0.0"
                sourceUrl  = "$BaseUrl/$zipName"
                checksum   = $md5
                timestamp  = $timestamp
            }
        )
    }
}

# ============================================================
# Build JellyTube
# ============================================================
$manifestEntries += Build-Plugin `
    -ProjectDir  (Join-Path $PSScriptRoot "Jellyfin.Plugin.JellyTube") `
    -AssemblyName "Jellyfin.Plugin.JellyTube" `
    -PluginName  "JellyTube" `
    -Description "YouTube-Videos, Playlists und Kanaele in die Jellyfin-Mediathek herunterladen - manuell oder automatisch per Zeitplan." `
    -Overview    "Laedt YouTube-Inhalte mit yt-dlp (wird vom Plugin selbst aktuell gehalten) und erstellt NFO-Metadaten, Vorschaubilder und Kapitel. Kanaele koennen als Serien angelegt werden, geplante Downloads holen neue Videos automatisch und gesehene Videos lassen sich nach einer Karenzzeit automatisch loeschen." `
    -Guid        "a1b2c3d4-e5f6-7890-abcd-ef1234567890" `
    -PluginVersion "1.0.3.0" `
    -Changelog   "Behebt Fehler 401 auf der Plugin-Seite unter Jellyfin 10.11 (Tools-Check, Warteschlange, Downloads). Tools-Check erkennt das vom Plugin verwaltete yt-dlp. Serien-Modus: automatische Downloads in einer Staffel 'Season 1', manuelle Playlists als eigene Staffel. Automatisch als gesehen markierte Videos starten ebenfalls den Loesch-Timer."

# ============================================================
# Build JellyTubbing
# ============================================================
$manifestEntries += Build-Plugin `
    -ProjectDir  (Join-Path $PSScriptRoot "Jellyfin.Plugin.JellyTubbing") `
    -AssemblyName "Jellyfin.Plugin.JellyTubbing" `
    -PluginName  "JellyTubbing" `
    -Description "YouTube-Videos direkt in Jellyfin streamen - via Invidious mit yt-dlp als Fallback." `
    -Overview    "Nutzt Invidious als primaere Quelle fuer YouTube-Streams. yt-dlp dient als Fallback." `
    -Guid        "c3d4e5f6-a7b8-9012-cdef-012345678901"

# ============================================================
# Write combined manifest.json
# ============================================================
Write-Host ""
Write-Host "Writing manifest.json..." -ForegroundColor Cyan
$json = ConvertTo-Json -InputObject $manifestEntries -Depth 5
[System.IO.File]::WriteAllText($ManifestPath, $json, [System.Text.UTF8Encoding]::new($false))

Write-Host ""
Write-Host "==================================================" -ForegroundColor Green
Write-Host " Build completed successfully!" -ForegroundColor Green
Write-Host "==================================================" -ForegroundColor Green
Write-Host " Manifest: $ManifestPath"
$zipTube    = Join-Path $OutputDir "Jellyfin.Plugin.JellyTube_$Version.zip"
$zipTubbing = Join-Path $OutputDir "Jellyfin.Plugin.JellyTubbing_$Version.zip"
Write-Host " JellyTube:    $zipTube"
Write-Host " JellyTubbing: $zipTubbing"
Write-Host ""
