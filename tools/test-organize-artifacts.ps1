# 在独立临时仓库中验证迁移的预览、文件保留、重复执行和冲突保护。
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $root ('build/migration-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $work 'tools') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'organize-artifacts.ps1') -Destination (Join-Path $work 'tools')
$script = Join-Path $work 'tools/organize-artifacts.ps1'

# 内容哨兵用于确认移动没有覆盖或丢失；不依赖真实本机依赖和发布包。
function Write-Fixture([string]$Relative, [string]$Content) {
    $path = Join-Path $work $Relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [IO.File]::WriteAllText($path, $Content)
}
function Assert-Fixture([string]$Relative, [string]$Content) {
    if ([IO.File]::ReadAllText((Join-Path $work $Relative)) -cne $Content) { throw "Content changed: $Relative" }
}
try {
    Write-Fixture 'artifacts/publish/config/settings.json' 'personal settings'
    Write-Fixture 'artifacts/dist/package.zip' 'package'
    Write-Fixture 'artifacts/tools-cache/jdk/tool.txt' 'toolchain'
    Write-Fixture 'artifacts/tools-cache/classes/cache.txt' 'intermediate'
    Write-Fixture 'artifacts/old-runtime/component.bin' 'runtime'
    Write-Fixture 'artifacts/captures/image.png' 'capture'
    Write-Fixture 'artifacts/unknown [old]/data.txt' 'historical'
    & $script -WhatIf
    Assert-Fixture 'artifacts/old-runtime/component.bin' 'runtime'
    if (Test-Path -LiteralPath (Join-Path $work 'runtime')) { throw 'Preview mutated workspace' }
    & $script
    Assert-Fixture 'archive/tools-cache/jdk/tool.txt' 'toolchain'
    Assert-Fixture 'archive/tools-cache/classes/cache.txt' 'intermediate'
    Assert-Fixture 'archive/old-runtime/component.bin' 'runtime'
    Assert-Fixture 'research/captures/image.png' 'capture'
    Assert-Fixture 'archive/unknown [old]/data.txt' 'historical'
    Assert-Fixture 'artifacts/publish/config/settings.json' 'personal settings'
    Assert-Fixture 'artifacts/dist/package.zip' 'package'
    & $script
    Write-Fixture 'artifacts/captures/image.png' 'new capture'
    $rejected = $false
    try { & $script } catch {
        if ($_.Exception.Message -notlike 'Destination already exists:*') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'Existing destination was not rejected' }
    Assert-Fixture 'artifacts/captures/image.png' 'new capture'
    Assert-Fixture 'research/captures/image.png' 'capture'
    Write-Host 'PASS: preview, migration, retained outputs, repeat execution and collision protection'
}
finally {
    $resolved = [IO.Path]::GetFullPath($work)
    $allowed = [IO.Path]::GetFullPath((Join-Path $root 'build'))
    if ([IO.Directory]::GetParent($resolved).FullName -ne $allowed -or
        [IO.Path]::GetFileName($resolved) -notmatch '^migration-test-[a-f0-9]{32}$') { throw 'Unsafe migration test cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
