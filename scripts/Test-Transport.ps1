param([int]$Port = 17071, [string]$ExecutablePath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $projectRoot 'artifacts\app\VirtualCnc.App.exe'
if (-not (Test-Path -LiteralPath $exe)) { $exe = Join-Path $projectRoot 'src\VirtualCnc.App\bin\Release\net10.0-windows\VirtualCnc.App.exe' }
if ($ExecutablePath) { $exe = [System.IO.Path]::GetFullPath($ExecutablePath) }
$temporary = Join-Path ([System.IO.Path]::GetTempPath()) ('VirtualCncTransport-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $temporary | Out-Null
$process = $null
$client = $null
try {
    $process = Start-Process -FilePath $exe -ArgumentList @('--tcp', "$Port", '--memory', ('"' + (Join-Path $temporary 'eeprom.json') + '"')) -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $temporary 'stderr.txt')
    $client = [System.Net.Sockets.TcpClient]::new()
    $connected = $false
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        try { $client.Connect('127.0.0.1', $Port); $connected = $true; break } catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $connected) { throw 'TCP server did not start.' }
    $stream = $client.GetStream()
    $stream.ReadTimeout = 3000
    $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::ASCII, $false, 1024, $true)
    function Send-Raw([byte[]]$Bytes) { $stream.Write($Bytes, 0, $Bytes.Length); $stream.Flush() }
    function Send-Line([string]$Line) { Send-Raw ([System.Text.Encoding]::ASCII.GetBytes($Line + "`n")) }
    function Read-Until([string]$Prefix) {
        $lines = [System.Collections.Generic.List[string]]::new()
        for ($i = 0; $i -lt 100; $i++) {
            $line = $reader.ReadLine()
            if ($null -eq $line) { throw 'Unexpected end of stream.' }
            $lines.Add($line)
            if ($line.StartsWith($Prefix)) { return ,$lines.ToArray() }
        }
        throw "Response $Prefix not received."
    }
    $banner = Read-Until 'Grbl '
    if ($banner[-1] -ne "Grbl 1.1f ['$' for help]") { throw 'Unexpected banner.' }
    Send-Line '$I'
    $info = Read-Until 'ok'
    if ($info -notcontains '[VER:1.1f.20170801:]') { throw 'Wrong version.' }
    Send-Line '$100=123.5'
    $null = Read-Until 'ok'
    Send-Line 'G1X10F300'
    $null = Read-Until 'ok'
    Send-Raw ([byte[]]@(63))
    $status = Read-Until '<'
    if ($status[-1] -notlike '<Run|MPos:*') { throw 'Status unavailable during motion.' }
    Send-Raw ([byte[]]@(33,63))
    $status = Read-Until '<'
    if ($status[-1] -notlike '<Hold:0|*') { throw 'Hold did not take effect.' }
    Send-Raw ([byte[]]@(24))
    $null = Read-Until 'ALARM:3'
    Send-Line '$X'
    $null = Read-Until 'ok'
    Send-Line 'G81'
    $errorReply = Read-Until 'error:'
    if ($errorReply[-1] -ne 'error:20') { throw 'Wrong error code.' }
    foreach ($setting in @('$22=1','$23=7','$130=2','$131=3','$132=1','$27=0.1','$26=250','$24=60','$25=600')) {
        Send-Line $setting
        $null = Read-Until 'ok'
    }
    Send-Line '$H'
    $homingClock = [System.Diagnostics.Stopwatch]::StartNew()
    $homingContacts = ''
    $homeComplete = $false
    while ($homingClock.Elapsed.TotalSeconds -lt 12) {
        Send-Raw ([byte[]]@(63))
        $responses = Read-Until '<'
        $status = $responses[-1]
        if ($status -match '\|Pn:([XYZ]+)') { $homingContacts += $Matches[1] }
        if ($status.StartsWith('<Idle|')) { $homeComplete = $true; break }
        Start-Sleep -Milliseconds 20
    }
    if (-not $homeComplete -or -not ($homingContacts.Contains('X') -and $homingContacts.Contains('Y') -and $homingContacts.Contains('Z'))) { throw 'Homing did not complete with all three virtual switch contacts.' }
    if ($status -notmatch 'MPos:([-\d.]+),([-\d.]+),([-\d.]+)') { throw 'No final homing position.' }
    if ([Math]::Abs([double]$Matches[1]+1.9) -gt .01 -or [Math]::Abs([double]$Matches[2]+2.9) -gt .01 -or [Math]::Abs([double]$Matches[3]+.9) -gt .01) { throw 'Wrong final homing position.' }
    $memoryPath = Join-Path $temporary 'eeprom.json'
    $memory = Get-Content -Raw -LiteralPath $memoryPath | ConvertFrom-Json
    if ($memory.Settings.'100' -ne 123.5) { throw 'EEPROM not saved.' }
    Write-Host 'PASS TCP byte transport: banner, version, EEPROM, motion, query, hold, reset, unlock, error, homing contacts and pull-off.'
} finally {
    if ($client) { $client.Dispose() }
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
    $resolved = [System.IO.Path]::GetFullPath($temporary)
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolved.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $resolved) -like 'VirtualCncTransport-*') { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
