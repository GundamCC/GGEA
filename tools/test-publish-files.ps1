$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "publish-files.ps1")

# 用当前进程的 Windows 工作目录持有目录句柄，不依赖原生调用或外部测试库。
$workspace = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $workspace ("build\publish-files-test-" + [Guid]::NewGuid().ToString("N"))
$output = Join-Path $testRoot "publish [locked]"
$source = Join-Path $testRoot "staging"
$backup = Join-Path $testRoot "backup"
$previousDirectory = [Environment]::CurrentDirectory
$fileLock = $null

# 每个测试仅写独占临时目录，路径含方括号以验证字面路径行为。
function Write-TestFile([string]$Root, [string]$Relative, [string]$Value) {
    $path = Join-Path $Root $Relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, $Value)
}
function Assert-Content([string]$Root, [string]$Relative, [string]$Expected) {
    if ([IO.File]::ReadAllText((Join-Path $Root $Relative)) -ne $Expected) { throw "Unexpected content: $Relative" }
}
try {
    Write-TestFile $output "runtime-fixture\legacy.bin" "old runtime"
    Write-TestFile $output "config\settings.json" "old settings"
    Write-TestFile $output "logs\session.log" "keep logs"
    Write-TestFile $source "runtime-fixture\lite.bin" "new runtime"
    Write-TestFile $source "config\settings.json" "new settings"
    [Environment]::CurrentDirectory = Join-Path $output "runtime-fixture"
    Assert-PublishFilesAvailable $output -Exclude @("logs")
    Copy-DirectoryContents $output $backup -Exclude @("logs")
    Clear-DirectoryContents $output -Exclude @("logs")
    # 空目录仍被当前进程持有：旧实现删除它会失败。
    $directoryBlocked = $false
    try { [IO.Directory]::Delete([Environment]::CurrentDirectory) } catch { $directoryBlocked = $true }
    if (-not $directoryBlocked) { throw "Fixture did not hold directory open" }
    Copy-DirectoryContents $source $output
    Assert-Content $output "runtime-fixture\lite.bin" "new runtime"
    Assert-Content $output "config\settings.json" "new settings"
    Assert-Content $output "logs\session.log" "keep logs"
    if (Test-Path -LiteralPath (Join-Path $output "runtime-fixture\legacy.bin")) { throw "Old runtime file survived" }
    Write-Host "PASS: held directory permits replacement; stale files removed; logs preserved"

    # 文件被占用时应在任何删除之前失败，原有文件完整保留。
    $fileLock = [IO.File]::Open((Join-Path $output "runtime-fixture\lite.bin"), 'Open', 'Read', 'Read')
    $blocked = $false
    try { Assert-PublishFilesAvailable $output -Exclude @("logs") } catch { $blocked = $true }
    if (-not $blocked) { throw "Locked file was not detected" }
    Assert-Content $output "config\settings.json" "new settings"
    Assert-Content $output "runtime-fixture\lite.bin" "new runtime"
    $fileLock.Dispose(); $fileLock = $null
    Write-Host "PASS: file lock rejected before mutation"

    # 模拟部分发布后失败：先恢复备份，再移除新版本额外文件，不再删除目录。
    Copy-DirectoryContents $backup $output -SkipIdentical
    Clear-DirectoryContents $output -Exclude @("logs") -KeepDirectory $backup
    Assert-Content $output "runtime-fixture\legacy.bin" "old runtime"
    Assert-Content $output "config\settings.json" "old settings"
    Assert-Content $output "logs\session.log" "keep logs"
    if (Test-Path -LiteralPath (Join-Path $output "runtime-fixture\lite.bin")) { throw "Rollback left new runtime file" }
    $fileLock = [IO.File]::Open((Join-Path $output "runtime-fixture\legacy.bin"), 'Open', 'Read', 'Read')
    Write-TestFile $output "config\settings.json" "partial update"
    Copy-DirectoryContents $backup $output -SkipIdentical
    Assert-Content $output "config\settings.json" "old settings"
    Write-Host "PASS: rollback works with held directory and unchanged locked file"

    # 普通版原位更新继承用户配置、作品位置和独立插件；旧主程序和模板不能混入新版。
    Write-TestFile $backup 'config\ggea.config.json' '{"adbDiscoveryCompleted":true,"gameLanguage":"ja"}'
    Write-TestFile $backup 'resources\common\stage_work_positions.json' 'user positions'
    Write-TestFile $backup 'resources\common\templates\old.png' 'old template'
    Write-TestFile $backup 'plugins\example\plugin.dll' 'installed plugin'
    Write-TestFile $source 'config\new-default.json' 'new default'
    Copy-PublishUserFiles $backup $source
    Assert-Content $source 'config\ggea.config.json' '{"adbDiscoveryCompleted":true,"gameLanguage":"ja"}'
    Assert-Content $source 'config\settings.json' 'old settings'
    Assert-Content $source 'config\new-default.json' 'new default'
    Assert-Content $source 'resources\common\stage_work_positions.json' 'user positions'
    Assert-Content $source 'plugins\example\plugin.dll' 'installed plugin'
    if (Test-Path -LiteralPath (Join-Path $source 'resources\common\templates\old.png')) { throw 'Old template copied into new build' }
    if (Test-Path -LiteralPath (Join-Path $source 'runtime-fixture\legacy.bin')) { throw 'Old program copied into new build' }
    Write-Host 'PASS: normal build preserves user configuration without overwriting new program resources'
}
finally {
    if ($fileLock) { $fileLock.Dispose() }
    [Environment]::CurrentDirectory = $previousDirectory
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $allowed = [IO.Path]::GetFullPath((Join-Path $workspace "build")) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe test cleanup path" }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
