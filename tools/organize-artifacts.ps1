# 将旧 artifacts 布局迁到本机资料区；不删除、不覆盖，重复运行只处理尚未迁移的项。
[CmdletBinding(SupportsShouldProcess)]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifacts = Join-Path $root 'artifacts'
$moves = [Collections.Generic.List[object]]::new()

# 仅接受仓库内的普通路径；拒绝路径祖先中的链接，避免移动到工作区之外。
function Assert-LocalPath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path outside workspace: $resolved"
    }
    for ($cursor = $resolved; $cursor -ne $root; $cursor = [IO.Directory]::GetParent($cursor).FullName) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Linked path cannot be migrated: $cursor"
            }
        }
    }
}

# 先建立完整计划并检查冲突，再开始迁移；未知旧资料按原名归档，不推断其可删除性。
function Add-Migration([string]$Source, [string]$RelativeDestination) {
    $destination = Join-Path $root $RelativeDestination
    Assert-LocalPath $Source
    Assert-LocalPath $destination
    if (Test-Path -LiteralPath $destination) { throw "Destination already exists: $destination" }
    $moves.Add([pscustomobject]@{ source = $Source; destination = $destination })
}

if (-not (Test-Path -LiteralPath $artifacts)) { return }
foreach ($item in Get-ChildItem -LiteralPath $artifacts -Force) {
    $name = $item.Name
    if ($name -in @('publish', 'publish-dev', 'dist')) { continue }
    $destination = if ($name -like 'publish-backup-*') { "backups/$name" }
        elseif ($name -like 'release-work-*' -or $name -eq 'dist-staging') { "build/$name" }
        elseif ($name -eq 'test-results') { 'test-results' }
        elseif ($item.Extension -eq '.log') { "logs/$name" }
        elseif ($name -in @('captures', 'screenshots', 'debug') -or $name -like 'logo-*' -or $name -like '*review*') { "research/$name" }
        else { "archive/$name" }
    Add-Migration $item.FullName $destination
}
# 活跃程序所在目录不迁移；发布目录本身始终保留原位。
foreach ($process in Get-Process) {
    if (-not $process.Path) { continue }
    foreach ($move in $moves) {
        if ($process.Path.StartsWith($move.source + '\', [StringComparison]::OrdinalIgnoreCase) -or $process.Path -eq $move.source) {
            throw "Close process before migration: $($process.ProcessName) ($($process.Id))"
        }
    }
}
$journal = Join-Path (Join-Path $root 'logs') ('migration-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N') + '.jsonl')
foreach ($move in $moves) {
    if ($PSCmdlet.ShouldProcess($move.source, "Move to $($move.destination)")) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $move.destination) -Force | Out-Null
        New-Item -ItemType Directory -Path (Split-Path -Parent $journal) -Force | Out-Null
        Move-Item -LiteralPath $move.source -Destination $move.destination
        $move | ConvertTo-Json -Compress | Add-Content -LiteralPath $journal -Encoding UTF8
    }
}
Write-Host "Migration entries: $($moves.Count); journal: $journal"
