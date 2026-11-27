param([Parameter(Mandatory)][string]$Archive, [Parameter(Mandatory)][string]$VerifiedDirectory, [string]$BaseDirectory = '')
$ErrorActionPreference = 'Stop'
$arguments = @('run', '--project', (Join-Path $PSScriptRoot 'SmallUpdate/SmallUpdate.csproj'), '-c', 'Release', '--',
    [IO.Path]::GetFullPath($Archive), [IO.Path]::GetFullPath($VerifiedDirectory))
if ($BaseDirectory) { $arguments += [IO.Path]::GetFullPath($BaseDirectory) }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Small update generation or reconstruction failed' }
