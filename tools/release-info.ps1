# 发布身份与资源校验共用实现；清单描述发布文件本身，打包不得从源码覆盖内容。
. (Join-Path $PSScriptRoot "publish-files.ps1")
. (Join-Path $PSScriptRoot "release-notes.ps1")

# 使用与 PowerShell 版本和系统区域无关的序数排序，保证两个 PowerShell 运行时得到同一内容版本。
function Get-ContentIdentity($Files, [string]$Prefix) {
    $keys = [string[]]@($Files.Keys | Where-Object { $_.StartsWith($Prefix, [StringComparison]::Ordinal) })
    [Array]::Sort($keys, [StringComparer]::Ordinal)
    $lines = @($keys | ForEach-Object { "$_=$($Files[$_])" })
    if ($lines.Count -eq 0) { throw "Empty resource group: $Prefix" }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

# 只接纳发布文件，拒绝链接，逐个摘要还可以检测文件遗漏与额外混入。
function Get-ReleaseInventory([string]$Directory, [string[]]$Exclude = @()) {
    $base = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    $files = [ordered]@{}
    foreach ($file in @(Get-PublishFiles $base -Exclude $Exclude | Sort-Object FullName)) {
        $relative = $file.FullName.Substring($base.Length + 1).Replace('\', '/')
        if ($relative -eq 'build-info.json') { continue }
        $files[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return $files
}

# 解析 JSON 并校验其中显式引用的资源路径，不虚构模板或忽略缺失资源。
function Assert-ReleaseResources([string]$Directory) {
    foreach ($notice in @('model.json', 'NOTICE.txt', 'PADDLE-LICENSE.txt', 'RAPIDOCR-LICENSE.txt', 'ONNX-RUNTIME-LICENSE.txt', 'ONNX-RUNTIME-ThirdPartyNotices.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Directory "resources/common/ocr/$notice") -PathType Leaf)) {
            throw "Missing OCR resource or license: $notice"
        }
    }
    # OCR 模型属于实际运行资源，校验固定身份，防止遗漏或混入错误字典的模型。
    $ocrManifest = Get-Content -LiteralPath (Join-Path $Directory 'resources/common/ocr/model.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $ocrModel = Join-Path $Directory 'resources/common/ocr/en_PP-OCRv5_rec_mobile.onnx'
    if ((Get-Item -LiteralPath $ocrModel).Length -ne $ocrManifest.bytes -or
        (Get-FileHash -LiteralPath $ocrModel -Algorithm SHA256).Hash -ine $ocrManifest.sha256) {
        throw 'OCR model size or SHA-256 mismatch'
    }
    foreach ($group in @('config', 'resources')) {
        $jsonFiles = @(Get-ChildItem -LiteralPath (Join-Path $Directory $group) -Recurse -File -Filter '*.json')
        if ($jsonFiles.Count -eq 0) { throw "Missing JSON resources: $group" }
        foreach ($file in $jsonFiles) {
            $raw = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
            $null = $raw | ConvertFrom-Json
            foreach ($match in [regex]::Matches($raw, '"(resources/[^"\r\n]+)"')) {
                $relative = $match.Groups[1].Value
                $resolved = Join-Path $Directory $relative
                if ($relative -notmatch '^resources/(common|zh-CN|ja|en|ko|zh-TW|zh-HK)/') {
                    $suffix = $relative.Substring('resources/'.Length)
                    $language = 'zh-CN'
                    if ($file.FullName -match '[\\/](zh-CN|ja|en|ko|zh-TW|zh-HK)[\\/]') { $language = $Matches[1] }
                    $resolved = Join-Path $Directory "resources/$language/$suffix"
                    if (-not (Test-Path -LiteralPath $resolved)) { $resolved = Join-Path $Directory "resources/common/$suffix" }
                }
                if ($relative.Contains('..') -or -not (Test-Path -LiteralPath $resolved)) {
                    throw "Missing or invalid resource reference in $($file.Name): $relative"
                }
            }
        }
    }
}

# 构建前后校验固定源码；独立调用则要求提交和干净工作区同时匹配。
function Assert-ReleaseSource([string]$SourceRoot, [string]$Commit) {
    $snapshot = Join-Path $SourceRoot '.release-source.json'
    if (Test-Path -LiteralPath $snapshot) {
        $source = Get-Content -LiteralPath $snapshot -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($source.commit -cne $Commit) { throw 'Source snapshot commit mismatch' }
        foreach ($file in $source.files.PSObject.Properties) {
            if ((Get-FileHash -LiteralPath (Join-Path $SourceRoot $file.Name) -Algorithm SHA256).Hash -ne $file.Value) {
                throw "Source snapshot changed: $($file.Name)"
            }
        }
    }
    else {
        $head = [string](& git -C $SourceRoot rev-parse HEAD)
        if ($LASTEXITCODE -ne 0 -or $head -cne $Commit) { throw 'Source commit mismatch' }
        $dirty = & git -C $SourceRoot status --porcelain --untracked-files=normal
        if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Source worktree is not clean' }
    }
}

# 版本来源唯一；文档必须同步，否则在构建前失败。
function Get-ReleaseVersion([string]$SourceRoot) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $SourceRoot 'src/GGEA.App/GGEA.App.csproj') -Raw
    $version = [string]$project.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Invalid release version' }
    foreach ($document in @('README.md', '使用说明.txt', 'RELEASE.txt')) {
        $content = Get-Content -LiteralPath (Join-Path $SourceRoot $document) -Raw -Encoding UTF8
        # 分别报告版本和渠道，避免文档漏同步时误导为发布渠道配置错误。
        if ($content -notmatch [regex]::Escape("v$version")) { throw "Version mismatch in ${document}: expected v$version. Synchronize release documents with GGEA.App.csproj." }
        if ($content -notmatch '正式发布 / Release') { throw "Release channel missing in ${document}: expected Release channel marker." }
        foreach ($match in [regex]::Matches($content, 'v(\d+\.\d+\.\d+\.\d+)')) {
            if ($match.Groups[1].Value -ne $version) { throw "Stale version in $document" }
        }
    }
    # 版本历史可含旧版本，但当前分发说明必须对应当前章节且只包含本版内容。
    $null = Get-CurrentReleaseNotes $SourceRoot $version
    return $version
}

# 清单最后生成；文件集合、资源版本和 EXE 文件/产品版本必须同时一致。
function Write-ReleaseInfo([string]$Directory, [string]$Version, [string]$Commit, [string]$Runtime) {
    $files = Get-ReleaseInventory $Directory
    [ordered]@{
        schemaVersion = 1; version = $Version; channel = 'Release'; commit = $Commit
        configuration = 'Release'; runtime = $Runtime; selfContained = $true; singleFile = $true
        builtAtUtc = [DateTime]::UtcNow.ToString('o'); sdk = [string](& dotnet --version)
        resourcesVersion = Get-ContentIdentity $files 'resources/'
        configVersion = Get-ContentIdentity $files 'config/'
        files = $files
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Directory 'build-info.json') -Encoding UTF8
}

# ExpectedCommit 来自固定构建提交，拒绝拿旧二进制为当前提交重新命名。
function Assert-ReleaseInfo([string]$Directory, [string]$Version, [string]$ExpectedCommit, [string]$Runtime = 'win-x64', [switch]$AllowRuntimeLogs) {
    Assert-NoReleaseManagerPrivateFiles -Directory $Directory
    $info = Get-Content -LiteralPath (Join-Path $Directory 'build-info.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($info.schemaVersion -ne 1 -or $info.version -ne $Version -or $info.commit -cne $ExpectedCommit -or
        $info.commit -notmatch '^[a-f0-9]{40}$' -or $info.channel -ne 'Release' -or
        $info.configuration -ne 'Release' -or $info.runtime -ne $Runtime -or
        $info.selfContained -ne $true -or $info.singleFile -ne $true) { throw 'Build identity mismatch' }
    $excluded = if ($AllowRuntimeLogs) { @('logs') } else { @() }
    $files = Get-ReleaseInventory $Directory -Exclude $excluded
    $expected = @($info.files.PSObject.Properties)
    if ($files.Count -ne $expected.Count) { throw 'Release file set mismatch' }
    foreach ($entry in $expected) {
        if (-not $files.Contains($entry.Name) -or $files[$entry.Name] -ne $entry.Value) { throw "Release hash mismatch: $($entry.Name)" }
    }
    foreach ($required in @('GGEA.exe', 'GGEA.Updater.exe', 'OpenCvSharpExtern.dll', 'onnxruntime.dll', 'resources/common/ocr/en_PP-OCRv5_rec_mobile.onnx', 'config/ggea.config.json', 'config/ap_consumption_profiles.json', 'config/update-sources.json', '使用说明.txt', 'RELEASE.txt')) {
        if (-not $files.Contains($required)) { throw "Missing release file: $required" }
    }
    $exe = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $Directory 'GGEA.exe'))
    if ($exe.FileVersion -ne $Version -or $exe.ProductVersion -ne $Version) { throw 'EXE version mismatch' }
    if ($info.resourcesVersion -ne (Get-ContentIdentity $files 'resources/') -or $info.configVersion -ne (Get-ContentIdentity $files 'config/')) { throw 'Resource identity mismatch' }
    Assert-ReleaseResources $Directory
}

# 从解压目录启动真实自包含 EXE，设置超时并检查退出码，避免仅验证“存在”。
function Test-ReleaseExecutable([string]$Directory) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = Join-Path $Directory 'GGEA.exe'
    $startInfo.Arguments = '--verify-release'
    $startInfo.WorkingDirectory = $Directory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardError = $true
    # EXE 的自检诊断固定写入 UTF-8；Windows PowerShell 默认系统编码会把中文拒绝原因读成乱码。
    $startInfo.StandardErrorEncoding = [Text.UTF8Encoding]::new($false, $true)
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { $process.Dispose(); throw 'Release self-test could not start' }
    $errorTask = $process.StandardError.ReadToEndAsync()
    try {
        if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Release self-test timed out' }
        # 显式等待重定向流排空后读取整数退出码；避免 Start-Process 在深层临时路径下返回空 ExitCode。
        $process.WaitForExit()
        $details = $errorTask.GetAwaiter().GetResult()
        $exitCode = [int]$process.ExitCode
        if ($exitCode -ne 0) { throw "Release self-test failed: $exitCode. $details" }
    }
    finally {
        $process.Dispose()
    }
}
