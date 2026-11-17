param([string]$Configuration='Release',[switch]$NoRestore)
$ErrorActionPreference='Stop'
[xml]$project=Get-Content (Join-Path $PSScriptRoot 'DiskHeartbeat\DiskHeartbeat.csproj') -Raw
$version=[string]$project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if($version -notmatch '^\d+\.\d+\.\d+([-.][A-Za-z0-9.-]+)?$'){throw 'The project must declare a release version'}
$artifactDirectory=Join-Path $PSScriptRoot 'artifacts'
$stage=Join-Path $artifactDirectory ('stage-'+$version+'-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $stage | Out-Null
& (Join-Path $PSScriptRoot 'publish.ps1') -Configuration $Configuration -NoRestore:$NoRestore -OutputDirectory $stage
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.md') -Destination $stage
@'
Disk Heartbeat - Windows x64

Extract this ZIP into a writable folder and run DiskHeartbeat.exe.
The EXE includes .NET and its libraries: no runtime install or smartctl is needed.
Run as administrator for native SATA telemetry and per-process write attribution.

Metrics and ratings are saved in Data\history.json beside the EXE.
An existing LocalAppData history imports automatically only when no portable
history exists. Close any old instance before launching this one.
Minimize to keep recording in the tray. Closing the window exits.
Keep the Data folder when upgrading. This ZIP contains no user's metric data.

Source, supported counter mappings and full instructions:
https://github.com/RossAscends/DiskHeartbeat

Third-party license notices are included in licenses/ and
THIRD-PARTY-NOTICES.md. They are not additional runtime installations.
'@ | Set-Content (Join-Path $stage 'README.txt') -Encoding UTF8
$assets=Get-Content (Join-Path $PSScriptRoot 'DiskHeartbeat\obj\project.assets.json') -Raw | ConvertFrom-Json
$packageRoots=@($assets.packageFolders.PSObject.Properties.Name)
$licenseRoot=Join-Path $stage 'licenses'
New-Item -ItemType Directory -Force $licenseRoot | Out-Null
function Copy-Notices([string]$relativePackage){
 foreach($root in $packageRoots){
  $package=Join-Path $root $relativePackage
  if(-not(Test-Path -LiteralPath $package)){continue}
  $files=@(Get-ChildItem -LiteralPath $package -File | Where-Object {$_.Name -match '(?i)^(LICENSE|THIRD.PARTY.NOTICES)'})
  if($files.Count -gt 0){
   $name=$relativePackage.Replace('/','-').Replace('\','-')
   $folder=Join-Path $licenseRoot $name
   New-Item -ItemType Directory -Force $folder | Out-Null
   foreach($file in $files){Copy-Item -LiteralPath $file.FullName -Destination $folder}
  }
  return
 }
}
foreach($library in $assets.libraries.PSObject.Properties){if($library.Value.type -eq 'package'){Copy-Notices $library.Value.path}}
$runtimeConfigPath=Join-Path $PSScriptRoot ('DiskHeartbeat\bin\'+$Configuration+'\net8.0-windows\win-x64\DiskHeartbeat.runtimeconfig.json')
$runtimeConfig=Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
foreach($framework in $runtimeConfig.runtimeOptions.includedFrameworks){Copy-Notices ($framework.name.ToLowerInvariant()+'.runtime.win-x64/'+$framework.version)}
$archive=Join-Path $artifactDirectory ('DiskHeartbeat-v'+$version+'-win-x64.zip')
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -CompressionLevel Optimal -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Update)
try {
 foreach($entry in $zip.Entries){$entry.LastWriteTime=[DateTimeOffset]::Parse('2000-01-01T00:00:00Z');$entry.ExternalAttributes=0}
} finally {$zip.Dispose()}
$checksum=Get-FileHash -LiteralPath $archive -Algorithm SHA256
($checksum.Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($archive)) | Set-Content (Join-Path $artifactDirectory 'SHA256SUMS.txt') -Encoding ASCII
Get-Item -LiteralPath $archive | Select-Object FullName,Length
