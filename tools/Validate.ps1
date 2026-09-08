param()

$ErrorActionPreference = 'Stop'
$workspacePath = Split-Path $PSScriptRoot -Parent
Push-Location $workspacePath
try {
    & (Join-Path $PSScriptRoot 'Test-DistributionCompliance.ps1')

    if (-not (Test-Path -LiteralPath 'Directory.Build.targets')) {
        throw 'Configure Directory.Build.targets from the example with your Derail Valley installation path first.'
    }

    $projects = @(
        'tools/ValidationAssets/MultiplayerEditor/MultiplayerEditor.csproj',
        'tools/ValidationAssets/UnityChan/UnityChan.csproj',
        'Multiplayer/Multiplayer.csproj',
        'tests/Protocol.Tests/Protocol.Tests.csproj'
    )
    foreach ($project in $projects) {
        & dotnet build $project '-p:SkipMultiplayerPostBuild=true' --nologo -v:quiet
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
    }

    & './tests/Protocol.Tests/bin/Debug/net48/Protocol.Tests.exe'
    if ($LASTEXITCODE -ne 0) { throw 'Protocol regression tests failed.' }
}
finally {
    Pop-Location
}
