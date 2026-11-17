param([Parameter(Mandatory = $true)][ValidateSet('ja', 'ko', 'reading')][string]$Language)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression.FileSystem

$runtimeLock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'runtime-lock.json') -Raw | ConvertFrom-Json
$offlineRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Glint/Offline'))
$runtimeDirectory = [IO.Path]::GetFullPath((Join-Path $offlineRoot $runtimeLock.id))
$pythonDirectory = Join-Path $runtimeDirectory 'Python'
$downloadCache = [IO.Path]::GetFullPath((Join-Path $offlineRoot 'downloads'))
New-Item -ItemType Directory -Force -Path $downloadCache | Out-Null

function Download-Verified($Entry) {
    if ([string]::IsNullOrWhiteSpace($Entry.sha256) -or $Entry.sha256.Length -ne 64) { throw 'Pinned checksum is missing.' }
    $path = Join-Path $downloadCache $Entry.filename
    if (Test-Path -LiteralPath $path) {
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $Entry.sha256) { return $path }
    }
    Write-Output ('下载 ' + $Entry.filename)
    $partial = $path + '.partial'
    Invoke-WebRequest -UseBasicParsing -Uri $Entry.url -OutFile $partial -TimeoutSec 900
    if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Entry.sha256) {
        throw 'Downloaded file checksum did not match the pinned release.'
    }
    Move-Item -LiteralPath $partial -Destination $path -Force
    return $path
}

function Expand-Zip([string]$Archive, [string]$Destination) {
    $root = [IO.Path]::GetFullPath($Destination) + [IO.Path]::DirectorySeparatorChar
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        [long]$total = 0
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ($entry.Name.Length -eq 0) { continue }
            $path = [IO.Path]::GetFullPath((Join-Path $Destination $name))
            if (-not $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe archive path.' }
            if (($entry.ExternalAttributes -band 0xF0000000) -eq 0xA0000000) { throw 'Archive links are not allowed.' }
            $total += $entry.Length
            if ($total -gt 2200MB) { throw 'Archive exceeds the installation size limit.' }
            New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($path)) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path, $true)
        }
    }
    finally { $zip.Dispose() }
}

function Install-Reading {
    if (-not (Test-Path -LiteralPath (Join-Path $pythonDirectory 'python.exe'))) { throw 'Install the base offline runtime before the language model.' }
    $site = Join-Path $pythonDirectory 'Lib/site-packages'
    New-Item -ItemType Directory -Force -Path $site | Out-Null
    $fugashi = Download-Verified $runtimeLock.reading[0]
    if (-not (Test-Path -LiteralPath (Join-Path $site 'fugashi/__init__.py'))) { Expand-Zip $fugashi $site }
    $unidic = Download-Verified $runtimeLock.reading[1]
    $packageDir = Join-Path $site 'unidic_lite'
    $needsPackage = -not (Test-Path -LiteralPath (Join-Path $packageDir '__init__.py'))
    $needsLicense = -not (Test-Path -LiteralPath (Join-Path $packageDir 'LICENSE.unidic'))
    if ($needsPackage -or $needsLicense) {
        $stage = Join-Path $downloadCache 'unidic-lite-src'
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
        New-Item -ItemType Directory -Path $stage | Out-Null
        & tar.exe -xf $unidic -C $stage
        if ($LASTEXITCODE -ne 0) { throw 'Unable to unpack the Japanese reading dictionary.' }
        if ($needsPackage) {
            $package = Get-ChildItem -Path $stage -Recurse -Directory -Filter 'unidic_lite' | Select-Object -First 1
            if (-not $package) { throw 'Japanese reading dictionary layout was not recognized.' }
            Copy-Item -LiteralPath $package.FullName -Destination $packageDir -Recurse -Force
        }
        foreach ($licenseName in @('LICENSE', 'LICENSE.unidic')) {
            $licenseFile = Get-ChildItem -Path $stage -Recurse -File -Filter $licenseName | Select-Object -First 1
            if (-not $licenseFile) { throw "Japanese reading dictionary is missing $licenseName." }
            Copy-Item -LiteralPath $licenseFile.FullName -Destination (Join-Path $packageDir $licenseName) -Force
        }
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}

if ($Language -eq 'reading') {
    Install-Reading
    Write-Output '日语读音数据已就绪'
    exit 0
}

$catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'language-models.json') -Raw | ConvertFrom-Json
$spec = $catalog.$Language
$lockPath = Join-Path $PSScriptRoot ("locks/$Language.json")
if (-not (Test-Path -LiteralPath $lockPath)) { throw 'Model lock is missing.' }
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
Install-Reading
Write-Output ("准备 OPUS-MT " + $(if ($Language -eq 'ja') { '中日' } else { '中韩' }) + "模型")
$archive = Download-Verified $spec.archive
$extract = Join-Path $downloadCache ("src-" + $Language)
if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
New-Item -ItemType Directory -Path $extract | Out-Null
Expand-Zip $archive $extract
$decoder = Get-ChildItem -Path $extract -Recurse -Filter 'decoder.yml' | Select-Object -First 1
if (-not $decoder) { throw 'OPUS-MT package has no decoder configuration.' }
$sourceDir = $decoder.DirectoryName
$converted = Join-Path $downloadCache ("ct2-" + $Language)
if (Test-Path -LiteralPath $converted) { Remove-Item -LiteralPath $converted -Recurse -Force }
$python = Join-Path $pythonDirectory 'python.exe'
$code = @"
from pathlib import Path
from ctranslate2.converters.opus_mt import OpusMTConverter
root = Path(r'$sourceDir')
for path in root.glob('*.vocab'):
    lines = path.read_bytes().replace(b'\r\n', b'\n').replace(b'\r', b'\n').decode('utf-8').split('\n')
    if lines and lines[-1] == '':
        lines = lines[:-1]
    if lines and not lines[0].endswith(': 0'):
        path.write_text(''.join(f'{line}: {index}\n' for index, line in enumerate(lines)), encoding='utf-8')
