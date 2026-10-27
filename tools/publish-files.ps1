# 发布文件操作：目录可能被资源管理器或进程作为工作目录持有，只替换目录内的文件。
# 发布包不得包含维护者本机配置或凭证备份。发现即失败，不静默删除来掩盖泄漏来源。
function Assert-NoReleaseManagerPrivateFiles {
    param([Parameter(Mandatory = $true)][string]$Directory)
    $base = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    foreach ($file in @(Get-PublishFiles -Directory $base)) {
        $relative = $file.FullName.Substring($base.Length + 1).Replace('\', '/')
        if ($relative -match '(^|/)(\.local|ReleaseManager)(/|$)' -or
            $file.Name -match '^(\.env($|\.)|(?:GGEA\.)?release-manager\.settings\.json$|GGEA\.ReleaseManager\.(exe|dll|settings\.json)$|credentials\.json$)' -or
            $file.Extension -in @('.pfx', '.p12', '.pem', '.key')) {
            throw 'Package contains release-manager private files or credential material. Inspect staging locally.'
        }
    }
}

function Get-PublishFiles {
    param([Parameter(Mandatory = $true)][string]$Directory, [string[]]$Exclude = @())
    if (-not (Test-Path -LiteralPath $Directory)) { return }
    $entry = Get-Item -LiteralPath $Directory -Force
    if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Publish does not follow directory links: $Directory"
    }
    foreach ($item in Get-ChildItem -LiteralPath $Directory -Force) {
        if ($Exclude -contains $item.Name) { continue }
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Publish does not follow file or directory links: $($item.FullName)"
        }
        if ($item.PSIsContainer) { Get-PublishFiles -Directory $item.FullName }
        else { $item }
    }
}

# 只检查待替换文件；持有目录的句柄不会阻止文件更新，不应要求关闭资源管理器。
function Assert-PublishFilesAvailable {
    param([Parameter(Mandatory = $true)][string]$Directory, [string[]]$Exclude = @())
    foreach ($file in @(Get-PublishFiles -Directory $Directory -Exclude $Exclude)) {
        try {
            $probe = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            $probe.Dispose()
        }
        catch { throw "发布文件被占用或不可写，尚未修改发布包：$($file.FullName)。请关闭 GGEA 或占用该文件的其他进程后重试。" }
    }
}

# 使用字面路径逐文件复制；回滚跳过内容相同的文件，以保留新出现的只读占用。
function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [string[]]$Exclude = @(),
        [switch]$SkipIdentical
    )
    $sourceRoot = [IO.Path]::GetFullPath($Source).TrimEnd([IO.Path]::DirectorySeparatorChar)
    # 在写入前完整检查目标树，禁止通过 junction/symlink 写到发布目录之外。
    $null = @(Get-PublishFiles -Directory $Destination)
    $files = @(Get-PublishFiles -Directory $sourceRoot -Exclude $Exclude)
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($sourceRoot.Length + 1)
        $target = Join-Path $Destination $relative
        if ($SkipIdentical -and (Test-Path -LiteralPath $target -PathType Leaf)) {
            if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -eq
                (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) { continue }
        }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::Copy($file.FullName, $target, $true)
    }
}

# 普通版原位更新保留用户文件及独立插件；此函数不用于全新分发包，不修改现有安装。
function Copy-PublishUserFiles {
    param([Parameter(Mandatory = $true)][string]$Source, [Parameter(Mandatory = $true)][string]$Destination)
    $sourceRoot = [IO.Path]::GetFullPath($Source).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $null = @(Get-PublishFiles -Directory $Destination)
    foreach ($file in @(Get-PublishFiles -Directory $Source -Exclude @('logs'))) {
        $relative = $file.FullName.Substring($sourceRoot.Length + 1).Replace('\', '/')
        if (-not ($relative.StartsWith('config/', [StringComparison]::OrdinalIgnoreCase) -or
            $relative.StartsWith('plugins/', [StringComparison]::OrdinalIgnoreCase) -or
            $relative -in @('resources/stage_work_positions.json', 'resources/common/stage_work_positions.json'))) { continue }
        $target = Join-Path $Destination $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::Copy($file.FullName, $target, $true)
    }
}

# 调用方先验证绝对路径；不删除任何目录，旧版本的文件仍必须清除，logs 保持原样。
# 回滚时 KeepDirectory 指向完整备份，仅移除本次失败发布额外引入的文件。
function Clear-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [string[]]$Exclude = @(),
        [string]$KeepDirectory = ""
    )
    $directoryRoot = [IO.Path]::GetFullPath($Directory).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $files = @(Get-PublishFiles -Directory $directoryRoot -Exclude $Exclude)
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($directoryRoot.Length + 1)
        if ($KeepDirectory -and (Test-Path -LiteralPath (Join-Path $KeepDirectory $relative) -PathType Leaf)) { continue }
        Remove-Item -LiteralPath $file.FullName -Force
    }
}
