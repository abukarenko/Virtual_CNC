param(
    [string]$EmulatorPort = 'COM20',
    [string]$SenderPort = 'COM21',
    [string]$SetupcPath
)
$ErrorActionPreference = 'Stop'
if ($EmulatorPort -notmatch '^COM\d+$' -or $SenderPort -notmatch '^COM\d+$' -or $EmulatorPort -eq $SenderPort) {
    throw 'Choose two different COM port names, such as COM20 and COM21.'
}
if (-not $SetupcPath) {
    $candidates = @("${env:ProgramFiles}\com0com\setupc.exe", "${env:ProgramFiles(x86)}\com0com\setupc.exe")
    $SetupcPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $SetupcPath -or -not (Test-Path -LiteralPath $SetupcPath)) {
    throw 'com0com is not installed. Install a Windows-compatible signed virtual serial-port driver, then run this script as Administrator. Official project: https://sourceforge.net/projects/com0com/'
}
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script in PowerShell as Administrator.' }
$existing = [System.IO.Ports.SerialPort]::GetPortNames()
if ($existing -contains $EmulatorPort -or $existing -contains $SenderPort) { throw 'One of the requested port names already exists. Choose unused names.' }
& $SetupcPath install "PortName=$EmulatorPort" "PortName=$SenderPort"
if ($LASTEXITCODE -ne 0) { throw "com0com failed, exit code $LASTEXITCODE" }
Write-Host "Emulator: $EmulatorPort; CNC sender: $SenderPort. Use 115200 baud, 8N1."
& $SetupcPath list
