# Builds both programs into .\publish and zips each one for a GitHub release.
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'publish'

dotnet publish "$PSScriptRoot\MirrorDL.App\MirrorDL.App.csproj" -c Release -o "$out\app"
if ($LASTEXITCODE) { throw 'Building the app failed.' }
dotnet publish "$PSScriptRoot\MirrorDL\MirrorDL.csproj" -c Release -o "$out\terminal"
if ($LASTEXITCODE) { throw 'Building the terminal version failed.' }

Compress-Archive -Path "$out\app\*" -DestinationPath "$out\MirrorDL-win-x64.zip" -Force
Compress-Archive -Path "$out\terminal\mirrordl.exe" -DestinationPath "$out\mirrordl-terminal-win-x64.zip" -Force
Get-ChildItem "$out\*.zip" | ForEach-Object { '{0}  {1:N1} MB' -f $_.Name, ($_.Length / 1MB) }
