param([Parameter(Mandatory = $true)][string]$PublishDirectory, [Parameter(Mandatory = $true)][string]$SourceCommit)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-info.ps1')
$root = Split-Path -Parent $PSScriptRoot
$version = Get-ReleaseVersion $root
$work = Join-Path $root ('build/release-test-' + [Guid]::NewGuid().ToString('N'))

# 必须因预期原因失败，防止测试把无关脚本异常视为有效拒绝。
function Assert-Rejected([scriptblock]$Action, [string]$Reason) {
    try { & $Action }
    catch {
        if ($_.Exception.Message -notlike "*$Reason*") { throw }
        Write-Host "PASS: rejected $Reason"
        return
    }
    throw "Expected rejection: $Reason"
}
try {
    Copy-DirectoryContents $PublishDirectory $work -Exclude @('logs')
    Assert-ReleaseInfo $work $version $SourceCommit
    $logDirectory = Join-Path $work 'logs'
    New-Item -ItemType Directory -Path $logDirectory | Out-Null
    $log = Join-Path $logDirectory 'session.log'
    [IO.File]::WriteAllText($log, 'runtime log')
    Assert-ReleaseInfo $work $version $SourceCommit -AllowRuntimeLogs
    Assert-Rejected { Assert-ReleaseInfo $work $version $SourceCommit } 'Release file set mismatch'
    Remove-Item -LiteralPath $log
    Assert-Rejected { Assert-ReleaseInfo $work $version ('0' * 40) } 'Build identity mismatch'
    Assert-Rejected { Assert-ReleaseInfo $work '99.0.0.0' $SourceCommit } 'Build identity mismatch'
    $config = Join-Path $work 'config/ggea.config.json'
    $original = [IO.File]::ReadAllBytes($config)
    [IO.File]::AppendAllText($config, ' ')
    Assert-Rejected { Assert-ReleaseInfo $work $version $SourceCommit } 'Release hash mismatch'
    [IO.File]::WriteAllBytes($config, $original)
    $extra = Join-Path $work 'unexpected.txt'
    [IO.File]::WriteAllText($extra, 'unexpected')
    Assert-Rejected { Assert-ReleaseInfo $work $version $SourceCommit } 'Release file set mismatch'
    Remove-Item -LiteralPath $extra
    $native = Join-Path $work 'OpenCvSharpExtern.dll'
    Move-Item -LiteralPath $native -Destination "$native.missing"
    Assert-Rejected { Assert-ReleaseInfo $work $version $SourceCommit } 'Release hash mismatch'
    Move-Item -LiteralPath "$native.missing" -Destination $native
    # 即使有人修改清单版本，EXE 自身版本仍必须阻止旧程序套新版本号。
    $infoPath = Join-Path $work 'build-info.json'
    $originalInfo = [IO.File]::ReadAllBytes($infoPath)
    $info = Get-Content -LiteralPath $infoPath -Raw -Encoding UTF8 | ConvertFrom-Json
    # 文件摘要和版本即使未改，伪造提交号也必须被真实 EXE 中的元数据识别。
    $info.commit = '0' * 40
    $info | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $infoPath -Encoding UTF8
    Assert-Rejected { Test-ReleaseExecutable $work } '程序构建提交与 build-info 不一致'
    $info.commit = $SourceCommit
    $info.version = '99.0.0.0'
    $info | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $infoPath -Encoding UTF8
    Assert-Rejected { Assert-ReleaseInfo $work '99.0.0.0' $SourceCommit } 'EXE version mismatch'
    [IO.File]::WriteAllBytes($infoPath, $originalInfo)
    # 真实打包入口也须拒绝损坏文件，失败后不得出现 ZIP。
    [IO.File]::AppendAllText($config, ' ')
    $failedDist = Join-Path $work 'failed-dist'
    Assert-Rejected { & (Join-Path $PSScriptRoot 'package-dist.ps1') -PublishDirectory $work -SourceCommit $SourceCommit -OutputDirectory $failedDist } 'Release hash mismatch'
    if (Test-Path -LiteralPath $failedDist) { throw 'Failed packaging created output' }
    [IO.File]::WriteAllBytes($config, $original)
    Assert-ReleaseInfo $work $version $SourceCommit
}
finally {
    $resolved = [IO.Path]::GetFullPath($work)
    $allowed = [IO.Path]::GetFullPath((Join-Path $root 'build'))
    if ([IO.Directory]::GetParent($resolved).FullName -ne $allowed -or [IO.Path]::GetFileName($resolved) -notmatch '^release-test-[a-f0-9]{32}$') { throw 'Unsafe regression cleanup path' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
