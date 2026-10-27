param([switch]$FullTests, [switch]$Package)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/GGEA.App/GGEA.App.csproj'
$solution = Join-Path $root 'GGEA.sln'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$version = [string]$projectXml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Invalid development version.' }
$commit = [string](& git -C $root rev-parse HEAD)
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[a-f0-9]{40}$') { throw 'Cannot identify source commit.' }
$dirty = @( & git -C $root status --porcelain --untracked-files=normal )
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect source worktree.' }

# 开发版保留 Debug 条件与当前工作区内容，不把它声明为经过固定提交验收的正式包。
& dotnet build $solution -c Debug
if ($LASTEXITCODE -ne 0) { throw 'Debug build failed.' }
# 默认仅运行核心检查，先生成可供用户实机体验的开发包；全量回归只在明确要求时运行。
& (Join-Path $PSScriptRoot 'test.ps1') -Configuration Debug -NoBuild -Full:$FullTests

$buildRoot = Join-Path $root 'build'
$destination = Join-Path $root 'dev'
$identifier = [Guid]::NewGuid().ToString('N')
$staging = Join-Path $buildRoot ".dev-building-$identifier"
$backup = Join-Path $buildRoot ".dev-backup-$identifier"
$backupForRecovery = $false
$packageName = "GGEA-dev-v$version-win-x64-$(Get-Date -Format 'yyyyMMdd-HHmmss')-$($identifier.Substring(0, 8))"
$packageStaging = Join-Path $buildRoot ".dev-package-$identifier"
$candidateZip = Join-Path $buildRoot ".dev-package-$identifier.zip"
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
. (Join-Path $PSScriptRoot 'publish-files.ps1')
try {
    & dotnet publish $project -c Debug -r win-x64 --self-contained true -p:PublishSingleFile=true -o $staging
    if ($LASTEXITCODE -ne 0) { throw 'Debug publish failed.' }
    Assert-NoReleaseManagerPrivateFiles -Directory $staging
    foreach ($required in @('GGEA.exe', 'OpenCvSharpExtern.dll', 'onnxruntime.dll', 'config/ggea.config.json', 'resources/common/templates', 'resources/common/ocr/en_PP-OCRv5_rec_mobile.onnx')) {
        if (-not (Test-Path -LiteralPath (Join-Path $staging $required))) { throw "Missing development output: $required" }
    }
    # 明示开发包的源码状态，防止未提交工作区产物被误认为正式发布包。
    [ordered]@{
        channel = 'Development'; configuration = 'Debug'; version = $version
        commit = $commit; worktreeDirty = ($dirty.Count -gt 0)
        builtAtUtc = [DateTime]::UtcNow.ToString('o')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $staging 'development-info.json') -Encoding UTF8
    if ($Package) {
        # 只压缩本次干净的 publish 结果，不读取 dev 中的用户配置、日志或运行中程序。
        $outputRoot = Join-Path $root 'artifacts/dev-dist'
        $packageDirectory = Join-Path $packageStaging $packageName
        New-Item -ItemType Directory -Path $packageStaging -Force | Out-Null
        Copy-DirectoryContents -Source $staging -Destination $packageDirectory
        Compress-Archive -LiteralPath $packageDirectory -DestinationPath $candidateZip -CompressionLevel Optimal
        $hash = (Get-FileHash -LiteralPath $candidateZip -Algorithm SHA256).Hash.ToLowerInvariant()
        New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
        $zip = Join-Path $outputRoot "$packageName.zip"
        Move-Item -LiteralPath $candidateZip -Destination $zip
        "$hash  $packageName.zip" | Set-Content -LiteralPath "$zip.sha256" -Encoding ASCII
        Write-Host "Development ZIP: $zip"
        Write-Host "SHA-256: $hash"
        Write-Host "Version: $version; source commit: $commit; worktree dirty: $($dirty.Count -gt 0)"
        return
    }
    # 固定写入仓库根目录 dev/，让该目录始终代表最近一次成功构建。
    # 替换前备份旧文件并检查占用；保留运行日志，失败时恢复旧包。
    if (Test-Path -LiteralPath $destination) {
        $destinationItem = Get-Item -LiteralPath $destination -Force
        if (-not $destinationItem.PSIsContainer) { throw "Development output path is not a directory: $destination" }
        Assert-PublishFilesAvailable -Directory $destination -Exclude @('logs')
        Copy-DirectoryContents -Source $destination -Destination $backup -Exclude @('logs')
    }
    Assert-PublishFilesAvailable -Directory $destination -Exclude @('logs')
    try {
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Clear-DirectoryContents -Directory $destination -Exclude @('logs')
        Copy-DirectoryContents -Source $staging -Destination $destination
        if (-not (Test-Path -LiteralPath (Join-Path $destination 'GGEA.exe') -PathType Leaf)) {
            throw "Development deployment failed: GGEA.exe is missing from $destination."
        }
    }
    catch {
        $deploymentError = $_
        if (Test-Path -LiteralPath $backup -PathType Container) {
            try {
                Copy-DirectoryContents -Source $backup -Destination $destination -SkipIdentical
                Clear-DirectoryContents -Directory $destination -Exclude @('logs') -KeepDirectory $backup
            }
            catch {
                $backupForRecovery = $true
                Write-Warning "Automatic rollback did not finish; the previous development build is preserved at $backup."
            }
        }
        else {
            try { Clear-DirectoryContents -Directory $destination -Exclude @('logs') }
            catch { Write-Warning "Could not clear the incomplete development directory: $destination" }
        }
        throw $deploymentError
    }
    Write-Host "Development build (latest): $destination"
    Write-Host "Version: $version; source commit: $commit; worktree dirty: $($dirty.Count -gt 0)"
}
finally {
    # 仅删除本次生成的暂存与回滚目录；回滚失败时保留备份供手动恢复。
    $base = [IO.Path]::GetFullPath($buildRoot).TrimEnd('\', '/')
    foreach ($path in @($staging, $packageStaging, $candidateZip, $(if (-not $backupForRecovery) { $backup }))) {
        if (-not $path) { continue }
        $target = [IO.Path]::GetFullPath($path)
        if ([IO.Directory]::GetParent($target).FullName.TrimEnd('\', '/') -ne $base -or
            [IO.Path]::GetFileName($target) -notmatch '^\.dev-(building|backup|package)-[a-f0-9]{32}(\.zip)?$') {
            throw "Unsafe development cleanup path: $target"
        }
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    }
}
