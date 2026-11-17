param([Parameter(Mandatory=$true)][string]$DestinationRoot,[Parameter(Mandatory=$true)][string]$Model)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $workspace
$verificationDirectory=Join-Path $workspace '.tools\sata-verification'
New-Item -ItemType Directory -Force $verificationDirectory | Out-Null
$env:DOTNET_CLI_HOME=Join-Path $workspace '.tools\cli'
$env:APPDATA=Join-Path $workspace '.tools\appdata'
$env:NUGET_PACKAGES=Join-Path $workspace '.tools\packages'
$env:DISKHEARTBEAT_DATA_DIR=$verificationDirectory
$sdk=Join-Path $workspace '.tools\dotnet\dotnet.exe'
if(-not(Test-Path -LiteralPath $sdk)){$sdk=(Get-Command dotnet -ErrorAction Stop).Source}
$transcript=Join-Path $verificationDirectory 'transcript.txt'
try {
 Get-CimInstance Win32_DiskDrive | Select-Object Model,Index,Size | ConvertTo-Json | Set-Content (Join-Path $verificationDirectory 'inventory.json')
 Get-Partition | Select-Object DiskNumber,PartitionNumber,DriveLetter,Size | ConvertTo-Json | Set-Content (Join-Path $verificationDirectory 'partitions.json')
 & $sdk tools/bin/Release/net8.0-windows/Checks.dll --probe 2>&1 | Out-File $transcript
 & $sdk tools/bin/Release/net8.0-windows/Checks.dll --sata-copy-probe $DestinationRoot (Join-Path $verificationDirectory 'copy-result.json') $Model 2>&1 | Out-File $transcript -Append
 if($LASTEXITCODE -ne 0){throw "Native copy probe failed (exit $LASTEXITCODE); see transcript"}
 # Same production ETW path, now with an elevated token. Workload is inside the workspace.
 & $sdk tools/bin/Release/net8.0-windows/Checks.dll --attribution-probe 2>&1 | Out-File $transcript -Append
 if($LASTEXITCODE -ne 0){throw "ETW probe failed (exit $LASTEXITCODE); see transcript"}
 & $sdk tools/bin/Release/net8.0-windows/Checks.dll --published-probe .tools/publish 2>&1 | Out-File $transcript -Append
 if($LASTEXITCODE -ne 0){throw "Published native probe failed"}
 'complete' | Set-Content (Join-Path $verificationDirectory 'status.txt')
} catch {
 $_ | Out-String | Add-Content $transcript
 'failed' | Set-Content (Join-Path $verificationDirectory 'status.txt')
 exit 1
}
