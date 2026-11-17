param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot '../Glint.Windows/Glint.Windows.csproj'
& dotnet build $projectPath --configuration $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'Windows build failed.' }
