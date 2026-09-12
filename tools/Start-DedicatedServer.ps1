[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ConfigPath
)

$configFile = Resolve-Path -LiteralPath $ConfigPath -ErrorAction Stop
$config = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json

if ([string]::IsNullOrWhiteSpace($config.gamePath)) { throw 'gamePath is required.' }
$gamePath = [System.IO.Path]::GetFullPath($config.gamePath)
if (!(Test-Path -LiteralPath $gamePath -PathType Leaf)) { throw "Derail Valley executable not found: $gamePath" }
if ($config.gameMode -notin @('Career', 'FreeRoam')) { throw 'gameMode must be Career or FreeRoam.' }
if ([int]$config.port -lt 1024 -or [int]$config.port -gt 65535) { throw 'port must be between 1024 and 65535.' }
if ([int]$config.maxPlayers -lt 1 -or [int]$config.maxPlayers -gt 255) { throw 'maxPlayers must be between 1 and 255.' }
if ([string]::IsNullOrWhiteSpace($config.session)) { throw 'session is required.' }
if ($null -eq $config.saveUid -and [string]::IsNullOrWhiteSpace($config.save)) { throw 'saveUid or save is required.' }
if ($null -ne $config.saveUid -and ![string]::IsNullOrWhiteSpace($config.save)) { throw 'Choose saveUid or save, not both.' }

$arguments = @(
    '-dvmp-dedicated',
    "-dvmp-game-mode=$($config.gameMode)",
    "-dvmp-session=$($config.session)",
    "-dvmp-port=$([int]$config.port)",
    "-dvmp-max-players=$([int]$config.maxPlayers)"
)
foreach ($entry in @(@{ key = 'user'; value = $config.user }, @{ key = 'save'; value = $config.save }, @{ key = 'save-uid'; value = $config.saveUid }, @{ key = 'server-name'; value = $config.serverName }, @{ key = 'password'; value = $config.password }, @{ key = 'visibility'; value = $config.visibility }, @{ key = 'details'; value = $config.details })) {
    if ($null -ne $entry.value -and ![string]::IsNullOrWhiteSpace([string]$entry.value)) { $arguments += "-dvmp-$($entry.key)=$($entry.value)" }
}
if ($config.headless -eq $true) { $arguments += '-batchmode'; $arguments += '-nographics' }
if (![string]::IsNullOrWhiteSpace($config.logPath)) {
    $logPath = [System.IO.Path]::GetFullPath($config.logPath)
    $logDirectory = Split-Path -Parent $logPath
    if (!(Test-Path -LiteralPath $logDirectory)) { New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null }
    $arguments += '-logFile'
    $arguments += $logPath
}

$process = Start-Process -FilePath $gamePath -ArgumentList $arguments -PassThru -WindowStyle Hidden
[pscustomobject]@{ ProcessId = $process.Id; LogPath = $config.logPath; GamePath = $gamePath }
