param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$OutputDirectory = $OutputDirectory.Trim().Trim('"').TrimEnd('\')
$source = Join-Path $PSScriptRoot '../Glint.Windows/Native/WeTypeSink.c'
$source = [System.IO.Path]::GetFullPath($source)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$destination = Join-Path $OutputDirectory 'WeTypeSink.dll'
if ((Test-Path -LiteralPath $destination) -and (Get-Item -LiteralPath $destination).LastWriteTimeUtc -ge (Get-Item -LiteralPath $source).LastWriteTimeUtc) {
    exit 0
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = $null
if (Test-Path -LiteralPath $vswhere) {
    $installation = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
if (-not $installation) {
    foreach ($candidate in @(
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise",
        "${env:ProgramFiles}\Microsoft Visual Studio\18\Community")) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'VC\Auxiliary\Build\vcvars64.bat')) { $installation = $candidate; break }
    }
}
if (-not $installation) { throw '未找到 Visual C++ 工具集，无法编译微信输入法桥接。' }
$vcvars = Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat'
$work = Join-Path $env:TEMP 'glint-wetype-sink'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$built = Join-Path $work 'WeTypeSink.dll'
Push-Location $work
try {
    cmd /c "`"$vcvars`" >nul && cl /nologo /LD /O2 /MT /W4 /WX /DUNICODE /D_UNICODE /utf-8 `"$source`" /FeWeTypeSink.dll /link oleaut32.lib user32.lib kernel32.lib"
}
finally { Pop-Location }
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $built)) { throw '微信输入法桥接编译失败。' }
Copy-Item -LiteralPath $built -Destination $destination -Force
