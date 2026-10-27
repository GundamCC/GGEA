param(
    [ValidateSet("win-x64")][string]$Runtime = "win-x64",
    [string]$SourceCommit = "",
    [switch]$FullTests
)

$ErrorActionPreference = "Stop"

# 脚本位于 tools/ 下，仓库根目录是其上一级
# 部分执行环境下 $PSScriptRoot / $MyInvocation 为空，逐级退化
$scriptDir = $null
if ($PSScriptRoot) { $scriptDir = $PSScriptRoot }
elseif ($MyInvocation.MyCommand.Path) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
elseif ($MyInvocation.MyCommand.Definition) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition }
if (-not $scriptDir) { $scriptDir = (Get-Location).Path }

$root = Split-Path -Parent $scriptDir
if (-not (Test-Path (Join-Path $root "GGEA.sln"))) { $root = $scriptDir }
# 独立调用只允许干净提交；release.ps1 从 Git 导出的不可变快照传入完整提交号。
. (Join-Path $root "tools/release-info.ps1")
if (-not $SourceCommit) {
    $SourceCommit = [string](& git -C $root rev-parse HEAD)
    if ($LASTEXITCODE -ne 0) { throw "Cannot resolve source commit" }
    $dirty = & git -C $root status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -ne 0 -or $dirty) { throw "Commit source changes before publishing, or use release.ps1 -Commit <commit>." }
}
if ($SourceCommit -notmatch '^[a-f0-9]{40}$') { throw "Invalid source commit" }
Assert-ReleaseSource $root $SourceCommit
$version = Get-ReleaseVersion $root
& (Join-Path $root 'tools/test-release-notes.ps1')
Assert-ReleaseResources $root
& dotnet build (Join-Path $root 'GGEA.sln') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Release build failed' }
# 发布默认验证核心边界；版本、资源、摘要及 EXE 自检仍由原有发布流程完整执行。
& (Join-Path $root 'tools/test.ps1') -Configuration Release -NoBuild -Full:$FullTests
& (Join-Path $root 'tools/test-publish-files.ps1')
$project = Join-Path $root "src\GGEA.App\GGEA.App.csproj"
$artifacts = Join-Path $root "artifacts"
$output = Join-Path $artifacts "publish"
# Publish to a unique staging directory so a failed build cannot corrupt the live package.
$staging = Join-Path (Join-Path $root "build") ("publish-staging-" + [Guid]::NewGuid().ToString("N"))

. (Join-Path $root "tools\publish-files.ps1")

# Verify the destructive target once and never derive it from user-provided input.
$resolvedArtifacts = [IO.Path]::GetFullPath($artifacts).TrimEnd([IO.Path]::DirectorySeparatorChar)
$resolvedOutput = [IO.Path]::GetFullPath($output).TrimEnd([IO.Path]::DirectorySeparatorChar)
$outputParent = [IO.Directory]::GetParent($resolvedOutput).FullName.TrimEnd([IO.Path]::DirectorySeparatorChar)
if ($outputParent -ne $resolvedArtifacts -or [IO.Path]::GetFileName($resolvedOutput) -ne "publish") {
    throw "Unsafe publish output path: $resolvedOutput"
}

# 编译前检查所有待替换文件，避免只检查 EXE 而漏掉旧助手的 DLL。
Assert-PublishFilesAvailable -Directory $output -Exclude @("logs")

Write-Host "Publishing GGEA ($Runtime, self-contained, single-file)..."

try {
    # Follow agent.md section 10: Release + win-x64 + self-contained + single-file.
    dotnet publish $project `
        -c Release `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:GgeaReleaseCommit=$SourceCommit `
        -o $staging

    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed with exit code $LASTEXITCODE"
    }

    Assert-NoReleaseManagerPrivateFiles -Directory $staging

    $stagedExe = Join-Path $staging "GGEA.exe"
    if (-not (Test-Path -LiteralPath $stagedExe -PathType Leaf)) {
        throw "Publish verification failed: $stagedExe was not generated."
    }

    # Remove the unused OpenCV FFmpeg videoio native module.
    # GGEA only uses ImDecode / MatchTemplate / MinMaxLoc (core + imgproc),
    # never video I/O, so this DLL is dead weight in the package.
    Get-ChildItem -LiteralPath $staging -Filter "opencv_videoio_ffmpeg*.dll" -File |
        Remove-Item -Force

    # 清单覆盖最终分发内容；文档与配置在同一源码快照中，打包阶段不再补写。
    Copy-Item -LiteralPath (Join-Path $root 'RELEASE.txt') -Destination $staging
    Get-ChildItem -LiteralPath $staging -Recurse -File | Where-Object { $_.Extension -in @('.pdb', '.md') } | Remove-Item -Force
    Write-ReleaseInfo $staging $version $SourceCommit $Runtime
    Assert-ReleaseInfo $staging $version $SourceCommit $Runtime
    Assert-ReleaseSource $root $SourceCommit

    # 发布目录完全以本次构建的内容为准：配置、模板与程序一并覆盖，
    # 确保新版本的默认参数不会被旧发布目录中的配置静默覆盖。

    # Back up the previous package before changing any live files.
    $backup = $null
    if (Test-Path -LiteralPath $output) {
        $backupName = "publish-backup-" + (Get-Date -Format "yyyyMMdd-HHmmss") + "-" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
        $backup = Join-Path (Join-Path $root "backups") $backupName
        # Logs are runtime data and may be open in a viewer; they are not part of the package backup.
        Copy-DirectoryContents -Source $output -Destination $backup -Exclude @("logs")
    }

    # 构建/备份期间可能出现新的占用，再次确认后才开始修改现有文件。
    Assert-PublishFilesAvailable -Directory $output -Exclude @("logs")
    if ($backup) { Write-Host "Previous package backup: $backup" }
    $deploymentStarted = $true
    try {
        # Replace contents in place because Explorer may hold the publish directory itself open.
        New-Item -ItemType Directory -Path $output -Force | Out-Null
        Clear-DirectoryContents -Directory $output -Exclude @("logs")
        Copy-DirectoryContents -Source $staging -Destination $output

        if (-not (Test-Path -LiteralPath (Join-Path $output "GGEA.exe") -PathType Leaf)) {
            throw "Deployment verification failed: GGEA.exe is missing from $output."
        }
    }
    catch {
        $deploymentError = $_
        # 先覆盖恢复备份，再清理额外文件；不能重走导致失败的目录删除路径。
        if ($deploymentStarted -and $backup -and (Test-Path -LiteralPath $backup)) {
            try {
                Copy-DirectoryContents -Source $backup -Destination $output -SkipIdentical
                Clear-DirectoryContents -Directory $output -Exclude @("logs") -KeepDirectory $backup
            }
            catch { Write-Warning "自动恢复未完成，完整备份位于 $backup。请先关闭文件占用，再从备份恢复。" }
        }
        throw $deploymentError
    }

    Write-Host "Done: $output"
}
finally {
    # Remove only the unique staging directory created by this invocation.
    if (Test-Path -LiteralPath $staging) {
        $resolvedStaging = [IO.Path]::GetFullPath($staging)
        if ([IO.Directory]::GetParent($resolvedStaging).FullName -ne [IO.Path]::GetFullPath((Join-Path $root "build")) -or
            [IO.Path]::GetFileName($resolvedStaging) -notmatch '^publish-staging-[a-f0-9]{32}$') {
            throw "Unsafe staging cleanup path: $resolvedStaging"
        }
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}
