<div align="center">
  <h1>GGEA</h1>
  <p>🌟 GGEA SD高达G世纪永恒小助手 🌟</p>
  <p><a href="README.md">简体中文</a> | <a href="README.ja.md">日本語</a></p>
  <p>快速开始：<a href="https://github.com/GundamCC/GGEA-SD-Gundam-G-Generation-Eternal-Assistant/releases/latest">下载最新版</a>并访问<a href="https://www.gundamc.com/ggea/docs/guide/install/">使用文档</a>。</p>
  <p><a href="https://www.gundamc.com/ggea/">官网</a> · <a href="CHANGELOG.md">更新记录</a> · <a href="https://www.gundamc.com/ggea/docs/guide/faq/download/">FAQ</a></p>
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
前往 [Releases](https://github.com/GundamCC/GGEA-SD-Gundam-G-Generation-Eternal-Assistant/releases) 下载正式版本。主仓库尚无正式包时，可使用[旧客户端兼容下载](https://github.com/GundamCC/GGEA-Release/releases)。源码更新不代表安装包已经发布。
#### 方式二：QQ群组文件下载
GGEA官方群：[1002150086](https://qm.qq.com/q/zkxGhEiVQQ)

### 使用方式
启动已安装游戏的 Android 模拟器，开启 ADB 调试。模拟器可选用四种横屏配置：1280×720（240 DPI）、1600×900（240 DPI）、1920×1080（280 DPI）、2560×1440（320 DPI）。任务、OCR 和调试操作统一使用 1920×1080 坐标；程序按实际截图尺寸转换画面和输入位置。
<br/>
解压并运行程序文件夹里的 `GGEA.exe`，在右上角「设置」中选择模拟器对应的 `adb.exe`；BlueStacks 用户选择 `HD-Adb.exe`。
<br/>
在设置中选择使用的服务器及语言，回到主界面点击「刷新」并选择具体使用的模拟器。
<br/>
选择对应功能并开始任务。

VC++ v14 x64 运行库仍需安装。当前源码新增启动检测及随包 `DependencySetup_依赖库安装.bat` 安装/修复入口；旧包没有脚本时使用[微软官方下载](https://aka.ms/vc14/vc_redist.x64.exe)。详见[安装说明](https://www.gundamc.com/ggea/docs/guide/install/)，以实际发布包为准。

## 注意事项
- 推荐使用 mumu 模拟器或雷电模拟器，获得最佳的使用体验。
- 目前支持的服务器：国际服、日服。
- 目前支持的游戏语言：简体中文、日语。
- 已验证的模拟器：mumu模拟器6、mumu模拟器12、雷电14、BlueStacks 5。
- 支持 Windows 10/11 x64。安装包已包含 .NET 运行时，使用者不需要安装。
- GGEA 支持多模拟器切换。
- 支持四档横屏分辨率，游戏语言支持简体中文与日语。
- `config\ap_consumption_profiles.json` 文件保存了当前的AP消耗方案配置。

## 校验下载

Release 附件提供同名 `.zip.sha256` 文件，可用 PowerShell 的 `Get-FileHash -Algorithm SHA256` 校验 ZIP；结果应与校验文件第一列一致。SHA-256 用于检查下载完整性，不是数字签名。

## 使用的项目

- [.NET 8](https://dotnet.microsoft.com/en-us/) 与 [WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)：Windows 桌面程序与界面。正式包采用自包含构建，终端用户无需另行安装 .NET Runtime。
- [OpenCvSharp 4.13.0.20260627](https://github.com/shimat/opencvsharp) 与 [OpenCV 4.13.0](https://opencv.org/)：图像解码和模板匹配；`OpenCvSharpExtern.dll` 随发布包提供。
- [ONNX Runtime 1.23.2](https://github.com/microsoft/onnxruntime) 与 [PaddleOCR PP-OCRv5](https://github.com/PaddlePaddle/PaddleOCR)：在本机使用 CPU 识别关卡编号与 HARD 标题；模型及许可说明见 `resources/common/ocr/`。
- [Android Debug Bridge (ADB)](https://developer.android.com/tools/adb)：连接模拟器、截图和发送操作；发布包不含 ADB，请使用模拟器自带版本或 Android SDK Platform-Tools。

## 问题反馈

请在 [Issues](https://github.com/GundamCC/GGEA-SD-Gundam-G-Generation-Eternal-Assistant/issues) 中写明 GGEA 版本、Windows 与模拟器环境、复现步骤，以及出问题前后的操作。日志位于解压目录的 `logs/`。
<br/>
或通过GGEA官方群：[1002150086](https://qm.qq.com/q/zkxGhEiVQQ)  进行BUG反馈、功能建议。


## 开发与贡献

当前源码在主窗口右上角依次提供灯泡、线框 QQ 企鹅、地球（官网）、书本（使用文档）和线框齿轮（设置）入口；悬停图标可查看说明。

开发版「截图与操作 → 打开使命页」支持从主页或关卡菜单进入使命页。位于关卡菜单时，程序先识别标题和底部导航，返回并确认主页后再打开使命；已在使命页时只确认画面。已在雷电模拟器原生 1280×720 横屏验证这三种入口状态。

开发路线图与战舰区域搜索使用连续触点拖动画布。手指沿起点和终点之间的直线加速、减速，在终点短暂停留后抬起。开发版可在「截图与操作」设置起点、终点和时长，点击「精确拖动」试用。首次使用会通过 ADB 将随包的拖动组件复制到所选设备。

源码版本：**v1.5.15.0** · 正式发布 / Release。每日任务列表中的「角色要求（个人基地）」新增设置齿轮。点击即可打开「角色要求」页，调整机体交付保护和夺取单位的 AP 上限。修复雷电 1920×1080 下强化部队委托停在首台 LV1 机体的问题。首卡只匹配等级行的 LV 笔画，进入单位资料后可正确读取 1/90 并继续强化。修复交付机体时漏选已有单位并转去补开发的问题。在游戏筛选好的库存中按卡框定位，读取等级和突破星数，跳过锁定卡，点击合格卡片中央。识别不清时停止本次交付。修复强化部队委托使用材料后机体等级未提升时的超时。游戏直接返回单位资料时，助手会回到原委托核对材料进度，满额后报告。「角色要求」新增「夺取单位任务最大可消耗 AP」，可选 30–990 AP，每档 30，默认 90。每栏每轮共用一个上限。指定作品的 HARD 次数用尽仍未达标时，助手从右向左检查已通关的普通关卡，在有夺取奖励且还有次数的关卡继续略过。满额后回原委托报告，体力、预算或可用关卡耗尽时保留进度。强化部队委托遇到升序首台满级时，会补开发一台系列基准机体后返回原委托强化。强化部队委托沿用游戏默认的等级排序，修复雷电 720p 下选不到首台机体的问题。角色要求列表用英文 OCR 读取各栏的 RANK，修复雷电 1280×720 下的漏识别。简中每日任务默认勾选「角色要求（个人基地）」，可手动取消。承接或报告后的对话和预告会通过点击中央推进。战舰回收后若紧接「角色要求」，助手会从巡航详情返回个人基地。入口未显示时，向地图右侧平移一次，两个地图入口均连续三帧确认位置后才点击。简中「交付机体」显示「获取途径」时，助手在「主要获得方式」的右侧列表找到「开发」卡，点击同一卡上的「移动」。在游戏定位的路线图位置确认目标机体后，按原有数量、等级和费用检查补开发一台。若路线图稳定后仍未识别到目标，会提示先完成该机体的首次开发，结束本次角色要求并继续后续每日任务。简中「请求出击」按剩余次数略过并报告。每日消耗 AP 按体力自动调整次数。简中角色要求支持委托开发，按指定作品开发基准机体并报告。开发列表按原生作品 Logo 选图，多分图作品可进入第二张普通路线图。先识别委托标题。调度资金可直接承接，核对 CAPITAL 金额后交付并领取奖励；强化与机体交付再按角色查表。交付单台机体时若库存没有合格单位，会尝试按对应作品开发一台再交付。「角色要求」标签位于「自动推图」右侧，默认保护有突破星和 LV60 及以上的单位。

补充个人基地「角色要求」列表、角色详情和要求信息页的简体中文识别素材，覆盖页面标题、切换角色与「承接」按钮。

个人基地新增简体中文「角色要求」按钮定位，支持拖动地图后查找。战舰巡航的三张模板移入 battleship_cruise 子目录，已有配置中的默认路径自动迁移。

主要关卡和历史关卡共用作品 Logo 目录 `resources/common/templates/logo/`。已有配置会自动迁移旧版默认路径，自定义目录继续生效。

修复后台已经发现或正在下载新版时，设置页重复查询导致显示更新失败的问题。GitHub 查询达到上限时，显示服务端提供的本地重试时间。更新在独立目录通过校验后即可使用。关卡、配置和 Logo 在窗口空闲时刷新；程序代码在下一次打开时载入。原窗口可继续运行，用户配置、插件和日志仍在原安装目录。跨版本升级复用本机文件，大文件优先使用可用的补丁。修复每日任务从强化培育关卡转入限时活动时，误认关卡菜单并等待事件列表标题超时的问题。主要关卡和历史关卡共用作品 Logo 目录 `resources/common/templates/logo/`。已有配置会自动迁移旧版默认路径，自定义目录继续生效。

限时活动新增事件关卡推图与每日 10 次关卡。可将每日关卡、BOSS 战斗、奖章机体补给和领取使命奖励按勾选项加入同一个每日任务。

多开时，每台模拟器同时只接受一个窗口的任务。第二个窗口点击开始会提示“设备正在使用”，任务结束或中止完成后可再次开始。不同模拟器可以并行执行任务。

当前源码已拆分独立 `net8.0` 核心，Windows 主程序继续使用 WPF；macOS 界面尚未接入。核心边界与平台接入方式见[开发指南](docs/development.md#核心与平台边界)。

此源码版本支持文件级差异更新及完整包兜底；首次安装仍使用完整包。是否可用以正式发布的客户端和差异附件为准，本次源码修改不代表已经发布。

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
