param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug', [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$windowsRoot = Split-Path $PSScriptRoot -Parent
if (-not $NoBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration }
$executable = Join-Path $windowsRoot "Glint.Windows/bin/$Configuration/net10.0-windows/Glint.exe"
if (-not (Test-Path -LiteralPath $executable)) { throw 'Build Glint first.' }
# Ask Glint to exit gracefully only after a successful build. Never terminate another app by name.
$oldProcesses = @(Get-Process -Name Glint -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($windowsRoot, [StringComparison]::OrdinalIgnoreCase) })
if ($oldProcesses.Count -gt 0) {
    Start-Process -FilePath $executable -ArgumentList '--quit' -WindowStyle Hidden -Wait
    foreach ($oldProcess in $oldProcesses) { if (-not $oldProcess.WaitForExit(10000)) { throw 'Previous Glint did not exit; new instance was not started.' } }
}
$artifactDirectory = Join-Path (Split-Path $windowsRoot -Parent) 'Artifacts/Windows'
New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
$readyPath = Join-Path $artifactDirectory ('ready-' + [guid]::NewGuid().ToString('N') + '.json')
$appProcess = Start-Process -FilePath $executable -ArgumentList @('--ready-file', ('"' + $readyPath + '"')) -WindowStyle Hidden -PassThru
$deadline = [DateTime]::UtcNow.AddSeconds(20)
while (-not (Test-Path -LiteralPath $readyPath)) {
    if ($appProcess.HasExited) { throw "Glint exited during startup: $($appProcess.ExitCode)" }
    if ([DateTime]::UtcNow -gt $deadline) { throw 'Glint did not report readiness within 20 seconds.' }
    Start-Sleep -Milliseconds 100
}
$ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
if ($ready.status -ne 'ready' -or $ready.processId -ne $appProcess.Id) { throw 'Glint started without a ready translation runtime.' }
Write-Output "Glint ready (PID $($appProcess.Id))."
