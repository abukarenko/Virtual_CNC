$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    & "$PSScriptRoot\Generate-Icon.ps1"
    dotnet run --project tests/VirtualCnc.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Protocol tests failed.' }
    dotnet publish src/VirtualCnc.App -p:PublishProfile=Portable -o artifacts/portable
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath 'artifacts\portable\VirtualCnc.App.exe' -Destination 'VirtualCNC.exe' -Force
    & "$PSScriptRoot\Test-Transport.ps1" -ExecutablePath (Join-Path $projectRoot 'VirtualCNC.exe')
    Write-Host 'Ready: VirtualCNC.exe — single file with built-in .NET runtime.'
} finally { Pop-Location }
