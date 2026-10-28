<div align="center">
  <h1>GGEA</h1>
  <p>🌟 GGEA SD高达G世纪永恒小助手 🌟</p>
  <p><a href="README.md">简体中文</a> | <a href="README.ja.md">日本語</a></p>
  <p>快速开始：<a href="https://github.com/GundamCC/GGEA/releases/latest">下载最新版</a>并访问<a>使用教程</a>。</p>
  <p><a href="CHANGELOG.md">更新记录</a> · <a>FAQ</a></p>
</div>

## 功能简介

- **每日任务**：包含回收战舰、商店购买每日免费礼包、开发&强化一次、略过每日强化培育关卡、消耗AP及领取奖励，以上任务可通过拖拽排序改变执行顺序。
- **领取奖励**：可领取邮件奖励及每日使命奖励，通过配置项可在其他每日任务完成后领取最终的每日使命奖励。
- **AP 消耗**：可自定义顺序及HARD关卡的主线作品略过，支持保存自定义配置。
- **自动刷本**：自动周回对应的关卡，支持调节刷取次数与特殊情况出击。
- **自动招募**：自动招募所有可招募的角色。
- **自动推图**：支持主线关卡推进至HARD结束、历史关卡含BOSS挑战、强敌来袭三个难度的自动推图。
- **强敌来袭**：略过每日强敌来袭，支持将该任务添加到每日任务队列。
- **大师联盟**：自动挑战大师联盟。
- **铁球工厂**：自动制造铁球并解体，直到获得用户指定的GP数

## 快速开始
### 下载方式
#### 方式一：GitHub Releases（推荐）
前往 [Releases](https://github.com/GundamCC/GGEA/releases) 下载正式版本。主仓库尚无正式包时，可使用[旧客户端兼容下载](https://github.com/GundamCC/GGEA-Release/releases)。源码更新不代表安装包已经发布。
#### 方式二：QQ群组文件下载
GGEA官方群：[1002150086](https://qm.qq.com/q/zkxGhEiVQQ)

### 使用方式
启动已安装游戏的 Android 模拟器（分辨率 1920 x 1080），并开启 ADB 调试。
<br/>
解压并运行程序文件夹里的 `GGEA.exe`，在右上角「设置」中选择模拟器对应的 `adb.exe`；BlueStacks 用户选择 `HD-Adb.exe`。
<br/>
在设置中选择使用的服务器及语言，回到主界面点击「刷新」并选择具体使用的模拟器。
<br/>
选择对应功能并开始任务。

## 注意事项
- 推荐使用 mumu 模拟器或雷电模拟器，获得最佳的使用体验。
- 目前支持的服务器：国际服、日服。
- 目前支持的游戏语言：简体中文、日语。
- 已验证的模拟器：mumu模拟器6、mumu模拟器12、雷电14、BlueStacks 5。
- 支持 Windows 10/11 x64。安装包已包含 .NET 运行时，使用者不需要安装。
- GGEA 支持多模拟器切换。
- 目前仅支持 1920 x 1080 分辨率，游戏语言支持简体中文与日语。
- `config\ap_consumption_profiles.json` 文件保存了当前的AP消耗方案配置。

## 校验下载

Release 附件提供同名 `.zip.sha256` 文件，可用 PowerShell 的 `Get-FileHash -Algorithm SHA256` 校验 ZIP；结果应与校验文件第一列一致。SHA-256 用于检查下载完整性，不是数字签名。

## 使用的项目

- [.NET 8](https://dotnet.microsoft.com/en-us/) 与 [WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)：Windows 桌面程序与界面。正式包采用自包含构建，终端用户无需另行安装 .NET Runtime。
- [OpenCvSharp 4.13.0.20260627](https://github.com/shimat/opencvsharp) 与 [OpenCV 4.13.0](https://opencv.org/)：图像解码和模板匹配；`OpenCvSharpExtern.dll` 随发布包提供。
- [ONNX Runtime 1.23.2](https://github.com/microsoft/onnxruntime) 与 [PaddleOCR PP-OCRv5](https://github.com/PaddlePaddle/PaddleOCR)：在本机使用 CPU 识别关卡编号与 HARD 标题；模型及许可说明见 `resources/common/ocr/`。
- [Android Debug Bridge (ADB)](https://developer.android.com/tools/adb)：连接模拟器、截图和发送操作；发布包不含 ADB，请使用模拟器自带版本或 Android SDK Platform-Tools。

## 问题反馈

请在 [Issues](https://github.com/GundamCC/GGEA/issues) 中写明 GGEA 版本、Windows 与模拟器环境、复现步骤，以及出问题前后的操作。日志位于解压目录的 `logs/`。
<br/>
或通过GGEA官方群：[1002150086](https://qm.qq.com/q/zkxGhEiVQQ)  进行BUG反馈、功能建议。


## 开发与贡献

源码版本：**v1.3.4.0** · 正式发布 / Release。

欢迎提交代码、完善文档或反馈问题。本仓库为 GGEA 桌面程序源码，开发分支为 `main`。

[开发指南](docs/development.md) · [开发规范](agent.md) · [工作约定](AGENTS.md) · [插件开发](docs/plugins.md)

## 贡献者与致谢

感谢以下成员为 GGEA 作出的贡献！

<p>
  <a href="https://github.com/periaperia"><img src="docs/contributors/periaperia.svg" width="80" height="80" alt="periaperia" title="periaperia" /></a>
  <a href="https://github.com/Torfaen"><img src="docs/contributors/Torfaen.svg" width="80" height="80" alt="Torfaen" title="Torfaen" /></a>
</p>

## 开源许可

自有代码采用 [Apache-2.0](LICENSE)，详见[许可范围](LICENSE_SCOPE.md)。第三方组件与游戏素材保留各自权利，见[第三方声明](resources/common/licenses/NOTICE.txt)和[素材清单](docs/open-source/assets.md)。本项目为非官方社区工具，与游戏官方无关联。
