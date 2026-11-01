# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Nawid Salehie
<#
Talks to the Microsoft Store through the Microsoft Store Developer CLI (msstore), which the action
microsoft/microsoft-store-apppublisher installs. store.yml submits with it, store-notes.yml sends a
release's notes on their own, store-status.yml asks where the submission is.

  store.ps1 -Action submit -ProductId <Store ID> -Version 1.2.3 -Package <path to .msix>
  store.ps1 -Action notes  -ProductId <Store ID> -Version 1.2.3
  store.ps1 -Action status -ProductId <Store ID> -Version 1.2.3

Signs in first, with TENANT_ID, SELLER_ID, CLIENT_ID and CLIENT_SECRET from the environment. submit and
notes read the release's notes on GitHub with gh (GH_TOKEN, GH_REPO) and put its changes into the Store's
"What's new in this version". The status action writes state (live, failed, waiting, gone or unknown)
and status (Microsoft's word) to GITHUB_OUTPUT.
#>
param(
    [Parameter(Mandatory)] [ValidateSet('submit', 'notes', 'status')] [string] $Action,
    [Parameter(Mandatory)] [string] $ProductId,
    [Parameter(Mandatory)] [string] $Version,
    [string] $Package
)
$ErrorActionPreference = 'Stop'
$storeVersion = "$Version.0" # the Store counts four parts: v1.2.3 is 1.2.3.0

# Microsoft's names for where a submission is.
$underWay = 'CommitStarted', 'PreProcessing', 'Certification', 'Release', 'PendingPublication', 'Publishing'
$failed = 'CommitFailed', 'PreProcessingFailed', 'CertificationFailed', 'ReleaseFailed', 'PublishFailed', 'Canceled'

function Invoke-Msstore {
    # The output without the colors msstore puts in.
    $output = & msstore @args 2>&1 | Out-String
    [pscustomobject]@{ Code = $LASTEXITCODE; Text = $output -replace "\x1b\[[0-9;]*m", '' }
}

function Connect-Store {
    $missing = 'TENANT_ID', 'SELLER_ID', 'CLIENT_ID', 'CLIENT_SECRET' | Where-Object { -not [Environment]::GetEnvironmentVariable($_) }
    if ($missing) { throw "These secrets are missing: $($missing -join ', ') (docs/microsoft-store.md)." }
    for ($attempt = 1; ; $attempt++) {
        $result = Invoke-Msstore reconfigure --tenantId $env:TENANT_ID --sellerId $env:SELLER_ID --clientId $env:CLIENT_ID --clientSecret $env:CLIENT_SECRET
        if ($result.Code -eq 0) { return }
        if ($attempt -eq 3) {
            throw "Signing in to Partner Center failed 3 times. If the key has expired, add a new one in Partner Center and " +
                  "replace the secret AZURE_AD_APPLICATION_SECRET (docs/microsoft-store.md).`n$($result.Text)"
        }
        Write-Host "Signing in failed; trying again in $(30 * $attempt) s."
        Start-Sleep -Seconds (30 * $attempt)
    }
}

# The Store's current submission - the pending one, or else the last published one: where it is, and
# whether it holds this version.
function Get-Submission {
    $status = Invoke-Msstore submission status $ProductId
    $name = if ($status.Text -match '(?s).*Status\s*[-=]\s*(\w+)') { $Matches[1] } else { 'Unknown' }
    $details = Invoke-Msstore submission get $ProductId
    [pscustomobject]@{ Status = $name; Ours = $details.Text.Contains("`"$storeVersion`""); Text = $status.Text.Trim() }
}

# The Store's "What's new in this version" for this release: the changes its notes on GitHub list (the
# pull requests' titles, without who made them and the type in front), and a link to the release. Changes
# that leave the app as it is (ci:, docs:, test: and the like) are left out. The Store takes at most 1500
# characters; changes that don't fit are left to the link. If GitHub can't be read, the link alone (the
# notes are no reason to hold up a release) - unless the notes are all there is to send (-Required).
function Get-ReleaseNotes([switch] $Required) {
    if (-not $env:GH_REPO) { throw 'GH_REPO is missing: the repository, like owner/name.' }
    $body = (gh release view "v$Version" --json body --jq .body) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        if ($Required) { throw "Reading the notes of the release v$Version on GitHub failed." }
        Write-Host "::warning::Reading the notes of the release v$Version on GitHub failed; the Store gets only the link to it."
        $body = ''
    }
    $changes = @()
    $inList = $false
    foreach ($line in $body -split '\r?\n') {
        if ($line -match '^#+\s') { $inList = $line -match "^## What.s Changed"; continue }
        if ($inList -and $line -match '^\* (.+?) by @\S+ in https://\S+$') {
            $title = $Matches[1]
            if ($title -cmatch '^(build|chore|ci|docs|refactor|style|test)(\(.*?\))?!?:') { continue }
            $title = $title -creplace '^[a-z]+(\(.*?\))?!?:\s*'
            if ($title) { $changes += '- ' + $title.Substring(0, 1).ToUpper() + $title.Substring(1) }
        }
    }
    $link = "https://github.com/$env:GH_REPO/releases/tag/v$Version"
    if (-not $changes) { return "What changed in this version: $link" }
    for ($shown = $changes.Count; ; $shown--) {
        $lines = @($changes | Select-Object -First $shown)
        if ($shown -lt $changes.Count) { $lines += '- and more' }
        $notes = ($lines -join "`n") + "`n`nDetails on GitHub: $link"
        if ($notes.Length -le 1500) { return $notes }
    }
}

# The Store's current submission as msstore gives it: the pending one, or else the last published one.
function Read-Submission {
    $output = & msstore submission get $ProductId 2>&1
    # The JSON is all msstore writes to stdout, and comes last; what it says on the way goes to stderr.
    $text = ($output | Where-Object { $_ -is [string] }) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $text -notmatch '(?ms)^\{.*') { throw "Reading the Store's submission failed.`n$($output | Out-String)" }
    $options = [System.Text.Json.Nodes.JsonNodeOptions]@{ PropertyNameCaseInsensitive = $true }
    # Whole: PowerShell would hand on the object's entries one by one.
    Write-Output -NoEnumerate ([System.Text.Json.Nodes.JsonNode]::Parse($Matches[0], $options))
}

# Hands msstore a changed submission. A file, not an argument: the JSON can be longer than a command line.
function Update-Submission($submission) {
    $file = Join-Path ([IO.Path]::GetTempPath()) "store-submission-$PID.json"
    [IO.File]::WriteAllText($file, $submission.ToJsonString())
    try { $result = Invoke-Msstore submission update $ProductId $file }
    finally { Remove-Item $file }
    if ($result.Code -ne 0) { Write-Host $result.Text }
    $result.Code -eq 0
}

# Puts the notes into the Store's draft submission and leaves all else in it as it is. The draft is the
# one msstore publish --noCommit left, or else a new one, a copy of the published submission: msstore
# makes it when asked to change a submission while there is no draft.
function Set-ReleaseNotes([string] $notes) {
    $submission = Read-Submission
    if ("$($submission['Status'])" -ne 'PendingCommit') {
        if (-not (Update-Submission $submission)) { Write-Host 'Making a draft submission failed.' }
        $submission = Read-Submission
        if ("$($submission['Status'])" -ne 'PendingCommit') {
            Write-Host "There is no draft submission to put the notes in: the submission is at $($submission['Status'])."
            return $false
        }
    }
    $listing = $submission['Listings']?['en-us']?['BaseListing']
    if ($null -eq $listing) { throw "The Store's submission has no English (en-us) listing to put the notes in." }
    $listing['ReleaseNotes'] = [System.Text.Json.Nodes.JsonNode] $notes
    Update-Submission $submission
}

# Commits the draft and waits until Microsoft has taken it on, as msstore publish does without --noCommit.
function Complete-Submission {
    $commit = Invoke-Msstore submission publish $ProductId
    Write-Host $commit.Text
    if ($commit.Code -ne 0) { return $false }
    # Waits while the commit is being processed (CommitStarted), which takes a few minutes.
    $poll = Invoke-Msstore submission poll $ProductId
    Write-Host $poll.Text
    (Get-Submission).Status -notin $failed + 'PendingCommit' + 'Unknown'
}

if ($Action -ne 'status') {
    $notes = Get-ReleaseNotes -Required:($Action -eq 'notes')
    Write-Host "What's new in this version:`n$notes`n"
}
Connect-Store

if ($Action -eq 'submit') {
    for ($attempt = 1; ; $attempt++) {
        # Already there, or under way - from an earlier try, or a re-run of this job? A new publish would delete
        # that submission and start over, so it is left alone.
        $submission = Get-Submission
        if ($submission.Ours -and $submission.Status -in $underWay + 'Published') {
            Write-Host "The submission of $storeVersion is under way already ($($submission.Status))."
            break
        }
        if ($attempt -gt 3) { throw "Submitting $storeVersion failed 3 times; the Store's submission is at $($submission.Status)." }
        # Uploads the package into a new draft (replacing a draft an earlier try left), puts this release's
        # notes in, and commits it.
        & msstore publish (Resolve-Path $Package).Path -id $ProductId --noCommit
        if ($LASTEXITCODE -eq 0) {
            # Without its notes the version goes out all the same, with the last version's; the run says so.
            $withNotes = try { Set-ReleaseNotes $notes } catch { Write-Host $_.Exception.Message; $false }
            if (-not $withNotes) { Write-Host "::warning::Putting the release's notes into the Store's submission failed; it keeps the last version's. Send them later with the Store notes workflow (docs/microsoft-store.md)." }
            if (Complete-Submission) { break }
        }
        # It went wrong on the way - perhaps only after Microsoft had taken it on, which the Store may not show
        # at once: wait before looking again.
        Write-Host "Submitting failed; looking again in $(60 * $attempt) s."
        Start-Sleep -Seconds (60 * $attempt)
    }
    $submission = Get-Submission
    "MirrorDL $storeVersion is submitted to the Microsoft Store: $($submission.Status)." | Tee-Object -FilePath $env:GITHUB_STEP_SUMMARY -Append
}
elseif ($Action -eq 'notes') {
    # Only for the version the Store has, and not while a submission of it is still under way: a new
    # submission can't start then.
    $submission = Get-Submission
    if (-not $submission.Ours) { throw "The Store's submission does not hold $storeVersion; the notes go only with the version the Store has." }
    if ($submission.Status -in $underWay) { throw "A submission of $storeVersion is under way ($($submission.Status)); send the notes once it is live." }
    if (-not ((Set-ReleaseNotes $notes) -and (Complete-Submission))) { throw "Sending the notes of $storeVersion to the Store failed." }
    "The notes of MirrorDL $storeVersion are submitted to the Microsoft Store: $((Get-Submission).Status)." | Tee-Object -FilePath $env:GITHUB_STEP_SUMMARY -Append
}
else {
    $submission = Get-Submission
    Write-Host $submission.Text
    $state = if (-not $submission.Ours) { 'gone' }
        elseif ($submission.Status -eq 'Published') { 'live' }
        elseif ($submission.Status -in $failed) { 'failed' }
        elseif ($submission.Status -in $underWay) { 'waiting' }
        else { 'unknown' }
    Write-Host "MirrorDL $storeVersion in the Store: $state ($($submission.Status))"
    "state=$state" >> $env:GITHUB_OUTPUT
    "status=$($submission.Status)" >> $env:GITHUB_OUTPUT
}
