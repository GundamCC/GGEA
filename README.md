# GGEA — SD Gundam G Generation ETERNAL Assistant

《SD高达G世纪 永恒》的非官方 Windows 桌面助手，提供模拟器连接、每日任务、奖励领取与关卡辅助操作。

当前源码版本为 **v1.3.3.3**，构建渠道为「正式发布 / Release」。此版本修复尚未发布，下载入口仍提供上一正式版；已发布包的版本与源码提交见 [发布记录](docs/open-source/readiness.md)。

本版修复：初代高达 Logo 的平滑渲染识别，以及多个流程保存诊断证据时的覆盖问题；保持原识别阈值与身份校验。完整说明见 [更新记录](CHANGELOG.md)。

## 源码与下载

- 源码：[GundamCC/GGEA](https://github.com/GundamCC/GGEA)，默认开发分支为 `main`。
- 正式版：[发行与下载页](https://github.com/GundamCC/GGEA-Release/releases/latest)，在附件中下载 Windows x64 ZIP 与对应的 SHA-256 校验文件。
- 使用步骤：[使用说明](使用说明.txt)；问题反馈：[GitHub Issues](https://github.com/GundamCC/GGEA/issues)。

下载 ZIP 并完整解压后运行 `GGEA.exe`，不要将发行页自动生成的 Source code 压缩包当作程序。该版本构建自源码提交 [`9fbe302`](https://github.com/GundamCC/GGEA/commit/9fbe30250579a2e87393de4aeb5aaead0ba09100)。旧的 30 个发行版已于 2026-10-28 下架。

## 使用文档

[使用文档（本地预览）](http://127.0.0.1:4173/docs/guide/) · [离线使用说明](使用说明.txt) · [更新记录](CHANGELOG.md)

详细文档由独立的 `GGEA-website` 仓库维护，尚未部署。上述预览地址仅在本机启动网站预览服务后可访问。发布包保留离线 `使用说明.txt`。本仓库为桌面程序唯一开发仓库，默认分支为 `main`。

## 主要能力

- MuMu、雷电、BlueStacks 与自定义模拟器的 ADB 连接、设备选择与截图。
- 每日任务排序执行、奖励领取、AP 消耗与关卡导航。
- 自动招募、自动刷本、自动推图、大师联盟与铁球工厂。
- 识别与点击校验、钻石安全锁、取消任务及执行日志。
- 明暗主题、简体中文／日语界面、Windows 通知与本机插件。

自动化要求 **1920×1080 横屏截图**。设备在线不代表账号已经确认；使用前检查目标账号、游戏页面和资源消耗方案，首次执行保持观察。游戏语言和模拟器的适配范围以文档中的验证边界为准。

## 环境要求

- 终端用户：Windows 10/11 x64、Android 模拟器与可用 ADB。完整包自包含，不需要 .NET SDK、Python、NDK、JDK 或 Frida。
- 原生 OCR 依赖：安装或修复 [微软最新版 Microsoft Visual C++ v14 Redistributable（x64）](https://aka.ms/vc14/vc_redist.x64.exe)。.NET 自包含不包含 VC++ 运行库，仅安装 x86 版本不能满足本包要求；安装后完全退出并重新启动 GGEA。
- 开发与测试：Windows、Git、PowerShell 7（`pwsh`）、.NET 8 SDK（或兼容 `net8.0-windows` 的更高版本）。

## 开发与贡献

```powershell
dotnet build GGEA.sln
pwsh -File tools/test.ps1 -NoBuild
```

默认运行核心冒烟检查，不代表所有模拟器或游戏流程已实测。开发前阅读 [开发规范](agent.md) 和 [工作约定](AGENTS.md)。

- [开发、构建与发布](docs/development.md)：开发包、正式发布流程及模拟器扩展。
- [插件开发与使用](docs/plugins.md)：插件协议、安装与独立构建。
- [本机目录约定](docs/local-directories.md)：依赖、缓存及历史资料管理。

## 使用的项目

- [.NET 8](https://dotnet.microsoft.com/en-us/) 与 [WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)：Windows 桌面程序与界面。正式包采用自包含构建，终端用户无需另行安装 .NET Runtime。
- [OpenCvSharp 4.13.0.20260627](https://github.com/shimat/opencvsharp) 与 [OpenCV 4.13.0](https://opencv.org/)：图像解码和模板匹配；`OpenCvSharpExtern.dll` 随发布包提供。
- [ONNX Runtime 1.23.2](https://github.com/microsoft/onnxruntime) 与 [PaddleOCR PP-OCRv5](https://github.com/PaddlePaddle/PaddleOCR)：在本机使用 CPU 识别关卡编号与 HARD 标题；模型及许可说明见 `resources/common/ocr/`。
- [Android Debug Bridge (ADB)](https://developer.android.com/tools/adb)：连接模拟器、截图和发送操作；发布包不含 ADB，请使用模拟器自带版本或 Android SDK Platform-Tools。

## 开源许可

自有代码采用 [Apache-2.0](LICENSE)，适用范围见 [许可证说明](LICENSE_SCOPE.md)。第三方组件、OCR 模型、游戏截图、识别模板和图标按各自权利与许可处理，不因本项目开源而自动获得再分发授权。本项目为非官方社区工具。

开源准备状态见 [核查与待办](docs/open-source/readiness.md)，组件声明见 [第三方软件说明](resources/common/licenses/NOTICE.txt)，游戏素材见 [来源与隐私清单](docs/open-source/assets.md)。源码与当前正式版已公开；素材授权及第三方原生构建适用性等待确认事项仍保留在清单中，不因发布完成而视为解决。
