param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$sdk = if (Get-Command dotnet -ErrorAction SilentlyContinue) { 'dotnet' } else { Join-Path $projectRoot '.tools\dotnet\dotnet.exe' }
if (!(Test-Path $sdk) -and $sdk -ne 'dotnet') { throw 'Установите .NET 10 SDK.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $sdk publish (Join-Path $projectRoot 'src\AppFlow.Desktop\AppFlow.Desktop.csproj') -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $projectRoot "artifacts\$Runtime")
if ($LASTEXITCODE -ne 0) { throw 'Публикация не удалась.' }
Write-Host "Готово: $projectRoot\artifacts\$Runtime\AppFlow.exe"
