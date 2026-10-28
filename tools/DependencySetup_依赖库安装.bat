@echo off
chcp 65001 >nul
setlocal
rem 独立入口：即使 GGEA 无法启动，也可由用户手动安装或修复。
echo GGEA - Microsoft Visual C++ v14 x64
echo [1] 安装或更新 / Install or update
echo [2] 修复已安装的运行库 / Repair existing runtime
echo 需要联网；安装器可能请求管理员权限。 / Internet and administrator consent may be required.
choice /c 12 /n /m "请选择 / Select [1/2]: "
rem CHOICE 被 Ctrl+C / Ctrl+Break 中断时返回 0，不继续发起安装。
if not errorlevel 1 exit /b 2
if errorlevel 3 goto failed
if errorlevel 2 goto repair
set "GGEA_RUNTIME_ACTION=/install"
goto install
:repair
set "GGEA_RUNTIME_ACTION=/repair"
:install
where winget >nul 2>nul
if errorlevel 1 goto failed
call winget install --id Microsoft.VCRedist.2015+.x64 --exact --source winget --force --accept-package-agreements --accept-source-agreements --override "%GGEA_RUNTIME_ACTION% /passive /norestart"
set "GGEA_RUNTIME_EXIT=%errorlevel%"
if "%GGEA_RUNTIME_EXIT%"=="3010" goto restart
if "%GGEA_RUNTIME_EXIT%"=="1641" goto restart
if not "%GGEA_RUNTIME_EXIT%"=="0" goto failed
echo 操作完成。请完全退出并重新启动 GGEA。 / Completed. Close and restart GGEA.
goto success
:restart
echo 安装器要求重启 Windows，请保存工作后重启。 / Restart Windows before using GGEA.
:success
pause
exit /b 0
:failed
echo 安装未完成或 WinGet 不可用。 / Installation failed or WinGet is unavailable.
echo 请从微软官网下载 x64 安装器并安装或修复，然后重新启动 GGEA。
echo Download the x64 installer from Microsoft, install or repair, then restart GGEA.
echo https://aka.ms/vc14/vc_redist.x64.exe
echo 若修复提示未安装，请重新选择 [1]。 / If repair reports no installed runtime, select [1].
pause
exit /b 1
