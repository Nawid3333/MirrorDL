# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Nawid Salehie
<#
Talks to the Microsoft Store through the Microsoft Store Developer CLI (msstore), which the action
microsoft/microsoft-store-apppublisher installs. store.yml submits with it, store-status.yml asks
where the submission is.

  store.ps1 -Action submit -ProductId <Store ID> -Version 1.2.3 -Package <path to .msix>
  store.ps1 -Action status -ProductId <Store ID> -Version 1.2.3

Signs in first, with TENANT_ID, SELLER_ID, CLIENT_ID and CLIENT_SECRET from the environment. The
status action writes state (live, failed, waiting, gone or unknown) and status (Microsoft's word) to
GITHUB_OUTPUT.
#>
param(
    [Parameter(Mandatory)] [ValidateSet('submit', 'status')] [string] $Action,
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
        # Uploads the package, commits the submission, and waits until Microsoft has taken it on.
        & msstore publish (Resolve-Path $Package).Path -id $ProductId
        if ($LASTEXITCODE -eq 0) { break }
        # It went wrong on the way - perhaps only after Microsoft had taken it on, which the Store may not show
        # at once: wait before looking again.
        Write-Host "Submitting failed; looking again in $(60 * $attempt) s."
        Start-Sleep -Seconds (60 * $attempt)
    }
    $submission = Get-Submission
    "MirrorDL $storeVersion is submitted to the Microsoft Store: $($submission.Status)." | Tee-Object -FilePath $env:GITHUB_STEP_SUMMARY -Append
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
