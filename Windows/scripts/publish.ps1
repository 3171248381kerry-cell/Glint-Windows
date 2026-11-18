param(
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
    [switch]$UseInstalledRuntime,
    [switch]$Installer,
    [string]$InnoCompiler = 'ISCC.exe'
)
$ErrorActionPreference = 'Stop'
$windowsRoot = Split-Path $PSScriptRoot -Parent
$projectPath = Join-Path $windowsRoot 'Glint.Windows/Glint.Windows.csproj'
$version = ([xml](Get-Content -LiteralPath $projectPath -Raw)).Project.PropertyGroup.Version
$distRoot = Join-Path $windowsRoot 'dist'
$dist = Join-Path $distRoot "Glint-$version-$Runtime"
if ($Installer) {
    if ($Runtime -ne 'win-x64') { throw 'The installer supports Windows x64.' }
    $compiler = (Get-Command $InnoCompiler -CommandType Application).Source
}
if ($UseInstalledRuntime) {
    if ($Runtime -ne 'win-x64') { throw 'Installed runtime packaging requires this x64 host.' }
    & dotnet publish $projectPath -c Release -r $Runtime --self-contained false -p:NuGetAudit=false --source (Join-Path $env:USERPROFILE '.nuget/packages') -p:DebugType=None -o $dist --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    $dotnetRoot = Split-Path (Get-Command dotnet).Source -Parent
    $runtimeVersion = Get-ChildItem (Join-Path $dotnetRoot 'shared/Microsoft.WindowsDesktop.App') -Directory | Where-Object { $_.Name -like '10.0.*' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty Name
    foreach ($framework in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')) {
        $source = Join-Path $dotnetRoot "shared/$framework/$runtimeVersion"
        $licenses = Join-Path $dist "Data/DotNetLicenses/$framework"
        New-Item -ItemType Directory -Path $licenses -Force | Out-Null
        Get-ChildItem -LiteralPath $source -File | ForEach-Object {
            if ($_.Name -match 'LICENSE|THIRD-PARTY') { Copy-Item -LiteralPath $_.FullName -Destination $licenses }
            else { Copy-Item -LiteralPath $_.FullName -Destination $dist -Force }
        }
    }
    $licenseRoot = Join-Path $dist 'Data/DotNetLicenses'
    Copy-Item -LiteralPath (Join-Path $dotnetRoot 'LICENSE.txt'), (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') -Destination $licenseRoot
    Copy-Item -LiteralPath (Join-Path $dotnetRoot "host/fxr/$runtimeVersion/hostfxr.dll") -Destination $dist
    $configPath = Join-Path $dist 'Glint.runtimeconfig.json'
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $config.runtimeOptions.PSObject.Properties.Remove('frameworks')
    $config.runtimeOptions.PSObject.Properties.Remove('framework')
    $config.runtimeOptions | Add-Member -NotePropertyName includedFrameworks -NotePropertyValue @(@{name='Microsoft.NETCore.App';version=$runtimeVersion},@{name='Microsoft.WindowsDesktop.App';version=$runtimeVersion}) -Force
    $config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $configPath -Encoding utf8
    # This dependency-free application uses an app-local runtime closure. The framework-
    # dependent deps manifest omits CoreCLR; without it the host probes the local DLLs.
    Remove-Item -LiteralPath (Join-Path $dist 'Glint.deps.json')
    $oldManifest = Join-Path $dist 'Glint.deps.original'
    if (Test-Path -LiteralPath $oldManifest) { Remove-Item -LiteralPath $oldManifest }
} else {
    & dotnet publish $projectPath -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -p:DebugType=None -o $dist --nologo
}
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
Copy-Item -LiteralPath (Join-Path $windowsRoot 'README.md') -Destination (Join-Path $dist 'README.md')
if ($Installer) {
    $installerScript = Join-Path $windowsRoot 'installer/Glint.iss'
    & $compiler "/DGlintVersion=$version" "/DGlintPayload=$dist" "/O$distRoot" $installerScript
    if ($LASTEXITCODE -ne 0) { throw 'Windows installer build failed.' }
    $artifact = $dist + '-setup.exe'
} else {
    $artifact = $dist + '.zip'
    Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $artifact -Force
}
$hash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($artifact + '.sha256') -Value ($hash + '  ' + (Split-Path $artifact -Leaf)) -Encoding ascii
Write-Output $artifact
Write-Output "SHA256: $hash"
