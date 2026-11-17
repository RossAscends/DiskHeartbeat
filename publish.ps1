param([string]$Configuration='Release',[switch]$NoRestore,[string]$OutputDirectory)
$project=Join-Path $PSScriptRoot 'DiskHeartbeat\DiskHeartbeat.csproj'
$destination=if([string]::IsNullOrWhiteSpace($OutputDirectory)){Join-Path $PSScriptRoot 'dist'}elseif([IO.Path]::IsPathRooted($OutputDirectory)){$OutputDirectory}else{Join-Path $PSScriptRoot $OutputDirectory}
$installedSdk=Get-Command dotnet -ErrorAction SilentlyContinue
$sdkPath=if($installedSdk){$installedSdk.Source}else{Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'}
if(-not(Test-Path -LiteralPath $sdkPath)){throw 'Install the .NET 8 SDK for Windows to build from source.'}
$publishArguments=@('publish',$project,'-c',$Configuration,'-r','win-x64','--self-contained','true','-p:PublishSingleFile=true','-p:IncludeAllContentForSelfExtract=true','-p:EnableCompressionInSingleFile=true','-p:DebugType=None','-p:DebugSymbols=false','-o',$destination)
if($NoRestore){$publishArguments+='--no-restore'}
& $sdkPath @publishArguments
if($LASTEXITCODE -ne 0){throw "Publishing failed (exit $LASTEXITCODE)"}
