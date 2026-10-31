# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Nawid Salehie
#
# Builds the release files into .\publish:
#   MirrorDL-win-x64.zip            the window app, for GitHub
#   mirrordl-terminal-win-x64.zip   the terminal program, for GitHub
#   MirrorDL-x64.msix               with -Store: the window app as an MSIX package for the Microsoft Store
#
# release.yml runs it with the version of the release tag and, with -Store, the app's identity
# from Partner Center. Without arguments it makes a local build, version 0.0.0-dev.
param(
    [string]$Version = '0.0.0-dev',
    [switch]$Store,
    # Partner Center, the app's "Product identity" page: Package/Identity/Name, Package/Identity/Publisher
    # and Package/Properties/PublisherDisplayName.
    [string]$IdentityName,
    [string]$Publisher,
    [string]$PublisherDisplayName
)
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'publish'
$app = Join-Path $PSScriptRoot 'MirrorDL.App\MirrorDL.App.csproj'
$terminal = Join-Path $PSScriptRoot 'MirrorDL\MirrorDL.csproj'

dotnet publish $app -c Release -o "$out\app" "-p:Version=$Version"
if ($LASTEXITCODE) { throw 'Building the app failed.' }
dotnet publish $terminal -c Release -o "$out\terminal" "-p:Version=$Version"
if ($LASTEXITCODE) { throw 'Building the terminal version failed.' }
Compress-Archive -Path "$out\app\*" -DestinationPath "$out\MirrorDL-win-x64.zip" -Force
Compress-Archive -Path "$out\terminal\mirrordl.exe" -DestinationPath "$out\mirrordl-terminal-win-x64.zip" -Force

if ($Store) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "The Store needs a release version like 1.2.3, not '$Version'." }
    foreach ($name in 'IdentityName', 'Publisher', 'PublisherDisplayName') {
        if (-not (Get-Variable $name -ValueOnly)) { throw "-Store needs -$name, from the app's Product identity page in Partner Center." }
    }
    # The identity goes into the manifest only for this build; the file is put back afterwards.
    $manifestPath = Join-Path $PSScriptRoot 'MirrorDL.App\Package.appxmanifest'
    $original = [IO.File]::ReadAllText($manifestPath)
    try {
        $manifest = [xml]$original
        $manifest.Package.Identity.Name = $IdentityName
        $manifest.Package.Identity.Publisher = $Publisher
        $manifest.Package.Identity.Version = "$Version.0"
        $manifest.Package.Properties.PublisherDisplayName = $PublisherDisplayName
        $manifest.Save($manifestPath)
        # Packaged, with .NET inside: the Store has no .NET runtime to install alongside. Unsigned:
        # the Store signs what it publishes.
        dotnet publish $app -c Release "-p:Version=$Version" -p:WindowsPackageType=MSIX -p:SelfContained=true `
            -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=false -p:AppxBundle=Never -p:AppxSymbolPackageEnabled=false `
            "-p:AppxPackageDir=$out/msix/"
        if ($LASTEXITCODE) { throw 'Building the MSIX package failed.' }
    }
    finally {
        [IO.File]::WriteAllText($manifestPath, $original)
    }
    $msix = Get-ChildItem "$out\msix" -Recurse -Filter *.msix | Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $msix) { throw 'The MSIX build ran, but no .msix file came out of it.' }
    Copy-Item $msix.FullName "$out\MirrorDL-x64.msix" -Force
}

Get-ChildItem "$out\*.zip", "$out\*.msix" | ForEach-Object { '{0}  {1:N1} MB' -f $_.Name, ($_.Length / 1MB) }
