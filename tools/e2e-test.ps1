# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Nawid Salehie
#
# End-to-end test of the terminal program: downloads from local test mirrors (tools/testserver.py)
# and checks every result by its SHA-256. ci.yml runs it; it also runs locally:
#
#   pwsh tools/e2e-test.ps1 -Exe MirrorDL/bin/Release/net10.0/win-x64/mirrordl.exe [-Aria2 path\to\aria2c.exe]
#
# Covers: good and bad links together (different content, different size, dead, one that allows a
# single connection and answers 403 to more, one that drops connections), pasted input, a Firefox
# .part file, several unfinished files put together, one under another name, a finished file that is
# only checked, an unfinished file of another file, a hard kill and resume, a server without range requests, links to several
# files at once with a checksum named for each, connections the tuner tries
# and takes back, and - when aria2c is given or on the PATH - a download that aria2 started.
param(
    [Parameter(Mandatory)] [string] $Exe,
    [string] $Aria2 = (Get-Command aria2c -ErrorAction SilentlyContinue)?.Source
)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$server = Join-Path $PSScriptRoot 'testserver.py'
$work = Join-Path ([IO.Path]::GetTempPath()) "mirrordl-e2e-$PID"
New-Item -ItemType Directory $work | Out-Null
$mirrors = [Collections.Generic.List[Diagnostics.Process]]::new()
$failed = [Collections.Generic.List[string]]::new()

function New-TestFile([string] $name, [int] $size, [int] $seed) {
    $bytes = [byte[]]::new($size)
    [Random]::new($seed).NextBytes($bytes)
    [IO.File]::WriteAllBytes((Join-Path $work $name), $bytes)
}

function Copy-Start([string] $from, [string] $to, [long] $count) {
    $bytes = [byte[]]::new($count)
    $source = [IO.File]::OpenRead($from)
    try { $source.ReadExactly($bytes, 0, $count) } finally { $source.Dispose() }
    [IO.File]::WriteAllBytes($to, $bytes)
}

function Start-Mirror([int] $port, [string] $file, [string] $options = '') {
    $process = Start-Process python -ArgumentList "`"$server`" --port $port --file `"$(Join-Path $work $file)`" $options" `
        -PassThru -NoNewWindow
    $mirrors.Add($process)
    for ($i = 0; $i -lt 100; $i++) {
        try { [Net.Sockets.TcpClient]::new('127.0.0.1', $port).Dispose(); return } catch { Start-Sleep -Milliseconds 100 }
    }
    throw "Test mirror on port $port did not start."
}

function Url([int] $port) { "http://127.0.0.1:$port/test.bin" }
function New-Folder([string] $name) { (New-Item -ItemType Directory (Join-Path $work $name)).FullName }
function Hash([string] $path) { if (Test-Path $path) { (Get-FileHash $path -Algorithm SHA256).Hash.ToLower() } }
function New-Finished([string] $name, [string] $from) {
    $dir = New-Folder $name
    Copy-Item "$work\$from" "$dir\game.rar"
    Set-ItemProperty "$dir\game.rar" IsReadOnly $true # opening it for writing would fail the run
    $dir
}


function Invoke-Mirrordl([string[]] $arguments, [string] $stdin) {
    $output = if ($stdin) { $stdin | & $Exe @arguments 2>&1 | Out-String } else { & $Exe @arguments 2>&1 | Out-String }
    [pscustomobject]@{ Code = $LASTEXITCODE; Output = $output }
}

function Test-Case([string] $name, [scriptblock] $check, $run) {
    $ok = $false
    try { $ok = & $check } catch { $ok = $false }
    if ($ok) { Write-Host "PASS  $name" -ForegroundColor Green; return }
    Write-Host "FAIL  $name" -ForegroundColor Red
    if ($run) { Write-Host "exit code $($run.Code)`n$($run.Output)" }
    $failed.Add($name)
}

