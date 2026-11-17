$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration Debug
$windowsRoot = Split-Path $PSScriptRoot -Parent
$executable = Join-Path $windowsRoot 'Glint.Windows/bin/Debug/net10.0-windows/Glint.exe'
$reportDirectory = Join-Path (Split-Path $windowsRoot -Parent) 'Artifacts/Windows/self-test'
$testProcess = Start-Process -FilePath $executable -ArgumentList @('--self-test', ('"' + $reportDirectory + '"')) -WindowStyle Hidden -PassThru
if (-not $testProcess.WaitForExit(180000)) { throw 'Self-test did not complete in 180 seconds.' }
Get-Content -LiteralPath (Join-Path $reportDirectory 'results.json')
if ($testProcess.ExitCode -ne 0) { throw 'Windows self-test failed.' }
