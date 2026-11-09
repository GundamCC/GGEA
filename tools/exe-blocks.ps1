# 每个目标版本只生成一份 EXE 块附件。块独立压缩，HTTP Range 可从任意块边界解压。
function Write-ExeBlocks([string]$Executable, [string]$Destination) {
    if (-not ('GGEA.App.Updates.ExeChunker' -as [type])) {
        Add-Type -Path (Join-Path $PSScriptRoot '../src/GGEA.App/Updates/ExeChunker.cs')
    }
    $temporary = "$Destination.$([guid]::NewGuid().ToString('N')).tmp"
    $chunks = [Collections.Generic.List[object]]::new()
    try {
        $source = [IO.File]::OpenRead($Executable)
        try {
            $output = [IO.File]::Create($temporary)
            try {
                $buffer = [byte[]]::new([GGEA.App.Updates.ExeChunker]::Maximum)
                foreach ($chunk in [GGEA.App.Updates.ExeChunker]::Scan($source)) {
                    $source.Position = $chunk.Offset
                    $read = 0
                    while ($read -lt $chunk.Size) {
                        $n = $source.Read($buffer,$read,$chunk.Size - $read)
                        if (!$n) { throw 'Incomplete source EXE' }
                        $read += $n
                    }
                    $offset = $output.Position
                    $codec = [IO.Compression.DeflateStream]::new($output,[IO.Compression.CompressionLevel]::Optimal,$true)
                    try { $codec.Write($buffer,0,$read) } finally { $codec.Dispose() }
                    $chunks.Add([ordered]@{size=$read; compressed=$output.Position - $offset; hash=$chunk.Hash})
                }
            } finally { $output.Dispose() }
        } finally { $source.Dispose() }
        if (Test-Path -LiteralPath $Destination) {
            if ((Get-FileHash $Destination).Hash -ne (Get-FileHash $temporary).Hash) { throw 'Existing EXE block payload mismatch' }
        } else { [IO.File]::Move($temporary,$Destination) }
        return [ordered]@{algorithm='gear-v1'; chunks=$chunks.ToArray()}
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
