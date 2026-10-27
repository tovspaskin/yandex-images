#requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$SkipTests,
    [switch]$NoArchive
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts'))
$publish = [IO.Path]::GetFullPath((Join-Path $artifacts "publish/$Runtime"))
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'

Push-Location $repo
try {
    if (!$SkipTests) {
        dotnet run --project Tests/YandexImages.Tests.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Проверки завершились с ошибкой.' }
    }

    # Only replace this script's generated RID directory inside repository artifacts.
    $expected = [IO.Path]::GetFullPath((Join-Path $repo "artifacts/publish/$Runtime"))
    if ($publish -ne $expected -or !$publish.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar)) {
        throw 'Небезопасный путь выходной папки.'
    }
    for ($current = [IO.DirectoryInfo]::new($publish); $current -and $current.FullName -ne $repo; $current = $current.Parent) {
        if ((Test-Path -LiteralPath $current.FullName) -and
            ((Get-Item -LiteralPath $current.FullName).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Выходной путь не должен проходить через символические ссылки.'
        }
    }
    if (Test-Path -LiteralPath $publish) {
        Remove-Item -LiteralPath $publish -Recurse -Force
    }
    dotnet publish App/YandexImages.csproj -c Release -r $Runtime --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false `
        -p:DebugType=None -p:DebugSymbols=false -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать приложение.' }
    Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination (Join-Path $publish 'README.md')
    if (!$NoArchive) {
        [xml]$project = Get-Content -LiteralPath (Join-Path $repo 'App/YandexImages.csproj') -Raw
        $version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
        if (!$version) { $version = '1.0.0' }
        $python = Get-Command python -ErrorAction SilentlyContinue
        if (!$python) { $python = Get-Command python3 -ErrorAction Stop }
        & $python.Source (Join-Path $PSScriptRoot 'package.py') --rid $Runtime --publish-dir $publish `
            --output-dir (Join-Path $artifacts 'packages') --version $version
        if ($LASTEXITCODE -ne 0) { throw 'Не удалось упаковать приложение.' }
    }
    Write-Host "Готово: $publish"
}
finally { Pop-Location }
