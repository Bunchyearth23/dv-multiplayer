$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WindowsCommandLine.ps1')

$testDirectory = Join-Path ([IO.Path]::GetTempPath()) ('dvmp-launcher-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testDirectory) | Out-Null
$probePath = Join-Path $testDirectory 'ArgumentProbe.exe'
$outputPath = Join-Path $testDirectory 'received.json'
$source = @'
using System;
using System.IO;
class ArgumentProbe {
    static void Main(string[] args) {
        File.WriteAllLines(args[0], Array.ConvertAll(args, value => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value))));
    }
}
'@
# The Framework compiler creates a real native-process argv boundary, without Unity.
$compiler = Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'csc.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
}
$sourcePath = Join-Path $testDirectory 'ArgumentProbe.cs'
[IO.File]::WriteAllText($sourcePath, $source)
& $compiler /nologo "/out:$probePath" $sourcePath
if ($LASTEXITCODE -ne 0) { throw 'Argument probe compilation failed.' }
$expected = @($outputPath, '-dvmp-session=Dedicated world', '-dvmp-server-name=My Derail Valley Server', '-dvmp-details=He said "ready"', 'D:\DV Dedicated\logs\', '', '-dvmp-password=a\"b', '-dvmp-user=Équipe belge')
$commandLine = ($expected | ForEach-Object { ConvertTo-WindowsCommandLineArgument $_ }) -join ' '
$process = Start-Process -FilePath $probePath -ArgumentList $commandLine -PassThru -Wait -WindowStyle Hidden
if ($process.ExitCode -ne 0) { throw 'Argument probe failed.' }
$actual = @(Get-Content -LiteralPath $outputPath | ForEach-Object { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($_)) })
if ($actual.Count -ne $expected.Count) { throw 'Argument count changed across process launch.' }
for ($i = 0; $i -lt $expected.Count; $i++) {
    if ($actual[$i] -cne $expected[$i]) { throw "Argument $i changed across process launch." }
}
Write-Output "Dedicated launcher: $($expected.Count) native process arguments preserved."
