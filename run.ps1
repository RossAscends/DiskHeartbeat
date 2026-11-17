$project = Join-Path $PSScriptRoot 'DiskHeartbeat\DiskHeartbeat.csproj'
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnetCommand) { $sdkPath = $dotnetCommand.Source }
else { $sdkPath = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $sdkPath)) { throw 'Install the .NET 8 SDK for Windows, then run this script again.' }
& $sdkPath run --project $project -c Release
exit $LASTEXITCODE
