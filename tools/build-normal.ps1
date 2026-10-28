param([switch]$Package)
$ErrorActionPreference = 'Stop'
# 发布管理器按 UTF-8 读取管道；Windows PowerShell 也必须使用相同编码。
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/GGEA.App/GGEA.App.csproj'
$artifacts = Join-Path $root 'artifacts'
$outputRoot = if ($Package) { Join-Path $artifacts 'dist' } else { Join-Path $root 'build' }
$destination = if ($Package) { $null } else { Join-Path $artifacts 'publish' }
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$version = [string]$projectXml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Invalid normal build version.' }
$commit = [string](& git -C $root rev-parse HEAD)
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[a-f0-9]{40}$') { throw 'Cannot identify source commit.' }
$dirty = @( & git -C $root status --porcelain --untracked-files=normal )
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect source worktree.' }

# 普通版本用于本地快速验收：读取当前工作区，写入 artifacts/publish，不声明固定提交验收身份。
$identifier = [Guid]::NewGuid().ToString('N')
$name = "GGEA-v$version-win-x64-$(Get-Date -Format 'yyyyMMdd-HHmmss')-$($identifier.Substring(0, 8))"
$packageDirectory = Join-Path $outputRoot $name
$workRoot = Join-Path $root 'build'
$staging = Join-Path $workRoot ".building-$identifier"
$candidateZip = Join-Path $workRoot ".building-$identifier.zip"
$zip = Join-Path $outputRoot "$name.zip"
New-Item -ItemType Directory -Path $outputRoot, $workRoot -Force | Out-Null
if (-not $Package) {
    . (Join-Path $PSScriptRoot 'publish-files.ps1')
    # 编译前先检查，保留部署前的再次校验以防构建期间重新打开程序。
    Assert-PublishFilesAvailable -Directory $destination -Exclude @('logs')
}
$backup = $null
$deploymentStarted = $false
$previousCliLanguage = $env:DOTNET_CLI_UI_LANGUAGE
try {
    # 嵌套 MSBuild Exec 的本地代码页不一致时会二次乱码；构建工具统一输出英文。
    $env:DOTNET_CLI_UI_LANGUAGE = 'en'
    & dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $staging
    if ($LASTEXITCODE -ne 0) { throw 'Normal build publish failed.' }
    . (Join-Path $PSScriptRoot 'publish-files.ps1')
    Assert-NoReleaseManagerPrivateFiles -Directory $staging
    # 与正式发布保持一致：图像识别不使用视频 I/O，移除 FFmpeg、调试符号和开发说明。
    # 仅清理本次发布的随机暂存目录，保留 OpenCvSharpExtern.dll 及运行资源。
    Get-ChildItem -LiteralPath $staging -Filter 'opencv_videoio_ffmpeg*.dll' -File | Remove-Item -Force
    Get-ChildItem -LiteralPath $staging -Recurse -File |
        Where-Object { $_.Extension -in @('.pdb', '.md') } | Remove-Item -Force
    foreach ($required in @('DependencySetup_依赖库安装.bat', 'GGEA.exe', 'OpenCvSharpExtern.dll', 'onnxruntime.dll', 'config/ggea.config.json',
            'resources/common/templates', 'resources/common/ocr/en_PP-OCRv5_rec_mobile.onnx')) {
        if (-not (Test-Path -LiteralPath (Join-Path $staging $required))) { throw "Missing normal build output: $required" }
    }
    # 清单仅记录本地来源，不参与正式发布的 build-info 身份校验。
    [ordered]@{
        'channel' = 'LocalValidation'; 'configuration' = 'Release'; 'version' = $version
        'commit' = $commit; 'worktreeDirty' = ($dirty.Count -gt 0)
        'builtAtUtc' = [DateTime]::UtcNow.ToString('o')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $staging 'local-build-info.json') -Encoding UTF8
    if ($Package) {
        Move-Item -LiteralPath $staging -Destination $packageDirectory
    }
    else {
        # 原位更新 publish，先检查占用并备份旧内容；保留运行日志，失败时恢复旧包。
        Assert-PublishFilesAvailable -Directory $destination -Exclude @('logs')
        $previousOutputExisted = Test-Path -LiteralPath $destination
        if ($previousOutputExisted) {
            $backupName = 'publish-backup-normal-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $identifier.Substring(0, 8)
            $backup = Join-Path (Join-Path $root 'backups') $backupName
            Copy-DirectoryContents -Source $destination -Destination $backup -Exclude @('logs')
            # 新包继续使用已有 ADB、区服语言和 AP 方案，不能把每次本地构建变成首次启动。
            Copy-PublishUserFiles -Source $backup -Destination $staging
        }
        Assert-PublishFilesAvailable -Directory $destination -Exclude @('logs')
        $deploymentStarted = $true
        try {
            New-Item -ItemType Directory -Path $destination -Force | Out-Null
            Clear-DirectoryContents -Directory $destination -Exclude @('logs')
            Copy-DirectoryContents -Source $staging -Destination $destination
            if (-not (Test-Path -LiteralPath (Join-Path $destination 'GGEA.exe') -PathType Leaf)) {
                throw "Normal build deployment failed: GGEA.exe is missing from $destination."
            }
        }
        catch {
            $deploymentError = $_
            if ($deploymentStarted -and $backup -and (Test-Path -LiteralPath $backup)) {
                try {
                    Copy-DirectoryContents -Source $backup -Destination $destination -SkipIdentical
                    Clear-DirectoryContents -Directory $destination -Exclude @('logs') -KeepDirectory $backup
                }
                catch { Write-Warning "Automatic rollback did not finish; the previous package backup is at $backup." }
            }
            elseif ($deploymentStarted -and -not $previousOutputExisted) {
                try { Clear-DirectoryContents -Directory $destination -Exclude @('logs') }
                catch { Write-Warning "Could not clear the incomplete new publish directory: $destination" }
            }
            throw $deploymentError
        }
    }
    $buildOutput = if ($Package) { $packageDirectory } else { $destination }
    Write-Host "Build: $buildOutput"
    Write-Host "Version: $version; source commit: $commit; worktree dirty: $($dirty.Count -gt 0)"

    if ($Package) {
        Compress-Archive -LiteralPath $packageDirectory -DestinationPath $candidateZip -CompressionLevel Optimal
        Move-Item -LiteralPath $candidateZip -Destination $zip
        $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $name.zip" | Set-Content -LiteralPath "$zip.sha256" -Encoding ASCII
        Write-Host "ZIP: $zip"
        Write-Host "SHA-256: $hash"
    }
}
finally {
    $env:DOTNET_CLI_UI_LANGUAGE = $previousCliLanguage
    # 只清理本次随机暂存文件；失败时保留已完成的独立目录供排查。
    $base = [IO.Path]::GetFullPath($workRoot).TrimEnd('\', '/')
    foreach ($path in @($staging, $candidateZip)) {
        $target = [IO.Path]::GetFullPath($path)
        if ([IO.Directory]::GetParent($target).FullName.TrimEnd('\', '/') -ne $base -or
            [IO.Path]::GetFileName($target) -cnotmatch "^\.building-$identifier(\.zip)?$") {
            throw "Unsafe normal build cleanup path: $target"
        }
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    }
}
