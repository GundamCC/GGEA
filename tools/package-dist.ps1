param(
    [ValidateSet('win-x64')][string]$Runtime = 'win-x64',
    [string]$PublishDirectory = '',
    [string]$SourceCommit = '',
    [string]$OutputDirectory = '',
    [switch]$BuildIfNeeded,
    [string]$BaseDirectory = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'release-info.ps1')
$version = Get-ReleaseVersion $root
if (-not $SourceCommit) {
    $SourceCommit = [string](& git -C $root rev-parse HEAD)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve source commit' }
    $dirty = & git -C $root status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Commit source changes before packaging.' }
}
$dist = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'artifacts/dist' }
$name = "GGEA-v$version-$Runtime-$($SourceCommit.Substring(0, 12))"
$zip = Join-Path $dist "$name.zip"
$acceptance = Join-Path $dist "$name-verified"
# 完整发布已存在时优先复核其解压验收目录，避免默认 publish 中运行的旧程序阻碍重复校验。
$publishDir = if ($PublishDirectory) { [IO.Path]::GetFullPath($PublishDirectory) }
    elseif ((Test-Path -LiteralPath $zip) -or (Test-Path -LiteralPath "$zip.sha256")) { $acceptance }
    else { Join-Path $root 'artifacts/publish' }
if (-not $PublishDirectory -and $publishDir -eq $acceptance -and -not (Test-Path -LiteralPath $acceptance -PathType Container)) {
    throw "Missing acceptance directory: $acceptance"
}
# 界面普通打包入口仅在没有当前提交的包时检查构建身份；旧 publish 可能正被程序占用。
# 固定提交完整构建在独立源码快照中完成，避免尝试覆盖正在运行的旧程序。
if ($BuildIfNeeded -and -not $PublishDirectory -and -not (Test-Path -LiteralPath $zip) -and
    -not (Test-Path -LiteralPath "$zip.sha256")) {
    $infoPath = Join-Path $publishDir 'build-info.json'
    $info = if (Test-Path -LiteralPath $infoPath -PathType Leaf) {
        Get-Content -LiteralPath $infoPath -Raw -Encoding UTF8 | ConvertFrom-Json
    } else { $null }
    if ($null -eq $info -or $info.version -ne $version -or $info.commit -cne $SourceCommit -or
        $info.runtime -ne $Runtime -or $info.configuration -ne 'Release' -or
        $info.selfContained -ne $true -or $info.singleFile -ne $true) {
        Write-Host "No matching publish for v$version / $($SourceCommit.Substring(0, 12)); building from the fixed source commit."
        & (Join-Path $PSScriptRoot 'release.ps1') -Commit $SourceCommit -BaseDirectory $BaseDirectory
        return
    }
}
Assert-ReleaseInfo $publishDir $version $SourceCommit $Runtime -AllowRuntimeLogs
# 只有压缩、解压与真实程序自检全部通过，才将 ZIP 移到最终输出目录。
$workRoot = Join-Path $root 'build'
$work = Join-Path $workRoot ('.verify-' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $work $name
$unpacked = Join-Path $work 'unpacked'
try {
    # 同一提交的发布包可重复调用；必须重新解压校验，不能仅因文件存在就返回成功。
    if ((Test-Path -LiteralPath $zip) -or (Test-Path -LiteralPath "$zip.sha256")) {
        if (-not (Test-Path -LiteralPath $zip -PathType Leaf) -or -not (Test-Path -LiteralPath "$zip.sha256" -PathType Leaf)) {
            throw "Incomplete release already exists: $zip"
        }
        $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        $checksum = (Get-Content -LiteralPath "$zip.sha256" -Raw -Encoding ASCII).Trim()
        if ($checksum -cne "$hash  $name.zip") { throw "Existing release checksum mismatch: $zip" }
        Expand-Archive -LiteralPath $zip -DestinationPath $unpacked
        $verified = Join-Path $unpacked $name
        Assert-ReleaseInfo $verified $version $SourceCommit $Runtime
        Test-ReleaseExecutable $verified
        Assert-ReleaseInfo $verified $version $SourceCommit $Runtime
        if (-not (Test-Path -LiteralPath $acceptance -PathType Container)) { throw "Missing acceptance directory: $acceptance" }
        Assert-ReleaseInfo $acceptance $version $SourceCommit $Runtime
        if ((Get-FileHash -LiteralPath (Join-Path $verified 'build-info.json') -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath (Join-Path $acceptance 'build-info.json') -Algorithm SHA256).Hash) {
            throw "Existing release acceptance mismatch: $acceptance"
        }
        Write-Host "Verified existing ZIP: $zip"
        Write-Host "SHA-256: $hash"
        Write-Host "Acceptance directory (extracted from ZIP): $acceptance"
        if ($BaseDirectory) { & (Join-Path $PSScriptRoot 'package-delta.ps1') -BaseDirectory $BaseDirectory -PublishDirectory $acceptance -OutputDirectory $dist }
        return
    }
    Copy-DirectoryContents $publishDir $package -Exclude @('logs')
    Assert-ReleaseInfo $package $version $SourceCommit $Runtime
    $candidate = Join-Path $work "$name.zip"
    Compress-Archive -LiteralPath $package -DestinationPath $candidate -CompressionLevel Optimal
    Expand-Archive -LiteralPath $candidate -DestinationPath $unpacked
    $verified = Join-Path $unpacked $name
    Assert-ReleaseInfo $verified $version $SourceCommit $Runtime
    Test-ReleaseExecutable $verified
    # 自检也不得修改配置或污染包内容。
    Assert-ReleaseInfo $verified $version $SourceCommit $Runtime
    $hash = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name.zip" | Set-Content -LiteralPath (Join-Path $work 'checksum.txt') -Encoding ASCII
    # 保留从 ZIP 解压的目录，用于人工实机验收。
    if (Test-Path -LiteralPath $acceptance) { throw "Acceptance directory exists: $acceptance" }
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    Move-Item -LiteralPath $verified -Destination $acceptance
    Move-Item -LiteralPath (Join-Path $work 'checksum.txt') -Destination "$zip.sha256"
    try { Move-Item -LiteralPath $candidate -Destination $zip }
    catch { Remove-Item -LiteralPath "$zip.sha256" -Force; throw }
    Write-Host "Verified ZIP: $zip"
    Write-Host "SHA-256: $hash"
    Write-Host "Acceptance directory (extracted from ZIP): $acceptance"
    if ($BaseDirectory) { & (Join-Path $PSScriptRoot 'package-delta.ps1') -BaseDirectory $BaseDirectory -PublishDirectory $acceptance -OutputDirectory $dist }
}
finally {
    $resolved = [IO.Path]::GetFullPath($work)
    if ([IO.Directory]::GetParent($resolved).FullName -ne [IO.Path]::GetFullPath($workRoot).TrimEnd('\', '/') -or [IO.Path]::GetFileName($resolved) -notmatch '^\.verify-[a-f0-9]{32}$') { throw 'Unsafe package cleanup path' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
