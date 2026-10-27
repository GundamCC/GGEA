# 定向验证本机私有文件不得进入公开包，不构建产品、不读取真实凭证。
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'publish-files.ps1')
$root = Split-Path -Parent $PSScriptRoot
$directory = Join-Path ([IO.Path]::GetTempPath()) ('ggea-package-private-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $directory | Out-Null
    Set-Content (Join-Path $directory 'GGEA.exe') 'fixture'
    Assert-NoReleaseManagerPrivateFiles $directory
    foreach ($relative in @('GGEA.ReleaseManager.exe', 'release-manager.settings.json', '.env', 'backup.pfx', '.local/release/script.ps1', 'config/ReleaseManager/settings.json')) {
        $file = Join-Path $directory $relative
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($file)) -Force | Out-Null
        Set-Content $file 'synthetic test data'
        $blocked = $false
        try { Assert-NoReleaseManagerPrivateFiles $directory } catch { $blocked = $true }
        if (-not $blocked) { throw 'Private file was accepted into a package.' }
        Remove-Item -LiteralPath $file
    }
    foreach ($relative in @('.local/release/script.ps1', 'config/release-manager.settings.json', 'config/ReleaseManager/settings.json', '.env', '.env.production')) {
        & git -C $root check-ignore --quiet -- $relative
        if ($LASTEXITCODE -ne 0) { throw 'Private path is not ignored by Git.' }
    }
    Write-Host 'PASS: package private-file rejection and Git exclusions.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($directory)
    if ([IO.Directory]::GetParent($resolved).FullName.TrimEnd('\') -ne [IO.Path]::GetTempPath().TrimEnd('\') -or
        [IO.Path]::GetFileName($resolved) -notmatch '^ggea-package-private-[a-f0-9]{32}$') { throw 'Unsafe test cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
