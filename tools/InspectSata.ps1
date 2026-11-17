param([Parameter(Mandatory=$true)][string]$Model)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $workspace
$folder=Join-Path $workspace '.tools\sata-verification'
& .tools/dotnet/dotnet.exe tools/bin/Release/net8.0-windows/Checks.dll --ata-inspect $folder $Model 2>&1 | Out-File (Join-Path $folder 'inspect-transcript.txt')
