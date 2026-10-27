#requires -Version 5.1
[CmdletBinding()]
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$output = Join-Path $PSScriptRoot '..\artifacts\YandexImages'
Push-Location $PSScriptRoot
try {
    if (!$SkipTests) {
        dotnet run --project (Join-Path $PSScriptRoot 'Tests\YandexImages.Tests.csproj') -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Проверки завершились с ошибкой.' }
    }
    dotnet publish (Join-Path $PSScriptRoot 'App\YandexImages.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $output
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать приложение. Если ранее собранный YandexImages.exe запущен, закройте его и повторите сборку.' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $output 'Инструкция.txt')
    Write-Host "Готово: $output\YandexImages.exe"
}
finally { Pop-Location }
