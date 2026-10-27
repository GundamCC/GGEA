param(
    [string]$Commit = 'HEAD',
    [switch]$FullTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
# 固定一次提交解析；后续不再从可能变化的工作区复制源文件、配置或文档。
$revision = [string](& git -C $root rev-parse --verify "$Commit^{commit}")
if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[a-f0-9]{40}$') { throw 'Invalid release commit' }
$work = Join-Path $root ('build/release-work-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $work 'source'
New-Item -ItemType Directory -Path $source -Force | Out-Null
$archive = Join-Path $work 'source.zip'
& git -C $root archive --format=zip "--output=$archive" $revision
if ($LASTEXITCODE -ne 0) { throw 'Source archive failed' }
Expand-Archive -LiteralPath $archive -DestinationPath $source
# 对刚导出的源码建立摘要证据，publish 在构建前后核验，防止快照被中途修改。
. (Join-Path $source 'tools/release-info.ps1')
[ordered]@{ commit = $revision; files = Get-ReleaseInventory $source } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $source '.release-source.json') -Encoding UTF8
Write-Host "Fixed source commit: $revision"
Push-Location $source
try {
    & (Join-Path $source 'tools/publish.ps1') -SourceCommit $revision -FullTests:$FullTests
    & (Join-Path $source 'tools/test-release.ps1') -PublishDirectory (Join-Path $source 'artifacts/publish') -SourceCommit $revision
    & (Join-Path $source 'tools/package-dist.ps1') -SourceCommit $revision -OutputDirectory (Join-Path $root 'artifacts/dist')
}
finally { Pop-Location }
# 保留快照、构建输出与测试证据用于定位失败，全部位于忽略的 build 下。
Write-Host "Build evidence: $work"
