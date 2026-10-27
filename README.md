# GGEA

**SD Gundam G Generation ETERNAL Assistant**

《SD高达G世纪 永恒》的非官方 Windows 助手，让每日任务与重复操作更轻松。

[下载](https://github.com/GundamCC/GGEA-Release/releases/latest) · [使用说明](使用说明.txt) · [文档站（本地预览）](http://127.0.0.1:4173/docs/guide/) · [更新记录](CHANGELOG.md) · [问题反馈](https://github.com/GundamCC/GGEA/issues)

## 亮点功能

- **一键日常**：按自定顺序执行每日任务、领取奖励、消耗 AP。
- **关卡辅助**：自动刷本、自动推图、强敌来袭与大师联盟。
- **养成管理**：自动招募与铁球工厂。
- **模拟器连接**：支持 MuMu、雷电、BlueStacks 与自定义 ADB。
- **操作守护**：识别与点击校验、钻石安全锁、随时中止与执行日志。
- **桌面体验**：明暗主题、中日双语界面、Windows 通知与本机插件。

## 开始使用

1. 准备 Windows 10/11 x64 与 Android 模拟器，将游戏画面设为 **1920×1080 横屏**。
2. 从[发行页](https://github.com/GundamCC/GGEA-Release/releases/latest)下载 Windows x64 ZIP，完整解压后运行 `GGEA.exe`；无需安装 .NET。OCR 需要 [VC++ 运行库（x64）](https://aka.ms/vc14/vc_redist.x64.exe)。
3. 按[使用说明](使用说明.txt)连接模拟器，确认账号、游戏语言与资源消耗方案，再开始任务。首次运行请保持观察。

文档站由独立的 `GGEA-website` 仓库维护，尚未部署；上方预览链接仅在本机启动网站后可用。语言与任务适配范围以使用文档为准。

当前源码：**v1.3.3.3** · 正式发布 / Release 渠道。该源码版本尚未发布，下载包版本以发行页为准；详情见[发布记录](docs/open-source/readiness.md)。

## 参与贡献

欢迎反馈问题、完善文档与提交代码。报告问题时请附上模拟器版本、复现步骤和脱敏日志。

[开发指南](docs/development.md) · [开发规范](agent.md) · [工作约定](AGENTS.md) · [插件开发](docs/plugins.md)

## 许可与致谢

自有代码采用 [Apache-2.0](LICENSE)，详见[许可范围](LICENSE_SCOPE.md)。感谢 .NET / WPF、OpenCvSharp / OpenCV、ONNX Runtime、PaddleOCR 与 ADB 等开源项目，组件声明见[第三方软件说明](resources/common/licenses/NOTICE.txt)。

本项目与游戏官方无关联。游戏素材、图标与第三方组件保留各自权利，相关记录见[素材清单](docs/open-source/assets.md)及[待确认事项](docs/open-source/readiness.md)。
