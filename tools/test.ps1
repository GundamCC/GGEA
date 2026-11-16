# 统一快速、相关及全量测试入口；相关测试与核心检查合并执行，避免重复构建和重复运行。
[CmdletBinding(DefaultParameterSetName = 'Quick')]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [Parameter(Mandatory = $true, ParameterSetName = 'Related')]
    [ValidatePattern('\S')][string]$Filter,
    [Parameter(Mandatory = $true, ParameterSetName = 'Full')][switch]$Full,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'tests/GGEA.Tests/GGEA.Tests.csproj'
$settings = if ($Full -or $PSCmdlet.ParameterSetName -eq 'Related') { 'full.runsettings' } else { 'quick.runsettings' }
# CI 显式接收 TRX，本地结果仅供本次运行使用。
$results = $env:GGEA_TEST_RESULTS_DIRECTORY
$temporary = -not $results
if ($temporary) {
    $results = Join-Path ([IO.Path]::GetTempPath()) ('ggea-tests-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $results | Out-Null
}
try {
    $arguments = @('test', $project, '-c', $Configuration, '--settings', (Join-Path $root "tests/$settings"),
        '--logger', 'trx', '--results-directory', $results)
    if ($NoBuild) { $arguments += '--no-build' }
    if ($PSCmdlet.ParameterSetName -eq 'Related') {
        # 相关模式使用无筛选的设置，再合并核心边界；避免 runsettings 与命令行筛选取交集漏跑相关用例。
        $arguments += @('--filter', "(Category=Smoke)|($Filter)")
    }
    $scope = if ($Full) { 'Full regression (explicit request)' }
        elseif ($PSCmdlet.ParameterSetName -eq 'Related') { "Smoke + related: $Filter" }
        else { 'Quick smoke checks' }
    Write-Host "Testing: $scope; configuration: $Configuration"
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $coreArguments = @('test', (Join-Path $root 'tests/GGEA.Core.Tests/GGEA.Core.Tests.csproj'), '-c', $Configuration,
        '--logger', 'trx', '--results-directory', $results)
    if ($NoBuild) { $coreArguments += '--no-build' }
    & dotnet @coreArguments
    if ($LASTEXITCODE -ne 0) { throw "Core tests failed, exit code: $LASTEXITCODE" }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Tests failed ($scope), exit code: $LASTEXITCODE" }
    Write-Host ('Test command completed in {0:N1}s. Emulator experience requires user validation.' -f $timer.Elapsed.TotalSeconds)
}
finally {
    if ($temporary) {
        if ([IO.Directory]::GetParent($results).FullName.TrimEnd('\','/') -ne [IO.Path]::GetTempPath().TrimEnd('\','/') -or
            [IO.Path]::GetFileName($results) -notmatch '^ggea-tests-[a-f0-9]{32}$') { throw 'Unsafe test results path' }
        Remove-Item -LiteralPath $results -Recurse -Force
    }
}
