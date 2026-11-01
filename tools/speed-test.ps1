# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Nawid Salehie
#
# Speed test of the terminal program against local test mirrors (tools/testserver.py): each scenario is
# downloaded several times and the times compared, so that a change meant to make downloads faster can be
# measured instead of guessed - an idea that looks right can be slower (docs/speed.md has what was learned).
#
#   pwsh tools/speed-test.ps1 -Exe MirrorDL/bin/Release/net10.0/win-x64/mirrordl.exe [-Scenario normal,burst] [-Runs 5]
#
# Every run is checked by its SHA-256; a run that fails counts as failed, not as fast. Mirrors are stopped at the end.
param(
    [Parameter(Mandatory)] [string] $Exe,
    [string[]] $Scenario = @('normal', 'limit503', 'flaky503', 'burst', 'slowlink', 'stall', 'flakyneighbor', 'latency', 'oneconn',
                          'maxspeed', 'maxspeed-curl'),
    [int] $Runs = 5,
    [string] $Label = ''
)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
# "a,b" as one argument (as pwsh -File passes it) works as well as a list.
$Scenario = @($Scenario | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$server = Join-Path $PSScriptRoot 'testserver.py'
$work = Join-Path ([IO.Path]::GetTempPath()) "mirrordl-speed-$PID"
New-Item -ItemType Directory $work | Out-Null
$MB = 1MB

# What each scenario serves: one entry per mirror, its file and testserver.py options.
$scenarios = [ordered]@{
    # Two good links that gain from more connections: the base every change must not slow down.
    normal        = @{ Size = 64; Mirrors = @('--rate 2000000', '--rate 2000000') }
    # A host that allows 4 connections at once and refuses more with 503.
    limit503      = @{ Size = 96; Mirrors = @('--rate 1000000 --max-conns 4') }
    # A host often briefly overloaded: 1 request in 4 is refused with 503.
    flaky503      = @{ Size = 64; Mirrors = @('--rate 1000000 --flaky-503 0.25') }
    # A link that is fast for the first 2 MB of each connection, then throttled, next to a steady one.
    burst         = @{ Size = 64; Mirrors = @('--rate 3000000', '--rate 64000 --burst 2097152') }
    # A slow link next to a fast one, from the start (the end-to-end test's case).
    slowlink      = @{ Size = 32; Mirrors = @('--rate 4000000', '--rate 100000') }
    # A good link that gains from more connections, next to one that drops connections often.
    flakyneighbor = @{ Size = 64; Mirrors = @('--rate 1000000', '--rate 1000000 --fail 0.02') }
    # Far servers: 200 ms before each answer.
    latency       = @{ Size = 1; Mirrors = @('--latency 0.2', '--latency 0.2') }
    # A far server that allows a single connection: none may be refused for asking too much at once.
    oneconn       = @{ Size = 1; Mirrors = @('--latency 0.1 --max-conns 1') }
    # A link whose connections freeze for 2.5 s after every 3 MB, next to a steady one: a brief freeze is no
    # reason to take a connection's work away.
    stall         = @{ Size = 64; Mirrors = @('--rate 4000000', '--rate 3000000 --pause-every 3145728 --pause 2.5') }
    # No limits at all: how fast MirrorDL itself gets on this PC (not on the internet line).
    maxspeed      = @{ Size = 1536; Mirrors = @('', '') }
    # The same file with curl over one connection: what the test server can deliver, for comparison.
    'maxspeed-curl' = @{ Size = 1536; Mirrors = @(''); Curl = $true }
}

$mirrors = [Collections.Generic.List[Diagnostics.Process]]::new()
function Start-Mirror([int] $port, [string] $file, [string] $options, [string] $log) {
    $process = Start-Process python -ArgumentList "`"$server`" --port $port --file `"$file`" --log `"$log`" $options" -PassThru -NoNewWindow
    $mirrors.Add($process)
    for ($i = 0; $i -lt 100; $i++) {
        try { [Net.Sockets.TcpClient]::new('127.0.0.1', $port).Dispose(); return } catch { Start-Sleep -Milliseconds 100 }
    }
    throw "Test mirror on port $port did not start."
}

$results = [Collections.Generic.List[object]]::new()
try {
    foreach ($name in $Scenario) {
        $s = $scenarios[$name]
        if (-not $s) { throw "Unknown scenario $name" }
        $file = Join-Path $work "$name.bin"
        $bytes = [byte[]]::new($s.Size * $MB)
        [Random]::new(7).NextBytes($bytes)
        [IO.File]::WriteAllBytes($file, $bytes)
        $hash = (Get-FileHash $file -Algorithm SHA256).Hash
        $log = Join-Path $work "$name.log"
        $urls = @()
        $port = 19100
        foreach ($options in $s.Mirrors) {
            Start-Mirror $port $file $options $log
            $urls += "http://127.0.0.1:$port/$name.bin"
            $port++
        }
        $times = @()
        $failed = 0
        for ($run = 1; $run -le $Runs; $run++) {
            $out = Join-Path $work "$name-$run"
            New-Item -ItemType Directory $out | Out-Null
            $watch = [Diagnostics.Stopwatch]::StartNew()
            if ($s.Curl) { & curl.exe -s -o (Join-Path $out "$name.bin") $urls[0] }
            else { $null = '' | & $Exe -o $out @urls 2>&1 }
            $code = $LASTEXITCODE
            $watch.Stop()
            $saved = Join-Path $out "$name.bin"
            if ($code -eq 0 -and (Test-Path $saved) -and (Get-FileHash $saved -Algorithm SHA256).Hash -eq $hash) {
                $times += $watch.Elapsed.TotalSeconds
            } else {
                $failed++
            }
            Remove-Item $out -Recurse -Force
        }
        $refused = if (Test-Path $log) { @(Get-Content $log | Where-Object { $_ -in '503', '429' }).Count } else { 0 }
        $sorted = $times | Sort-Object
        $median = if ($sorted.Count) { $sorted[[int][Math]::Floor(($sorted.Count - 1) / 2)] } else { [double]::NaN }
        $results.Add([pscustomobject]@{
            Label = $Label; Scenario = $name; Median = [Math]::Round($median, 2)
            'MB/s' = [Math]::Round($s.Size / $median, 1)
            Min = if ($sorted.Count) { [Math]::Round($sorted[0], 2) } else { $null }
            Max = if ($sorted.Count) { [Math]::Round($sorted[-1], 2) } else { $null }
            Failed = $failed; Refused = $refused
        })
        $mirrors | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
        $mirrors.Clear()
        Start-Sleep -Milliseconds 300
        Remove-Item $file, $log -Force -ErrorAction SilentlyContinue
    }
}
finally {
    $mirrors | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 300
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
$results | Format-Table -AutoSize | Out-String -Width 200
