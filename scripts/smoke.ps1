#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')]
    [string]$Runtime,
    [int]$TimeoutSeconds = 30
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$stage = Join-Path $repo "artifacts/stage/$Runtime/YandexImages"
$relative = switch -Wildcard ($Runtime) {
    'win-*' { 'YandexImages.exe' }
    'osx-*' { 'YandexImages.app/Contents/MacOS/YandexImages' }
    default { 'YandexImages' }
}
$executable = Join-Path $stage $relative
if (!(Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Приложение не упаковано: $executable" }
$start = [Diagnostics.ProcessStartInfo]::new()
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.WorkingDirectory = [IO.Path]::GetDirectoryName($executable)
if ($IsLinux) {
    $start.FileName = 'xvfb-run'
    $start.ArgumentList.Add('-a')
    $start.ArgumentList.Add($executable)
}
else { $start.FileName = $executable }
$start.ArgumentList.Add('--smoke-test')
$process = [Diagnostics.Process]::Start($start)
try {
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill($true)
        throw "Проверка запуска не завершилась за $TimeoutSeconds секунд."
    }
    $stdout.GetAwaiter().GetResult() | Write-Host
    $stderr.GetAwaiter().GetResult() | Write-Host
    if ($process.ExitCode -ne 0) { throw "Ошибка запуска приложения: $($process.ExitCode)" }
    Write-Host "Native startup passed: $Runtime"
}
finally { $process.Dispose() }
