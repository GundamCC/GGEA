# 仅更新 README 使用的公开头像；使用 Windows 自带图像解码器，不引入额外依赖。
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../docs/contributors')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$contributors = @('periaperia', 'Torfaen')
$pending = [ordered]@{}

# 全部下载和解码成功后才写入，避免某一成员下载失败时留下半套更新。
foreach ($name in $contributors) {
    $response = Invoke-WebRequest -Uri "https://github.com/$name.png?size=160" `
        -TimeoutSec 30 -MaximumRetryCount 2 -RetryIntervalSec 3
    if ($response.Content.Length -gt 10MB) { throw "Avatar too large: $name" }
    $inputStream = [IO.MemoryStream]::new([byte[]]$response.Content)
    $outputStream = [IO.MemoryStream]::new()
    $image = $null
    try {
        # GitHub 的 .png 地址可能实际返回 GIF；统一为 PNG，保证 SVG 解码兼容性。
        $image = [Drawing.Image]::FromStream($inputStream, $true, $true)
        if ($image.Width -gt 2048 -or $image.Height -gt 2048) { throw "Avatar dimensions too large: $name" }
        $image.Save($outputStream, [Drawing.Imaging.ImageFormat]::Png)
        $data = [Convert]::ToBase64String($outputStream.ToArray())
        $pending[$name] = '<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="160" height="160" viewBox="0 0 160 160"><title>' + $name + '</title><defs><clipPath id="circle"><circle cx="80" cy="80" r="80"/></clipPath></defs><image width="160" height="160" preserveAspectRatio="xMidYMid slice" clip-path="url(#circle)" xlink:href="data:image/png;base64,' + $data + '"/></svg>'
    }
    finally {
        if ($null -ne $image) { $image.Dispose() }
        $outputStream.Dispose()
        $inputStream.Dispose()
    }
}

$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
foreach ($name in $contributors) {
    $path = Join-Path $OutputDirectory "$name.svg"
    # 不写时间戳；相同头像重复执行不会产生空提交。
    if ((Test-Path -LiteralPath $path) -and [IO.File]::ReadAllText($path) -ceq $pending[$name]) {
        Write-Output "Unchanged: $name"
        continue
    }
    [IO.File]::WriteAllText($path, $pending[$name], [Text.UTF8Encoding]::new($false))
    Write-Output "Updated: $name"
}
