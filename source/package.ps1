# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 Yui-Wei

$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'build.ps1')
if($LASTEXITCODE -ne 0){throw 'Build failed'}
$root=Split-Path $PSScriptRoot -Parent
$binary=Join-Path $root 'DayTrace.exe'
$version=(Get-Item -LiteralPath $binary).VersionInfo.ProductVersion
if($version -notmatch '^\d+\.\d+\.\d+$'){throw 'Invalid version'}
if($env:GITHUB_REF_NAME -and $env:GITHUB_REF_NAME -like 'v*' -and $env:GITHUB_REF_NAME -ne "v$version"){throw 'Tag and binary version differ'}
$folder=Join-Path $root "outputs/DayTrace-v$version"
New-Item -ItemType Directory -Path $folder -Force | Out-Null
Copy-Item -LiteralPath $binary -Destination $folder
foreach($name in @('README.md','使用说明.md','CHANGELOG.md','LICENSE')){Copy-Item -LiteralPath (Join-Path $root $name) -Destination $folder}
Copy-Item -LiteralPath $PSScriptRoot -Destination $folder -Recurse -Force
$manifest=[ordered]@{version=$version;url="https://github.com/Yui-Wei/DayTrace/releases/download/v$version/DayTrace.exe";sha256=(Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant();size=(Get-Item -LiteralPath $binary).Length}
[IO.File]::WriteAllText((Join-Path $folder 'update.json'),($manifest | ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
$zip=Join-Path $root "outputs/DayTrace-v$version.zip"
Compress-Archive -LiteralPath $folder -DestinationPath $zip -Force
Write-Output "Package: $zip"
