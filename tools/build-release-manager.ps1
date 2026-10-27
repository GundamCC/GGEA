param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/GGEA.ReleaseManager/GGEA.ReleaseManager.csproj'
$buildRoot = Join-Path $root 'build'
$identifier = [Guid]::NewGuid().ToString('N')
$output = Join-Path $buildRoot "release-manager-build-$identifier"
$builtExe = Join-Path $output 'GGEA.ReleaseManager.exe'
$rootExe = Join-Path $root 'GGEA.ReleaseManager.exe'

try {
    # WPF 单文件先输出到本次独立目录，再复制到仓库根目录；避免覆盖仍在运行的旧构建目录。
    & dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $output
    if ($LASTEXITCODE -ne 0) { throw 'Release manager publish failed.' }
    if (-not (Test-Path -LiteralPath $builtExe -PathType Leaf)) { throw 'Release manager EXE is missing.' }

    Copy-Item -LiteralPath $builtExe -Destination $rootExe -Force
    $sourceHash = (Get-FileHash -LiteralPath $builtExe -Algorithm SHA256).Hash
    $targetHash = (Get-FileHash -LiteralPath $rootExe -Algorithm SHA256).Hash
    if ($sourceHash -cne $targetHash) { throw 'Copied release manager EXE differs from the built file.' }
    Write-Host "Release manager: $rootExe"
    Write-Host "SHA-256: $targetHash"
}
finally {
    # 仅删除本次 GUID 命名的中间目录，根目录中的 EXE 和其他构建产物不在清理范围内。
    $base = [IO.Path]::GetFullPath($buildRoot).TrimEnd('\', '/')
    $target = [IO.Path]::GetFullPath($output)
    if ([IO.Directory]::GetParent($target).FullName.TrimEnd('\', '/') -ne $base -or
        [IO.Path]::GetFileName($target) -cne "release-manager-build-$identifier") { throw 'Unsafe release manager cleanup path.' }
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
}