try {
    New-TestFile test.bin (24MB) 1
    New-TestFile other.bin (24MB) 2
    New-TestFile small.bin (2MB) 3
    New-TestFile empty.bin 0 4
    New-TestFile big.bin (64MB) 5
    $hash = Hash (Join-Path $work test.bin)

    Start-Mirror 18001 test.bin '--rate 4000000'
    Start-Mirror 18002 test.bin '--rate 4000000 --fail 0.003'
    Start-Mirror 18003 other.bin
    Start-Mirror 18004 small.bin
    Start-Mirror 18005 test.bin '--rate 3000000 --max-conns 1 --limit-status 403'
    Start-Mirror 18006 test.bin '--no-range --rate 8000000'
    Start-Mirror 18007 test.bin '--rate 100000'
    Start-Mirror 18008 empty.bin
    Start-Mirror 18009 empty.bin
    Start-Mirror 18010 big.bin '--rate 1000000 --total-rate 4000000'

    # Good and bad links together; only the good ones may be used.
    $dir = New-Folder mixed
    $run = Invoke-Mirrordl @('-o', $dir, (Url 18001), (Url 18002), (Url 18003), (Url 18004), (Url 18005), 'http://127.0.0.1:18099/test.bin', $hash)
    Test-Case 'mixed good and bad links' {
        $run.Code -eq 0 -and (Hash "$dir\test.bin") -eq $hash -and
        $run.Output -match 'different content' -and $run.Output -match 'different size'
    } $run

    # Pasted text: links between empty lines, a prefixed checksum, words that are no link.
    $dir = New-Folder pasted
    $run = Invoke-Mirrordl @('-o', $dir) "`n$(Url 18001)`n`n$(Url 18002)`nSHA256: $hash`nsome words`n"
    Test-Case 'pasted input' { $run.Code -eq 0 -and (Hash "$dir\test.bin") -eq $hash -and $run.Output -match 'Ignored' } $run

    # Firefox: "name.<8 random>.ext.part" next to an empty "name.ext".
    $dir = New-Folder firefox
    New-Item -ItemType File "$dir\test.bin" | Out-Null
    Copy-Start "$work\test.bin" "$dir\test.Ab12-xY_.bin.part" (9MB)
    $run = Invoke-Mirrordl @('-o', $dir, (Url 18001), (Url 18002), $hash)
    Test-Case 'Firefox .part file' {
        $run.Code -eq 0 -and (Hash "$dir\test.bin") -eq $hash -and -not (Test-Path "$dir\test.Ab12-xY_.bin.part") -and
        $run.Output -match 'Found test\.Ab12-xY_\.bin\.part'
    } $run

    # Firefox crashed before its last bytes reached the disk: they read as zeros. The good beginning is kept.
    $dir = New-Folder firefoxcrash
    New-Item -ItemType File "$dir\test.bin" | Out-Null
    Copy-Start "$work\test.bin" "$dir\test.Zz9_Qq8w.bin.part" (12MB)
    $zeros = [IO.File]::Open("$dir\test.Zz9_Qq8w.bin.part", 'Append')
    $zeros.Write([byte[]]::new(3MB), 0, 3MB)
    $zeros.Dispose()
    $run = Invoke-Mirrordl @('-o', $dir, (Url 18001), (Url 18002), $hash)
    Test-Case 'Firefox .part whose end never reached the disk' {
        $run.Code -eq 0 -and (Hash "$dir\test.bin") -eq $hash -and $run.Output -match 'never reached the disk'
    } $run

    # Several unfinished files of the same download: MirrorDL's own with the middle third, a numbered Firefox
    # copy with the beginning, and a numbered copy of another file. The first two are put together, so only
    # the rest is downloaded, and the Firefox copy is removed at the end; the third is left as it is.
    function New-Stitch([string] $name) {
        $dir = New-Folder $name
        $middle = [byte[]]::new(24MB)
        [Array]::Copy([IO.File]::ReadAllBytes("$work\test.bin"), 8MB, $middle, 8MB, 8MB)
        [IO.File]::WriteAllBytes("$dir\test.bin.part", $middle)
        [IO.File]::WriteAllText("$dir\test.bin.part.mirrordl", "mirrordl 2`nsize $(24MB)`nmissing 0 $(8MB)`nmissing $(16MB) $(24MB)`nend`n")
        Copy-Start "$work\test.bin" "$dir\test(1).Ab12-xY_.bin.part" (6MB)
        Copy-Start "$work\other.bin" "$dir\test(2).Q1w2E3r4.bin.part" (5MB)
        $dir
    }
    $dir = New-Stitch stitch
    $otherBefore = Hash "$dir\test(2).Q1w2E3r4.bin.part"
    # A newline as input: should a question come (a leftover that doesn't fit), the answer is no, not a wait.
    $run = Invoke-Mirrordl @('-o', $dir, (Url 18001), (Url 18002), $hash) "`n"
    # The Firefox copy is in the Recycle Bin (only said when Windows reports it arrived there) - or, on a machine
    # whose Recycle Bin can't be sized up (a fresh CI user), kept and said so. Never gone without being in it.
    $leftover = "$dir\test(1).Ab12-xY_.bin.part"
    $recycled = $run.Output -match 'Moved the other unfinished files' -and -not (Test-Path $leftover)
    $kept = $run.Output -match "Kept, since they can't go to the Recycle Bin: test\(1\)\.Ab12-xY_\.bin\.part" -and (Test-Path $leftover)
    if ($kept) { Write-Host "NOTE  the leftover was kept: this machine's Recycle Bin can't be sized up" -ForegroundColor Yellow }
    Test-Case 'several unfinished files put together' {
        $run.Code -eq 0 -and (Hash "$dir\test.bin") -eq $hash -and
        $run.Output -match 'Took 5\.00 MB from test\(1\)\.Ab12-xY_\.bin\.part' -and $run.Output -match 'Downloaded 11\.00 MB' -and
        $run.Output -match 'test\(2\)\.Q1w2E3r4\.bin\.part is not the same file' -and
        (Hash "$dir\test(2).Q1w2E3r4.bin.part") -eq $otherBefore -and ($recycled -or $kept)
    } $run

    # A leftover that doesn't fit in the Recycle Bin (made so here by MIRRORDL_TEST_NO_RECYCLE_BIN) can only be
    # deleted for good: kept when the answer is no, deleted without asking with --delete-leftovers. Never the
    # copy of another file next to it.
    $env:MIRRORDL_TEST_NO_RECYCLE_BIN = '1'
    $dir = New-Stitch stitchnoroom
    $no = Invoke-Mirrordl @('-o', $dir, (Url 18001), (Url 18002), $hash) "`n"
    $keptNo = (Test-Path "$dir\test(1).Ab12-xY_.bin.part") -and (Hash "$dir\test.bin") -eq $hash
    $dir = New-Stitch stitchdelete
    $yes = Invoke-Mirrordl @('-o', $dir, '--delete-leftovers', (Url 18001), (Url 18002), $hash) "`n"
    Remove-Item Env:MIRRORDL_TEST_NO_RECYCLE_BIN
    Test-Case "leftovers that don't fit in the Recycle Bin: kept, or deleted for good when asked to" {
        $no.Code -eq 0 -and $keptNo -and $no.Output -match "Kept, since they can't go to the Recycle Bin: test\(1\)" -and
        $no.Output -match 'Test mode' -and
        $yes.Code -eq 0 -and (Hash "$dir\test.bin") -eq $hash -and -not (Test-Path "$dir\test(1).Ab12-xY_.bin.part") -and
        (Hash "$dir\test(2).Q1w2E3r4.bin.part") -eq $otherBefore -and $yes.Output -match 'Deleted for good: test\(1\)\.Ab12-xY_\.bin\.part'
    } ([pscustomobject]@{ Code = "$($no.Code) / $($yes.Code)"; Output = $no.Output + $yes.Output })

    # The same, but the result doesn't match the checksum given: nothing may be removed then.
    $dir = New-Stitch stitchdamaged
    $run = Invoke-Mirrordl @('-o', $dir, (Url 18001), (Url 18002), (Hash "$work\other.bin"))
    Test-Case 'keeps the other unfinished files when the result is damaged' {
        $run.Code -eq 2 -and (Test-Path "$dir\test(1).Ab12-xY_.bin.part") -and $run.Output -match 'are kept'
    } $run

    # An unfinished file under a name of its own, given by hand: compared by content, continued, and it keeps its
    # name and place - as aria2 and wget do - instead of taking the links' name.
    $dir = New-Folder renamed
    Copy-Start "$work\test.bin" "$dir\my copy.dat" (9MB)
    $run = Invoke-Mirrordl @('-o', $dir, "$dir\my copy.dat", (Url 18001), (Url 18002), $hash)
    Test-Case 'unfinished file with another name' {
        $run.Code -eq 0 -and (Hash "$dir\my copy.dat") -eq $hash -and -not (Test-Path "$dir\test.bin") -and
        -not (Test-Path "$dir\my copy.dat.mirrordl") -and $run.Output -match 'Found my copy\.dat'
    } $run

    # A finished file, given to check it against the links: only read - never written, renamed or moved, also
    # when a link's only name is a token at the end of its address (as it was on 2026-11-01, when such a check
    # renamed the user's file to that token). A plain file given with it is never removed either: it may be the
    # user's own, not an unfinished download.
    $dir = New-Finished finished test.bin
    Copy-Start "$work\test.bin" "$dir\copy.dat" (5MB)
    $before = (Get-Item "$dir\game.rar").LastWriteTimeUtc
    $run = Invoke-Mirrordl @('-o', $dir, "$dir\game.rar", "$dir\copy.dat", 'http://127.0.0.1:18001/ISYQvAPiMv', (Url 18002), $hash)
    Test-Case 'a finished file is only checked' {
        $run.Code -eq 0 -and (Hash "$dir\game.rar") -eq $hash -and (Get-Item "$dir\game.rar").LastWriteTimeUtc -eq $before -and
        @(Get-ChildItem $dir).Count -eq 2 -and (Test-Path "$dir\copy.dat") -and
        $run.Output -match 'only checked' -and $run.Output -match 'Kept copy\.dat' -and $run.Output -notmatch 'Downloaded'
    } $run

    # A finished file that doesn't match the checksum given: said so, and left as it was.
    $dir = New-Finished finishedwrongsum test.bin
    $run = Invoke-Mirrordl @('-o', $dir, "$dir\game.rar", (Url 18001), (Url 18002), (Hash "$work\other.bin"))
    Test-Case 'a finished file that misses the checksum is left as it was' {
        $run.Code -eq 2 -and (Hash "$dir\game.rar") -eq $hash -and @(Get-ChildItem $dir).Count -eq 1 -and
        $run.Output -match 'checksum is for another file'
    } $run

    # The same with a server that can't send parts: checked by the checksum, never downloaded over; without a
    # checksum only its size can be checked, and that is said.
    $dir = New-Finished finishednorange test.bin
    $withSum = Invoke-Mirrordl @('-o', $dir, "$dir\game.rar", (Url 18006), $hash)
    $sizeOnly = Invoke-Mirrordl @('-o', $dir, "$dir\game.rar", (Url 18006))
    Test-Case 'a finished file is only checked, also by a server without range requests' {
        $withSum.Code -eq 0 -and $sizeOnly.Code -eq 0 -and (Hash "$dir\game.rar") -eq $hash -and @(Get-ChildItem $dir).Count -eq 1 -and
        $withSum.Output -match 'only checked' -and $sizeOnly.Output -match 'nothing to compare its content with'
    } ([pscustomobject]@{ Code = "$($withSum.Code) / $($sizeOnly.Code)"; Output = $withSum.Output + $sizeOnly.Output })

    # An empty file under the download's name that isn't Firefox's placeholder (no Firefox .part next to it) may
    # be the user's own: it stays, and the download takes the next free name.
    $dir = New-Folder emptyname
    New-Item -ItemType File "$dir\test.bin" | Out-Null
    $run = Invoke-Mirrordl @('-o', $dir, (Url 18001), (Url 18002), $hash)
    Test-Case "an empty file of the user's under the same name stays" {
        $run.Code -eq 0 -and (Get-Item "$dir\test.bin").Length -eq 0 -and (Hash "$dir\test (1).bin") -eq $hash
    } $run

    # An empty file given to check it against an empty download: only checked, too.
    $dir = New-Folder finishedempty
    New-Item -ItemType File "$dir\notes.dat" | Out-Null
    Set-ItemProperty "$dir\notes.dat" IsReadOnly $true
    $run = Invoke-Mirrordl @('-o', $dir, "$dir\notes.dat", 'http://127.0.0.1:18008/empty.bin', 'http://127.0.0.1:18009/empty.bin',
        'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855')
    Test-Case 'an empty finished file is only checked' {
        $run.Code -eq 0 -and @(Get-ChildItem $dir).Count -eq 1 -and $run.Output -match 'only checked'
    } $run

    # MirrorDL's own unfinished file that is complete already (stopped just before it was finished): nothing to
    # download, and unlike a finished file it gets its final name.
    $dir = New-Folder completepart
    Copy-Item "$work\test.bin" "$dir\test.bin.part"
    [IO.File]::WriteAllText("$dir\test.bin.part.mirrordl", "mirrordl 2`nsize $(24MB)`nend`n")
    $run = Invoke-Mirrordl @('-o', $dir, (Url 18001), (Url 18002), $hash)
    Test-Case 'a complete unfinished file gets its final name' {
        $run.Code -eq 0 -and (Hash "$dir\test.bin") -eq $hash -and @(Get-ChildItem $dir).Count -eq 1 -and
        $run.Output -match 'complete already' -and $run.Output -notmatch 'only checked'
    } $run

    # A finished file that isn't the file behind the links: said so, and left exactly as it was.
    $dir = New-Finished finisheddiffers other.bin
    $otherHash = Hash "$dir\game.rar"
    $run = Invoke-Mirrordl @('-o', $dir, "$dir\game.rar", (Url 18001), (Url 18002))
    Test-Case 'a finished file of another file is left as it was' {
        $run.Code -ne 0 -and (Hash "$dir\game.rar") -eq $otherHash -and @(Get-ChildItem $dir).Count -eq 1 -and
        $run.Output -match 'not the same file'
    } $run

    # An empty file: nothing to download, still saved and checked.
    $dir = New-Folder empty
    $run = Invoke-Mirrordl @('-o', $dir, 'http://127.0.0.1:18008/empty.bin', 'http://127.0.0.1:18009/empty.bin',
        'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855')
    Test-Case 'empty file' { $run.Code -eq 0 -and (Get-Item "$dir\empty.bin").Length -eq 0 } $run

    # The unfinished file of another file must be refused and left alone.
    $dir = New-Folder wrong
    Copy-Start "$work\other.bin" "$dir\test.Q1w2E3r4.bin.part" (9MB)
    $before = Hash "$dir\test.Q1w2E3r4.bin.part"
    $run = Invoke-Mirrordl @('-o', $dir, (Url 18001), (Url 18002))
    Test-Case 'refuses a .part file of another file' {
        $run.Code -eq 1 -and $run.Output -match 'not the same file' -and (Hash "$dir\test.Q1w2E3r4.bin.part") -eq $before -and
        -not (Test-Path "$dir\test.Q1w2E3r4.bin.part.mirrordl")
    } $run

    # Killed without warning (as by a crash), then continued from the progress file.
    $dir = New-Folder resume
    $process = Start-Process $Exe -ArgumentList "-o `"$dir`" $(Url 18007)" -PassThru -NoNewWindow `
        -RedirectStandardOutput "$work\resume.log" -RedirectStandardError "$work\resume.err"
    $state = "$dir\test.bin.part.mirrordl"
    $saved = $false
    for ($i = 0; $i -lt 300 -and -not $saved; $i++) {
        Start-Sleep -Milliseconds 100
        # The first save after start lists everything as missing; a later one has progress.
        $saved = (Test-Path $state) -and ((Get-Content $state | Where-Object { $_ -like 'missing *' } |
            ForEach-Object { $p = $_.Split(' '); [long]$p[2] - [long]$p[1] } | Measure-Object -Sum).Sum -lt 24MB)
    }
    Stop-Process -Id $process.Id -Force
    $process.WaitForExit()
    $run = Invoke-Mirrordl @('-o', $dir, (Url 18001), (Url 18002), $hash)
    Test-Case 'hard kill, then resume' {
        $saved -and $run.Code -eq 0 -and (Hash "$dir\test.bin") -eq $hash -and $run.Output -match 'mirrordl progress'
    } $run

    # A server that ignores range requests: one connection, the whole file.
    $dir = New-Folder norange
    $run = Invoke-Mirrordl @('-o', $dir, (Url 18006), $hash)
    Test-Case 'server without range requests' { $run.Code -eq 0 -and (Hash "$dir\test.bin") -eq $hash } $run

    # One connection allowed, 403 for more: the link must stay in use.
    $dir = New-Folder oneconnection
    $run = Invoke-Mirrordl @('-o', $dir, (Url 18005), $hash)
    Test-Case 'server that allows one connection' { $run.Code -eq 0 -and (Hash "$dir\test.bin") -eq $hash } $run

    # A server whose connections share one speed limit: the 4 connections a link starts with already reach
    # it, so the 4 more that are tried bring nothing and leave again in the middle of their parts.
    $dir = New-Folder fullserver
    $run = Invoke-Mirrordl @('-o', $dir, 'http://127.0.0.1:18010/big.bin', (Hash "$work\big.bin"))
    Test-Case 'connections tried and taken back' { $run.Code -eq 0 -and (Hash "$dir\big.bin") -eq (Hash "$work\big.bin") } $run

    # A fast link and a very slow one: at the end, the fast one takes over what the slow one still has
    # instead of waiting for it (about 19 s without that, 8 s with it; 14 s leaves room for a slow runner).
    $dir = New-Folder endgame
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $run = Invoke-Mirrordl @('-o', $dir, (Url 18001), (Url 18007), $hash)
    $seconds = $watch.Elapsed.TotalSeconds
    Test-Case "a slow link's last parts are taken over ($([math]::Round($seconds, 1)) s)" {
        $run.Code -eq 0 -and (Hash "$dir\test.bin") -eq $hash -and $seconds -lt 14
    } $run

    # Links to two files at once, mixed, each checksum named for its file as in a SHA256SUMS file: two files, each
    # from its own links and checked against its own checksum.
    $dir = New-Folder multi
    $smallHash = Hash (Join-Path $work small.bin)
    $run = Invoke-Mirrordl @('-o', $dir, (Url 18001), 'http://127.0.0.1:18004/small.bin', (Url 18002),
        "$hash  test.bin", "$smallHash  small.bin")
    Test-Case 'links to several files at once' {
        $run.Code -eq 0 -and (Hash "$dir\test.bin") -eq $hash -and (Hash "$dir\small.bin") -eq $smallHash -and
        $run.Output -match 'lead to 2 files' -and $run.Output -match '2 files: 2 saved' -and
        ([regex]::Matches($run.Output, 'SHA256 matches')).Count -eq 2
    } $run

    # aria2 continued a Firefox file and stopped: its pieces are all over the file.
    if ($Aria2) {
        $dir = New-Folder aria2
        New-Item -ItemType File "$dir\test.bin" | Out-Null
        Copy-Start "$work\test.bin" "$dir\test.Ab12-xY_.bin.part" (4MB)
        & $Aria2 --continue=true --split=8 --max-connection-per-server=4 --min-split-size=1M --file-allocation=none `
            --auto-file-renaming=false --max-overall-download-limit=2M --stop=4 --summary-interval=0 `
            --console-log-level=error --download-result=hide "--dir=$dir" '--out=test.Ab12-xY_.bin.part' (Url 18001) (Url 18002) | Out-Null
        $aria2Left = Test-Path "$dir\test.Ab12-xY_.bin.part.aria2"
        $run = Invoke-Mirrordl @('-o', $dir, (Url 18001), (Url 18002), $hash)
        Test-Case 'download started by aria2' {
            $aria2Left -and $run.Code -eq 0 -and (Hash "$dir\test.bin") -eq $hash -and $run.Output -match 'aria2 progress' -and
            -not (Test-Path "$dir\test.Ab12-xY_.bin.part.aria2")
        } $run
    }
    else {
        Write-Host 'SKIP  download started by aria2 (no aria2c)' -ForegroundColor Yellow
    }
}
finally {
    Remove-Item Env:MIRRORDL_TEST_NO_RECYCLE_BIN -ErrorAction SilentlyContinue
    $mirrors | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 300
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failed.Count) {
    Write-Host "`n$($failed.Count) failed: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "`nAll passed." -ForegroundColor Green
