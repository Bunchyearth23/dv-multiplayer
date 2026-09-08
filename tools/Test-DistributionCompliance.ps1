param()

$ErrorActionPreference = 'Stop'
$workspacePath = Split-Path $PSScriptRoot -Parent
$expectedNormalizedLicenseHash = 'c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4'

$licensePath = Join-Path $workspacePath 'LICENSE'
$noticePath = Join-Path $workspacePath 'NOTICE'
$projectPath = Join-Path $workspacePath 'Multiplayer/Multiplayer.csproj'
$postBuildPath = Join-Path $workspacePath 'post-build.ps1'

$licenseText = [System.IO.File]::ReadAllText($licensePath).Replace("`r`n", "`n").Replace("`r", "`n")
$licenseBytes = [System.Text.UTF8Encoding]::new($false).GetBytes($licenseText)
$sha256 = [System.Security.Cryptography.SHA256]::Create()
try { $actualLicenseHash = ([System.BitConverter]::ToString($sha256.ComputeHash($licenseBytes))).Replace('-', '').ToLowerInvariant() }
finally { $sha256.Dispose() }
if ($actualLicenseHash -ne $expectedNormalizedLicenseHash) {
    throw 'LICENSE differs from the canonical upstream Apache-2.0 text tracked by this fork.'
}

$notice = Get-Content -Raw -LiteralPath $noticePath
foreach ($requiredText in @('AMacro/dv-multiplayer', 'Insprill/dv-multiplayer', 'Bunchyearth23/dv-multiplayer', 'modifications made in 2026', 'Copyright 2026 Bunchy (Bunchyearth23)', 'Apache License, Version 2.0')) {
    if (-not $notice.Contains($requiredText)) { throw "NOTICE is missing required attribution: $requiredText" }
}

$project = Get-Content -Raw -LiteralPath $projectPath
if ($project -notmatch 'LICENSE;\.\./NOTICE;') { throw 'Multiplayer.csproj does not copy LICENSE and NOTICE into build artifacts.' }

$postBuild = Get-Content -Raw -LiteralPath $postBuildPath
foreach ($requiredFile in @('/LICENSE', '/NOTICE')) {
    if (-not $postBuild.Contains($requiredFile)) { throw "post-build.ps1 does not package $requiredFile." }
}

Write-Host 'Distribution compliance checks passed.'
