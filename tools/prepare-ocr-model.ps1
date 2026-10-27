# 维护者下载固定 OCR 模型；运行程序不调用此脚本，也不会在线更新模型。
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $taskRoot 'resources/common/ocr/model.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$target = Join-Path $taskRoot 'resources/common/ocr/en_PP-OCRv5_rec_mobile.onnx'
if (Test-Path -LiteralPath $target) {
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ieq $manifest.sha256) {
        Write-Host 'OCR model already verified.'
        return
    }
}
$temporary = "$target.download-$([Guid]::NewGuid().ToString('N'))"
try {
    Invoke-WebRequest -Uri $manifest.source -OutFile $temporary -TimeoutSec 60
    if ((Get-Item -LiteralPath $temporary).Length -ne $manifest.bytes -or
        (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ine $manifest.sha256) {
        throw 'OCR model size or SHA-256 mismatch; existing model was preserved.'
    }
    Move-Item -LiteralPath $temporary -Destination $target -Force
    Write-Host 'OCR model downloaded and verified.'
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
}
