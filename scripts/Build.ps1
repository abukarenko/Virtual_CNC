param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    dotnet build VirtualCnc.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet run --project tests/VirtualCnc.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Protocol tests failed.' }
    dotnet publish src/VirtualCnc.App -c Release -r win-x64 --self-contained $SelfContained.IsPresent -o artifacts/app
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
} finally { Pop-Location }
