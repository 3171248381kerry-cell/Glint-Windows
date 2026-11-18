param([string]$DownloadCache)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression.FileSystem
if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64' -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'The optional local model currently supports Windows x64 only.'
}

$runtimeLock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'runtime-lock.json') -Raw | ConvertFrom-Json
$modelLockPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'Data/model-lock.json'
if (-not (Test-Path -LiteralPath $modelLockPath)) {
    $modelLockPath = Join-Path $PSScriptRoot '../../../Resources/OnlineModels/mt-opus-zh-en/model-lock.json'
}
$modelLock = Get-Content -LiteralPath $modelLockPath -Raw | ConvertFrom-Json
$offlineRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Glint/Offline'))
New-Item -ItemType Directory -Force -Path $offlineRoot | Out-Null
$targetDirectory = [IO.Path]::GetFullPath((Join-Path $offlineRoot $runtimeLock.id))
$stageDirectory = [IO.Path]::GetFullPath((Join-Path $offlineRoot ('.stage-' + [guid]::NewGuid().ToString('N'))))
$backupDirectory = [IO.Path]::GetFullPath((Join-Path $offlineRoot ('.old-' + [guid]::NewGuid().ToString('N'))))
if (-not $DownloadCache) { $DownloadCache = Join-Path $offlineRoot 'downloads' }
$DownloadCache = [IO.Path]::GetFullPath($DownloadCache)
New-Item -ItemType Directory -Force -Path $DownloadCache | Out-Null

function Assert-OwnedDirectory([string]$Directory, [bool]$TemporaryOnly = $false) {
    $resolved = [IO.Path]::GetFullPath($Directory)
    if (-not $resolved.StartsWith($offlineRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Installation path is outside the Glint offline directory.'
    }
    if ($TemporaryOnly -and [IO.Path]::GetFileName($resolved) -notmatch '^\.(stage|old)-[0-9a-f]{32}$') {
        throw 'Unexpected temporary directory.'
    }
}

function Download-Verified($Entry) {
    $path = Join-Path $DownloadCache $Entry.filename
    if (Test-Path -LiteralPath $path) {
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $Entry.sha256) { return $path }
    }
    Write-Output ('下载 ' + $Entry.filename) | Out-Host
    $partial = $path + '.partial'
    Invoke-WebRequest -UseBasicParsing -Uri $Entry.url -OutFile $partial -TimeoutSec 600
    if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Entry.sha256) {
        throw 'Downloaded file checksum did not match the pinned release.'
    }
    Move-Item -LiteralPath $partial -Destination $path -Force
    return $path
}

function Expand-Checked([string]$Archive, [string]$Destination, [string[]]$AllowedEntries = @()) {
    $root = [IO.Path]::GetFullPath($Destination) + [IO.Path]::DirectorySeparatorChar
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        [long]$total = 0
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            $path = [IO.Path]::GetFullPath((Join-Path $Destination $name))
            if (-not $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe archive path.' }
            if (($entry.ExternalAttributes -band 0xF0000000) -eq 0xA0000000) { throw 'Archive links are not allowed.' }
            if ($entry.Name.Length -eq 0) { continue }
            if ($AllowedEntries.Count -gt 0 -and $name -cnotin $AllowedEntries) { throw 'Unexpected file in model data archive.' }
            $total += $entry.Length
            if ($total -gt 800MB) { throw 'Archive exceeds the installation size limit.' }
            New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($path)) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path, $false)
        }
    }
    finally { $zip.Dispose() }
}

$installLock = $null
try {
    $installLock = [IO.File]::Open((Join-Path $offlineRoot 'install.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    Assert-OwnedDirectory $targetDirectory
    Assert-OwnedDirectory $stageDirectory $true
    Assert-OwnedDirectory $backupDirectory $true
    New-Item -ItemType Directory -Path $stageDirectory | Out-Null
    $pythonDirectory = Join-Path $stageDirectory 'Python'
    Write-Output '准备离线翻译运行环境'
    $pythonArchive = Download-Verified $runtimeLock.python
    Expand-Checked $pythonArchive $pythonDirectory
    $sitePackages = Join-Path $pythonDirectory 'Lib/site-packages'
    foreach ($wheel in $runtimeLock.wheels) {
        $wheelArchive = Download-Verified $wheel
        Expand-Checked $wheelArchive $sitePackages
    }
    [IO.File]::WriteAllText((Join-Path $pythonDirectory 'python312._pth'), "python312.zip`n.`nLib/site-packages`nimport site`n", [Text.Encoding]::ASCII)
    Write-Output '准备 OPUS-MT 中英模型'
    $modelArchive = Download-Verified $runtimeLock.model
    $allowed = @('mt-opus-zh-en/manifest.json', 'mt-opus-zh-en/model-lock.json', 'mt-opus-zh-en/ATTRIBUTION.md')
    foreach ($property in $modelLock.files.PSObject.Properties) { $allowed += 'mt-opus-zh-en/' + $property.Name }
    Expand-Checked $modelArchive $stageDirectory $allowed
    foreach ($property in $modelLock.files.PSObject.Properties) {
        $path = Join-Path $stageDirectory ('mt-opus-zh-en/' + $property.Name)
        if ((Get-Item -LiteralPath $path).Length -ne $property.Value.sizeBytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $property.Value.sha256) {
            throw 'Model data checksum did not match the pinned release.'
        }
    }
    Write-Output '验证离线运行环境'
    foreach ($extra in @('mt-opus-zh-ja', 'mt-opus-zh-ko')) {
        $existing = Join-Path $targetDirectory $extra
        if (Test-Path -LiteralPath $existing) { Copy-Item -LiteralPath $existing -Destination (Join-Path $stageDirectory $extra) -Recurse -Force }
    }
    $worker = Join-Path $PSScriptRoot 'opus_worker.py'
    $appBase = Split-Path $PSScriptRoot -Parent
    $proof = @('{"id":0,"text":"你好","target":"en"}') | & (Join-Path $pythonDirectory 'python.exe') -I -u $worker $stageDirectory $appBase en
    if ($LASTEXITCODE -ne 0 -or $proof.Count -ne 2 -or -not ($proof[0] | ConvertFrom-Json).ready -or -not ($proof[1] | ConvertFrom-Json).text) {
        throw 'Offline model could not run. Install the Microsoft Visual C++ 2015-2022 x64 runtime and retry.'
    }
    [IO.File]::WriteAllText((Join-Path $stageDirectory 'ready.json'), ('{"id":"' + $runtimeLock.id + '"}'), [Text.Encoding]::UTF8)
    if (Test-Path -LiteralPath $targetDirectory) { Move-Item -LiteralPath $targetDirectory -Destination $backupDirectory }
    try { Move-Item -LiteralPath $stageDirectory -Destination $targetDirectory }
    catch {
        if (Test-Path -LiteralPath $backupDirectory) { Move-Item -LiteralPath $backupDirectory -Destination $targetDirectory }
        throw
    }
    if (Test-Path -LiteralPath $backupDirectory) { Assert-OwnedDirectory $backupDirectory $true; Remove-Item -LiteralPath $backupDirectory -Recurse -Force }
    Write-Output 'OPUS-MT 已就绪'
}
catch {
    # Do not print exceptions from inference or candidate input. This installer receives no candidate text.
    Write-Error $_.Exception.Message
    exit 1
}
finally {
    if (Test-Path -LiteralPath $stageDirectory) { Assert-OwnedDirectory $stageDirectory $true; Remove-Item -LiteralPath $stageDirectory -Recurse -Force }
    if ($installLock) { $installLock.Dispose() }
}
