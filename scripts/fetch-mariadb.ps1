<#
.SYNOPSIS
  Downloads a portable MariaDB (Windows x64) and lays it out under
  LeontecSyncLogSystem/mariadb/ so the app can ship its own database server.

  The binaries are NOT committed to git (see .gitignore). Run this once per machine
  / CI checkout before building a distributable, or let it be part of the packaging step.

.NOTES
  Keeps only bin/ + share/ (enough for mariadb-install-db + mysqld). ~150-250 MB on disk.
#>
param(
    [string]$Version = "11.4.4",
    [string]$Dest = (Join-Path $PSScriptRoot "..\LeontecSyncLogSystem\mariadb")
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"   # much faster Invoke-WebRequest

$pkg = "mariadb-$Version-winx64"
$url = "https://archive.mariadb.org/mariadb-$Version/winx64-packages/$pkg.zip"
$tmp = Join-Path $env:TEMP "leontec-mariadb"
$zip = Join-Path $tmp "$pkg.zip"

New-Item -ItemType Directory -Force -Path $tmp | Out-Null
Write-Host "Downloading $url ..."
Invoke-WebRequest -Uri $url -OutFile $zip

Write-Host "Extracting ..."
$extractDir = Join-Path $tmp "extract"
if (Test-Path $extractDir) { Remove-Item -Recurse -Force $extractDir }
Expand-Archive -Path $zip -DestinationPath $extractDir -Force

$root = Join-Path $extractDir $pkg      # zip contains a top-level folder
if (-not (Test-Path $root)) { $root = (Get-ChildItem $extractDir -Directory | Select-Object -First 1).FullName }

Write-Host "Staging bin/ + share/ into $Dest ..."
if (Test-Path $Dest) { Remove-Item -Recurse -Force $Dest }
New-Item -ItemType Directory -Force -Path $Dest | Out-Null
Copy-Item -Recurse -Force (Join-Path $root "bin")   (Join-Path $Dest "bin")
Copy-Item -Recurse -Force (Join-Path $root "share") (Join-Path $Dest "share")

Write-Host "Done. MariaDB $Version staged at $Dest"
