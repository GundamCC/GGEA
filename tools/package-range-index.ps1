param([Parameter(Mandatory)][string]$Archive, [Parameter(Mandatory)][string]$VerifiedDirectory,
      [Parameter(Mandatory)][string]$ArchiveHash, [Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
# 正式 ZIP 使用普通本地头。记录压缩数据的精确位置，所有旧版本共用同一份目标索引。
$manifest = [IO.File]::ReadAllBytes((Join-Path $VerifiedDirectory 'build-info.json'))
$info = [Text.Encoding]::UTF8.GetString($manifest).TrimStart([char]0xFEFF) | ConvertFrom-Json
$entries = [ordered]@{}
$reader = [IO.BinaryReader]::new([IO.File]::OpenRead($Archive))
try {
    while (($signature = $reader.ReadUInt32()) -eq 0x04034b50) {
        $null = $reader.ReadUInt16()
        $flags = $reader.ReadUInt16()
        $method = $reader.ReadUInt16()
        if (($flags -band 0xf7ff) -ne 0 -or $method -notin @(0,8)) { throw 'Unsupported ZIP entry encoding' }
        $null = $reader.ReadBytes(8)
        $compressed = $reader.ReadUInt32()
        $size = $reader.ReadUInt32()
        $nameLength = $reader.ReadUInt16()
        $extraLength = $reader.ReadUInt16()
        $name = [Text.Encoding]::UTF8.GetString($reader.ReadBytes($nameLength)).Replace('\','/')
        $null = $reader.ReadBytes($extraLength)
        $offset = $reader.BaseStream.Position
        $relative = $name.Substring($name.IndexOf('/') + 1)
        if ($relative -and !$relative.EndsWith('/') -and $relative -cne 'build-info.json') {
            if ($null -eq $info.files.PSObject.Properties[$relative]) { throw "Unexpected ZIP member: $relative" }
            $entries.Add($relative, [ordered]@{offset=$offset; compressed=$compressed; size=$size; method=$method})
        }
        $reader.BaseStream.Position = $offset + $compressed
    }
    if ($signature -ne 0x02014b50 -or $entries.Count -ne @($info.files.PSObject.Properties).Count) { throw 'ZIP index inventory mismatch' }
} finally { $reader.Dispose() }
$index = [ordered]@{schema=1; archiveHash=$ArchiveHash; manifest=[Convert]::ToBase64String($manifest); entries=$entries}
. (Join-Path $PSScriptRoot 'exe-blocks.ps1')
$index.exeBlocks = Write-ExeBlocks (Join-Path $VerifiedDirectory 'GGEA.exe') "$Archive.blocks"
$json = $index | ConvertTo-Json -Depth 5 -Compress
$temp = "$OutputPath.$([guid]::NewGuid().ToString('N')).tmp"
try {
    [IO.File]::WriteAllText($temp, $json, [Text.UTF8Encoding]::new($false))
    if ((Get-Item -LiteralPath $temp).Length -gt 16MB) { throw 'Range index too large' }
    if (Test-Path -LiteralPath $OutputPath) {
        if ((Get-FileHash $OutputPath).Hash -ne (Get-FileHash $temp).Hash) { throw 'Existing range index mismatch' }
    } else { [IO.File]::Move($temp, $OutputPath) }
} finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp } }
