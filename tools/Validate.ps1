param()

$ErrorActionPreference = 'Stop'
$workspacePath = Split-Path $PSScriptRoot -Parent
Push-Location $workspacePath
try {
    & (Join-Path $PSScriptRoot 'Test-DedicatedLauncher.ps1')
    & (Join-Path $PSScriptRoot 'Test-DistributionCompliance.ps1')

    if (-not (Test-Path -LiteralPath 'Directory.Build.targets')) {
        throw 'Configure Directory.Build.targets from the example with your Derail Valley installation path first.'
    }

    $projects = @(
        'tools/ValidationAssets/MultiplayerEditor/MultiplayerEditor.csproj',
        'tools/ValidationAssets/UnityChan/UnityChan.csproj',
        'Multiplayer/Multiplayer.csproj',
        'tests/Protocol.Tests/Protocol.Tests.csproj',
        'tests/ShopWallet.Tests/ShopWallet.Tests.csproj',
        'tests/ShopBackend.Tests/ShopBackend.Tests.csproj',
        'tests/ItemLifecycle.Tests/ItemLifecycle.Tests.csproj',
        'tests/ExternalPacket.Tests/ExternalPacket.Tests.csproj',
        'tests/WalletIsolation.Tests/WalletIsolation.Tests.csproj'
    )
    foreach ($project in $projects) {
        & dotnet build $project '-p:SkipMultiplayerPostBuild=true' --nologo -v:quiet
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
    }

    & './tests/Protocol.Tests/bin/Debug/net48/Protocol.Tests.exe'
    if ($LASTEXITCODE -ne 0) { throw 'Protocol regression tests failed.' }
    & './tests/ShopWallet.Tests/bin/Debug/net48/ShopWallet.Tests.exe'
    if ($LASTEXITCODE -ne 0) { throw 'Shop wallet Harmony regression checks failed.' }
    & './tests/ShopBackend.Tests/bin/Debug/net48/ShopBackend.Tests.exe'
    if ($LASTEXITCODE -ne 0) { throw 'Shop backend lifecycle regression checks failed.' }
    & './tests/ItemLifecycle.Tests/bin/Debug/net48/ItemLifecycle.Tests.exe'
    if ($LASTEXITCODE -ne 0) { throw 'Item lifecycle regression checks failed.' }
    & './tests/ExternalPacket.Tests/bin/Debug/net48/ExternalPacket.Tests.exe'
    if ($LASTEXITCODE -ne 0) { throw 'External packet identity checks failed.' }
    & './tests/WalletIsolation.Tests/bin/Debug/net48/WalletIsolation.Tests.exe'
    if ($LASTEXITCODE -ne 0) { throw 'Individual wallet isolation checks failed.' }
}
finally {
    Pop-Location
}