OpusMTConverter(str(root)).convert(r'$converted', quantization='int8', force=True)
"@
& $python -I -c $code
if ($LASTEXITCODE -ne 0) { throw 'Model conversion failed.' }
$sourceSpm = Join-Path $sourceDir 'source.spm'
$targetSpm = Join-Path $sourceDir 'target.spm'
if (-not (Test-Path -LiteralPath $sourceSpm) -or -not (Test-Path -LiteralPath $targetSpm)) { throw 'SentencePiece models are missing.' }
Copy-Item -LiteralPath $sourceSpm -Destination (Join-Path $converted 'source.spm') -Force
Copy-Item -LiteralPath $targetSpm -Destination (Join-Path $converted 'target.spm') -Force
foreach ($property in $lock.files.PSObject.Properties) {
    $relative = $property.Name.Substring('model/'.Length)
    $path = Join-Path $converted $relative
    if (-not (Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -ne $property.Value.sizeBytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $property.Value.sha256) {
        throw 'Model data checksum did not match the pinned release.'
    }
}
$destination = Join-Path $runtimeDirectory ($spec.folder + '/model')
New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Copy-Item -Path (Join-Path $converted '*') -Destination $destination -Recurse -Force
$pair = if ($Language -eq 'ja') { 'Chinese-Japanese' } else { 'Chinese-Korean' }
$route = if ($Language -eq 'ja') { 'zho-jpn' } else { 'zho-kor' }
$attribution = @"
# OPUS-MT $pair

- Model family: OPUS-MT / Tatoeba-MT ($route)
- Source archive: $($spec.archive.url)
- Project: https://github.com/Helsinki-NLP/Tatoeba-Challenge
- License: CC BY 4.0
- License text: https://creativecommons.org/licenses/by/4.0/
- Local conversion: CTranslate2 int8 for Glint Windows

The converted model data remains available under CC BY 4.0.
"@
[IO.File]::WriteAllText((Join-Path (Split-Path $destination -Parent) 'ATTRIBUTION.md'), $attribution.Trim() + "`n", [Text.UTF8Encoding]::new($false))
Remove-Item -LiteralPath $extract -Recurse -Force
Write-Output ($(if ($Language -eq 'ja') { '中日模型已就绪' } else { '中韩模型已就绪' }))
