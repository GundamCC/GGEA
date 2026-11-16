# 使用独立临时目录编译；.NET 用户构建直接使用随源码分发的 DEX，无须安装 Android SDK。
param(
    [Parameter(Mandatory)][string]$Jdk,
    [Parameter(Mandatory)][string]$AndroidJar,
    [Parameter(Mandatory)][string]$R8Jar
)
$ErrorActionPreference = 'Stop'
$work = New-Item -Type Directory (Join-Path ([IO.Path]::GetTempPath()) ("ggea-dex-" + [guid]::NewGuid().ToString('N')))
$source = Join-Path $PSScriptRoot 'PreciseDrag.java'
& "$Jdk/bin/javac" --release 8 -encoding UTF-8 -g:none -classpath $AndroidJar -d $work.FullName $source
if ($LASTEXITCODE) { throw 'javac failed' }
& "$Jdk/bin/java" -cp $R8Jar com.android.tools.r8.D8 --min-api 21 --lib $AndroidJar --output $work.FullName "$work/org/ggea/input/PreciseDrag.class"
if ($LASTEXITCODE) { throw 'D8 failed' }
# app_process 可加载原始 DEX。只复制已成功完成的编译结果。
Copy-Item "$work/classes.dex" "$PSScriptRoot/../../resources/common/device/precise-drag.dex" -Force
# 重建使用 JDK 17、Android API 16 编译桩和 D8 8.3.37；将此摘要同步到 PreciseDragChannel.Digest。
Get-FileHash "$work/classes.dex" -Algorithm SHA256
