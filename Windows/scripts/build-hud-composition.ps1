param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$OutputDirectory = $OutputDirectory.Trim().Trim('"').TrimEnd('\')
$source = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../Glint.Windows/Native/HudComposition.cpp'))
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$destination = Join-Path $OutputDirectory 'HudComposition.dll'
if ((Test-Path -LiteralPath $destination) -and (Get-Item -LiteralPath $destination).LastWriteTimeUtc -ge (Get-Item -LiteralPath $source).LastWriteTimeUtc) { exit 0 }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) {
    foreach ($candidate in @("${env:ProgramFiles}\Microsoft Visual Studio\2022\Community", "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional", "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise", "${env:ProgramFiles}\Microsoft Visual Studio\18\Community")) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'VC\Auxiliary\Build\vcvars64.bat')) { $installation = $candidate; break }
    }
}
if (-not $installation) { throw 'Visual C++ tools are required to build the HUD material.' }
$vcvars = Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat'
$work = Join-Path $env:TEMP 'glint-hud-composition'
New-Item -ItemType Directory -Force -Path $work | Out-Null
Push-Location $work
try {
    cmd /c "`"$vcvars`" >nul && cl /nologo /LD /std:c++17 /EHsc /O2 /MT /DUNICODE /D_UNICODE /utf-8 `"$source`" /FeHudComposition.dll /link windowsapp.lib dwmapi.lib user32.lib d2d1.lib"
} finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw 'HUD material build failed.' }
Copy-Item -LiteralPath (Join-Path $work 'HudComposition.dll') -Destination $destination -Force
