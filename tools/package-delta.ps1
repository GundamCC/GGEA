# 从两个原始正式包生成文件差异附件；不联网、不发布，不接受用户运行后的安装目录。
param(
    [Parameter(Mandatory = $true)][string]$BaseDirectory,
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-info.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Assert-DeltaDirectory([string]$Path) {
    for ($current = [IO.Path]::GetFullPath($Path); $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Delta paths cannot traverse links'
        }
    }
}

function Read-DeltaInventory([string]$Directory) {
    Assert-DeltaDirectory $Directory
    Assert-NoReleaseManagerPrivateFiles $Directory
    $path = Join-Path $Directory 'build-info.json'
    if ((Get-Item -LiteralPath $path).Length -gt 8MB) { throw 'Release manifest too large' }
    $info = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($info.schemaVersion -ne 1 -or $info.version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or
        $info.commit -cnotmatch '^[a-f0-9]{40}$' -or $info.channel -ne 'Release' -or
        $info.runtime -ne 'win-x64' -or $info.configuration -ne 'Release' -or
        $info.selfContained -ne $true -or $info.singleFile -ne $true) { throw 'Invalid release identity' }
    $files = Get-ReleaseInventory $Directory
    if ((Get-PublishFiles $Directory | Measure-Object -Property Length -Sum).Sum -gt 3GB) { throw 'Expanded release too large' }
    if ($files.Count -gt 20000 -or $files.Count -ne @($info.files.PSObject.Properties).Count -or
        -not $files.Contains('GGEA.exe') -or -not $files.Contains('GGEA.Updater.exe')) { throw 'Invalid release file set' }
    foreach ($entry in $info.files.PSObject.Properties) {
        if (-not $files.Contains($entry.Name) -or $entry.Value -notmatch '^[a-fA-F0-9]{64}$' -or $files[$entry.Name] -ne $entry.Value -or
            $entry.Name -match '(^|/)(\.|\.\.|CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\.)' -or
            $entry.Name -match '^(\.|logs/|plugins/|local-build-info\.json$|development-info\.json$|delta-info\.json$)' -or
            $entry.Name.Contains('\')) { throw 'Invalid release path or file hash' }
    }
    return @{ Info = $info; Files = $files; Hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}

$baseRoot = [IO.Path]::GetFullPath($BaseDirectory)
$nextRoot = [IO.Path]::GetFullPath($PublishDirectory)
$previous = Read-DeltaInventory $baseRoot
$next = Read-DeltaInventory $nextRoot
if ([version]$previous.Info.version -ge [version]$next.Info.version) { throw 'Delta target must be newer than base' }
$dist = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path (Split-Path $PSScriptRoot) 'artifacts/dist' }
Assert-DeltaDirectory $dist
foreach ($sourceRoot in @($baseRoot, $nextRoot)) {
    if ($dist.TrimEnd('\', '/') -eq $sourceRoot.TrimEnd('\', '/') -or
        $dist.StartsWith($sourceRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Delta output must be outside source packages'
    }
}
$name = "GGEA-v$($next.Info.version)-from-v$($previous.Info.version)-win-x64.delta.zip"
$zipPath = Join-Path $dist $name
if ((Test-Path -LiteralPath $zipPath) -or (Test-Path -LiteralPath "$zipPath.sha256")) { throw 'Delta output already exists; use a new output directory' }
[IO.Directory]::CreateDirectory($dist) | Out-Null
$temporary = Join-Path $dist ('.delta-' + [Guid]::NewGuid().ToString('N') + '.tmp')
$checksumTemporary = "$temporary.sha256"
$prefix = "GGEA-v$($next.Info.version)-win-x64/"
# 默认配置始终随包携带，重建时不读取用户配置；安装事务仍保留已有用户数据。
$changed = @($next.Files.Keys | Where-Object {
    -not $previous.Files.Contains($_) -or $previous.Files[$_] -ne $next.Files[$_] -or
    $_.StartsWith('config/', [StringComparison]::OrdinalIgnoreCase) -or
    $_ -in @('resources/stage_work_positions.json', 'resources/common/stage_work_positions.json')
})
try {
    $zip = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($relative in @($changed) + @('build-info.json')) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $nextRoot $relative), "$prefix$relative", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
        $entry = $zip.CreateEntry("${prefix}delta-info.json")
        $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
        try {
            $writer.Write(([ordered]@{ schemaVersion = 1; baseVersion = $previous.Info.version; baseManifestSha256 = $previous.Hash } | ConvertTo-Json))
        } finally { $writer.Dispose() }
    } finally { $zip.Dispose() }
    if ((Get-Item -LiteralPath $temporary).Length -gt 1GB) { throw 'Delta ZIP too large' }
    # 重新读取 ZIP 内每个文件的字节摘要，保证压缩产物和原始目标清单一致。
    $zip = [IO.Compression.ZipFile]::OpenRead($temporary)
    try {
        if ($zip.Entries.Count -ne $changed.Count + 2) { throw 'Delta ZIP file set mismatch' }
        foreach ($relative in @($changed) + @('build-info.json')) {
            $stream = $zip.GetEntry("$prefix$relative").Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
            finally { $sha.Dispose(); $stream.Dispose() }
            $expected = if ($relative -eq 'build-info.json') { $next.Hash } else { $next.Files[$relative] }
            if ($hash -cne $expected) { throw 'Delta ZIP hash mismatch' }
        }
    } finally { $zip.Dispose() }
    # 再次核验来源，拒绝打包期间发生变化的输入。
    $baseAfter = Read-DeltaInventory $baseRoot
    $nextAfter = Read-DeltaInventory $nextRoot
    if ($baseAfter.Hash -cne $previous.Hash -or $nextAfter.Hash -cne $next.Hash) { throw 'Release changed during delta packaging' }
    $hash = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name" | Set-Content -LiteralPath $checksumTemporary -Encoding ASCII
    [IO.File]::Move($temporary, $zipPath)
    try { [IO.File]::Move($checksumTemporary, "$zipPath.sha256") }
    catch { [IO.File]::Delete($zipPath); throw }
    Write-Host "Verified delta: $zipPath ($($changed.Count) payload files)"
    Write-Host 'Upload alongside the full ZIP and both checksum files; never publish a delta alone.'
} finally {
    # 只删除本次创建的随机临时文件，不递归删除输入或输出目录。
    if (Test-Path -LiteralPath $temporary) { [IO.File]::Delete($temporary) }
    if (Test-Path -LiteralPath $checksumTemporary) { [IO.File]::Delete($checksumTemporary) }
}
