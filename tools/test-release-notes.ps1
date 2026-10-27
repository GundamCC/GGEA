$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'release-info.ps1')

# 只接受预期的失败原因，防止其它脚本错误被误判为测试通过。
function Assert-Rejected([scriptblock]$Action, [string]$Reason) {
    try { & $Action }
    catch {
        if ($_.Exception.Message -notlike "*$Reason*") { throw }
        Write-Host "PASS: rejected $Reason"
        return
    }
    throw "Expected rejection: $Reason"
}

function Assert-Equal([string]$Actual, [string]$Expected, [string]$Name) {
    if ($Actual -cne $Expected) { throw "Unexpected result: $Name" }
    Write-Host "PASS: $Name"
}

# 检查仓库现有版本历史与分发摘要同步，兼顾章节解析和实际发布入口。
$version = Get-ReleaseVersion $root
Assert-Equal (Get-CurrentReleaseNotes $root $version).TrimEnd() (Get-Content -LiteralPath (Join-Path $root 'RELEASE.txt') -Raw -Encoding UTF8).TrimEnd() 'current release notes match CHANGELOG.md'

$fixtureRoot = Join-Path $root ('build/release-notes-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
try {
    $changeLogPath = Join-Path $fixtureRoot 'CHANGELOG.md'
    $releasePath = Join-Path $fixtureRoot 'RELEASE.txt'
    $validChangeLog = @'
# GGEA 更新历史 / Changelog

## v2.0.0.0 — 正式发布 / Release

只属于新版本的更新。

## v1.0.0.0 — 正式发布 / Release

历史版本更新。
'@
    [IO.File]::WriteAllText($changeLogPath, $validChangeLog, [Text.UTF8Encoding]::new($false))
    $section = Get-ChangeLogVersionSection $changeLogPath '2.0.0.0'
    Assert-Equal $section '只属于新版本的更新。' 'version section stops before the next release'

    [IO.File]::WriteAllText($changeLogPath, $validChangeLog, [Text.UTF8Encoding]::new($false))
    $validRelease = "GGEA v2.0.0.0 — 正式发布 / Release`n`n$section`n"
    [IO.File]::WriteAllText($releasePath, $validRelease, [Text.UTF8Encoding]::new($false))
    Assert-Equal (Get-CurrentReleaseNotes $fixtureRoot '2.0.0.0').TrimEnd() $validRelease.TrimEnd() 'matching release summary is accepted'

    $duplicateChangeLog = $validChangeLog + "`n## v2.0.0.0 — 正式发布 / Release`n`n重复章节。`n"
    [IO.File]::WriteAllText($changeLogPath, $duplicateChangeLog, [Text.UTF8Encoding]::new($false))
    Assert-Rejected { Get-ChangeLogVersionSection $changeLogPath '2.0.0.0' } 'Duplicate CHANGELOG.md version section'

    [IO.File]::WriteAllText($changeLogPath, $validChangeLog, [Text.UTF8Encoding]::new($false))
    Assert-Rejected { Get-ChangeLogVersionSection $changeLogPath '3.0.0.0' } 'section missing'

    $emptySection = "# GGEA`n`n## v2.0.0.0 — 正式发布 / Release`n`n## v1.0.0.0 — 正式发布 / Release`n`n历史。`n"
    [IO.File]::WriteAllText($changeLogPath, $emptySection, [Text.UTF8Encoding]::new($false))
    Assert-Rejected { Get-ChangeLogVersionSection $changeLogPath '2.0.0.0' } 'section is empty'

    [IO.File]::WriteAllText($changeLogPath, $validChangeLog, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($releasePath, $validRelease + "`n不属于当前版本的历史更新。", [Text.UTF8Encoding]::new($false))
    Assert-Rejected { Get-CurrentReleaseNotes $fixtureRoot '2.0.0.0' } 'must match'
}
finally {
    $resolved = [IO.Path]::GetFullPath($fixtureRoot)
    $allowed = [IO.Path]::GetFullPath((Join-Path $root 'build'))
    if ([IO.Directory]::GetParent($resolved).FullName -ne $allowed -or [IO.Path]::GetFileName($resolved) -notmatch '^release-notes-test-[a-f0-9]{32}$') {
        throw 'Unsafe release-notes test cleanup path.'
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
