# 读取指定版本在完整更新历史中的唯一章节，避免把累计内容带入单次 Release。
function Get-ChangeLogVersionSection([string]$ChangeLogPath, [string]$Version) {
    if (-not (Test-Path -LiteralPath $ChangeLogPath -PathType Leaf)) {
        throw 'CHANGELOG.md is missing.'
    }

    $content = [IO.File]::ReadAllText($ChangeLogPath, [Text.Encoding]::UTF8)
    $lines = $content -split "`r?`n"
    $headingPattern = '^## v(?<version>\d+\.\d+\.\d+\.\d+) — 正式发布 / Release$'
    $headings = [Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -match $headingPattern) {
            $headings.Add([pscustomobject]@{ Version = $Matches.version; Index = $index })
        }
    }
    if ($headings.Count -eq 0) { throw 'CHANGELOG.md has no version sections.' }
    $duplicates = @($headings | Group-Object Version | Where-Object Count -gt 1)
    if ($duplicates.Count -gt 0) { throw "Duplicate CHANGELOG.md version section: $($duplicates[0].Name)" }

    $selected = @($headings | Where-Object { $_.Version -ceq $Version })
    if ($selected.Count -ne 1) { throw "CHANGELOG.md section missing for v$Version." }

    $sectionLines = [Collections.Generic.List[string]]::new()
    for ($index = $selected[0].Index + 1; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -match '^##\s+') { break }
        $sectionLines.Add($lines[$index])
    }
    $section = ($sectionLines -join "`n").Trim()
    if (-not $section) { throw "CHANGELOG.md section is empty for v$Version." }
    return $section
}

# RELEASE.txt 是随程序分发的本版摘要，必须逐字对应历史文件里的当前版本章节。
function Get-CurrentReleaseNotes([string]$SourceRoot, [string]$Version) {
    $section = Get-ChangeLogVersionSection (Join-Path $SourceRoot 'CHANGELOG.md') $Version
    $releasePath = Join-Path $SourceRoot 'RELEASE.txt'
    if (-not (Test-Path -LiteralPath $releasePath -PathType Leaf)) { throw 'RELEASE.txt is missing.' }

    $expected = "GGEA v$Version — 正式发布 / Release`n`n$section"
    $actual = [IO.File]::ReadAllText($releasePath, [Text.Encoding]::UTF8)
    $normalize = {
        param([string]$Text)
        return $Text.Replace("`r`n", "`n").Replace("`r", "`n").TrimEnd([char[]]@("`r", "`n"))
    }
    if ((& $normalize $actual) -cne (& $normalize $expected)) {
        throw "RELEASE.txt must match the v$Version section in CHANGELOG.md."
    }
    return $actual
}
