param()

$ErrorActionPreference = 'Stop'
$workspacePath = Split-Path $PSScriptRoot -Parent
$expectedLicenseHash = 'adcb860af54915edc16bd1ac0dbf067940e1642d23e53320232f2945b345fdf0'

$licensePath = Join-Path $workspacePath 'LICENSE'
$noticePath = Join-Path $workspacePath 'NOTICE'
$projectPath = Join-Path $workspacePath 'Multiplayer/Multiplayer.csproj'
$postBuildPath = Join-Path $workspacePath 'post-build.ps1'

if ((Get-FileHash -Algorithm SHA256 -LiteralPath $licensePath).Hash.ToLowerInvariant() -ne $expectedLicenseHash) {
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
