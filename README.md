# GGEA — SD Gundam G Generation ETERNAL Assistant

《SD高达G世纪 永恒》桌面游戏管理工具（v1.3.3.2，正式发布 / Release）。

## 源码与下载

- 源码：[GundamCC/GGEA](https://github.com/GundamCC/GGEA)，默认开发分支为 `main`。
- 正式版：[v1.3.3.2 发行页](https://github.com/GundamCC/GGEA-Release/releases/tag/v1.3.3.2) · [Windows x64 安装包](https://github.com/GundamCC/GGEA-Release/releases/download/v1.3.3.2/GGEA-v1.3.3.2-win-x64.zip) · [SHA-256 校验文件](https://github.com/GundamCC/GGEA-Release/releases/download/v1.3.3.2/GGEA-v1.3.3.2-win-x64.zip.sha256)。
- 使用步骤：[使用说明](使用说明.txt)；问题反馈：[GitHub Issues](https://github.com/GundamCC/GGEA/issues)。

下载 ZIP 并完整解压后运行 `GGEA.exe`，不要将发行页自动生成的 Source code 压缩包当作程序。该版本构建自源码提交 [`9fbe302`](https://github.com/GundamCC/GGEA/commit/9fbe30250579a2e87393de4aeb5aaead0ba09100)。旧的 30 个发行版已于 2026-10-28 下架。

官网与文档站源码独立维护，目前尚未提供已核验的公开站点地址。本仓库维护桌面程序及使用文档。

## v1.3.3.2 — 正式发布 / Release

略过动画正常等待延长至 60 秒，超时后增加一次 30 秒迟到结果恢复；确认结果后完成 OK 与返回校验，不重复执行略过，保留 AP 安全检查和取消。 修复连续单位奖励采样过慢、轮询睡眠越过剩余预算及次数上限状态被素材校验掩盖的问题。

完整版本更新历史见 [CHANGELOG.md](CHANGELOG.md)。运行前仍需核对 AP 方案与关卡解锁状态。

## 本机插件

设置页可查看并启用本机插件，重启后加载。插件协议、安装步骤及独立构建示例见 [插件开发与使用](docs/plugins.md)。普通构建不依赖任何私有仓库；扩展功能由独立插件提供。

## 功能（v1.3.3.2）

- 大师联盟：简中 1920×1080；可选自动编制，逐个挑战零积分对手，按需刷新败北对手；结算 OCR 与 SKIP 同轮检测，晋级后重新读取对手，晋级由游戏处理，列表更新后继续挑战，不检查右侧总分或最高分。画面暂未确认时持续恢复与低频重试，次数耗尽时结束，不消耗钻石补次数。

- 界面语言：简体中文 / 日本語；首次跟随 Windows 界面语言，保存后重启生效；游戏语言单独设置
- WPF 明暗双主题界面，默认夜间模式；设置旁的灯泡可切换主题，偏好保存后重启恢复
- ADB 设备检测与模拟器选择：刷新时仅保留已确认的在线目标，首次发现或目标离线时保持未选择；逐台识别品牌，MuMu 结合管理器实例地址识别，按 Android 启动标识确认并合并重复连接
- 模拟器截图与显示
- 点击 / 滑动（ADB）
- 基于 OpenCvSharp 的模板匹配（ROI、Confidence、中心坐标）
- WaitForImage / ClickImage / WaitImageDisappear 自动化原语
- 日志（界面 + 文件）
- Windows 任务结束提醒：每日队列、独立 AP 消耗、自动招募、自动刷本、自动推图与铁球工厂，统一由设置开关控制
- 模板采集基础工具（截图 → 框选 ROI → 保存 PNG → 匹配测试，按 entry/home/mission 分类存放）
- 设置页：浏览/测试/保存 `adb.exe` 或 BlueStacks `HD-Adb.exe` 路径（保持文件名白名单，避免误选其它程序）
- 启动后台检查并在设置按钮显示新版角标；设置页在线更新：查看说明、下载完整包，默认校验后自动重启安装（可关闭）；失败恢复旧版；GitHub 不可访问时使用维护者配置的备用源
- 一键启动模拟器并打开游戏（模拟器路径/参数可配置，游戏包由区服确定）
- 多模拟器支持：内置 MuMu / 雷电 / BlueStacks / 自定义适配器；安装探测、启动、设备发现与专属能力不再由 UI 写死
- MuMu 适配：默认启动第一个安卓设备（vmindex 0），自动检测安装路径，无需经过设备选择界面；设备刷新优先读取管理器列出的在线实例；查询为空时重试另一种参数，并有界补连管理器遗漏的本机实际监听端口，高编号多开不受前三个实例限制，目标须手动选择
- BlueStacks 适配：自动配对同一安装目录中的 `HD-Adb.exe` 与 `HD-Player.exe`；未发现设备时提示在“设置 → 高级”中开启 Android Debug Bridge
- 自动化画面防护：每日任务、一键进入游戏与自动刷取执行前要求 ADB 截图为 1920×1080 横屏，其他尺寸停止自动点击但仍允许手动截图排查
- 游戏入口：等待健康提示页后自动点击 tap to start 进入游戏（已登录状态）
- 登录活动奖励页：检测到 TAP TO NEXT 时自动点击跳过，没有活动页时直接继续
- 登录奖励页：同样检测底部 TAP TO NEXT 并自动点击跳过（两个奖励页都可能不存在）
- 主界面布局：保存固定功能区域坐标与红点模板（不依赖游戏语言）
- 主界面检测：左下角主界面按钮激活/未选中状态识别；激活状态连续两帧确认后固定点击 `(165,1032)`，并再次确认激活状态才放行；未选中时按识别坐标返回主界面；两者均未命中时仅识别左上角返回按钮并安全回退，最长等待 90 秒
- 收获战舰：个人基地 → 战舰巡航 → 全部回收 → 关闭结果弹窗（固定位置 + 模板确认）
- 战舰出航中：全部回收按钮为暗灰色时自动识别并安全跳过，不误点击
- 商店购物：商店 → 高级商店 → 购买免费培育包 → 确认 → 关闭（完成“于任一商店购物”）
- 钻石安全锁：先检测弹窗中的钻石图标，再检测其右侧红色剩余钻石数量，两项同时命中才判定风险并立即中止
- 每日任务区块：可拖拽任务调整执行顺序，勾选后一键按列表顺序执行（回收战舰 / 商店购买免费礼包等），自动回到主界面
- 领取奖励：奖励页可勾选“领取邮件奖励”（礼物箱流程）与“领取每日使命奖励”，可加入每日任务勾选执行；勾选“领取每日使命奖励”且勾选“领取奖励”时，按队列顺序执行一次并在每日任务最后再执行一次（共两次），未勾选“领取奖励”则不自动执行
- 开发&强化一次：进入开发路线图后以两张原有立绘模板搜索目标，支持不同入场缩放比例；保留当前画布比例，使用标准 ADB 逐行拖动，连续两帧确认同一机体后进入“全部开发 → 执行确认 → 跳过立绘 → 返回主界面”。不依赖 MuMu 滚轮缩放，也不按品牌禁用；首次显示设置、灰色全部开发按钮及失败页面检查保留。
- 开发版（Debug）允许多开，可与发布版同时运行；发布版（Release）重复启动只会激活已有发布版窗口并退出新进程
- 强化培育关卡：确认 UPGRADE STAGES 标题后优先识别并执行一次略过；按钮暗态时安全跳过，仅亮暗模板都未识别时才遍历四种类型
- 关卡菜单入口校验：主要关卡、强化培育和作品导航恢复均先识别左上角“关卡”标题，未确认时停止并提示检查主界面卡顿
- EXE Release 发布（win-x64、Self-contained、Single-file）
- 基础单元测试

完整操作步骤见 [使用说明.txt](使用说明.txt)，同一份说明随发布压缩包分发。

## 环境要求

- 终端用户：Windows 10/11 x64、Android 模拟器与可用 ADB。完整包自包含，不需要 .NET SDK、Python、NDK、JDK 或 Frida。
- 原生 OCR 依赖：安装或修复 [微软最新版 Microsoft Visual C++ v14 Redistributable（x64）](https://aka.ms/vc14/vc_redist.x64.exe)。.NET 自包含不包含 VC++ 运行库，仅安装 x86 版本不能满足本包要求；安装后完全退出并重新启动 GGEA。
- 开发与测试：Windows、Git、PowerShell 7（`pwsh`）、.NET 8 SDK（或兼容 `net8.0-windows` 的更高版本）。

## 使用的项目

- [.NET 8](https://dotnet.microsoft.com/en-us/) 与 [WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)：Windows 桌面程序与界面。正式包采用自包含构建，终端用户无需另行安装 .NET Runtime。
- [OpenCvSharp 4.13.0.20260627](https://github.com/shimat/opencvsharp) 与 [OpenCV 4.13.0](https://opencv.org/)：图像解码和模板匹配；`OpenCvSharpExtern.dll` 随发布包提供。
- [ONNX Runtime 1.23.2](https://github.com/microsoft/onnxruntime) 与 [PaddleOCR PP-OCRv5](https://github.com/PaddlePaddle/PaddleOCR)：在本机使用 CPU 识别关卡编号与 HARD 标题；模型及许可说明见 `resources/common/ocr/`。
- [Android Debug Bridge (ADB)](https://developer.android.com/tools/adb)：连接模拟器、截图和发送操作；发布包不含 ADB，请使用模拟器自带版本或 Android SDK Platform-Tools。

## 构建与测试

```powershell
dotnet build GGEA.sln
pwsh -File tools/test.ps1 -NoBuild
```

默认仅执行核心冒烟检查；直接运行 `dotnet test GGEA.sln` 也使用同一快速范围。核心集验证配置回退、画面规格、识别/点击边界、钻石安全锁及取消/设备互斥，不代表所有模拟器或游戏流程已经实测。

本次修改有相关用例时，合并执行少量定向回归（类名可换成具体方法名）：

```powershell
pwsh -File tools/test.ps1 -NoBuild -Filter 'FullyQualifiedName~ConfigServiceTests'
```

已有测试全部保留。显式需要全量回归时运行 `pwsh -File tools/test.ps1 -Full`，或 `dotnet test GGEA.sln --settings tests/full.runsettings`。日常不默认跑双配置、覆盖率、完整每日流程或多模拟器矩阵；测试失败仍会阻止提交/打包。只有代码修正或检查失败后才重跑受影响的检查。

不同模拟器的安装、连接、截图、识别和任务体验主要由用户亲自使用并反馈。反馈请提供模拟器名称/版本、画面分辨率、所执行的单项任务、预期与实际表现，以及问题附近的脱敏日志；需要识别排查时再提供对应截图。AI 根据具体反馈做定向修复，自动化测试通过不能替代实机验收。

## 开发版打包（支持多开）

在仓库根目录运行以下 PowerShell 命令。开发版沿用 Debug 编译条件，包含位置调整、截图调试工具；不能用 `-c Release` 打包开发版。

```powershell
dotnet build GGEA.sln -c Debug
pwsh -File tools/test.ps1 -Configuration Debug -NoBuild
dotnet publish src/GGEA.App/GGEA.App.csproj -c Debug -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/publish-dev
```

成功后运行 `artifacts/publish-dev/GGEA.exe`，重复双击即可多开。此目录独立于正式发布目录；重新打包前关闭该目录中的所有 GGEA 进程。分发时压缩整个目录，不能只复制 EXE。

开发包来自当前工作区，不具有正式发布入口的固定提交与清单验证保证。
各实例的任务状态独立，Debug 日志为 `logs/ggea-日期-进程号.log`。同一目录的实例仍共用配置、模板与布局文件，保存会影响后续读取；需要独立配置时，将完整开发包复制到不同目录。执行任务前为每个窗口选择不同的模拟器设备，当前没有跨进程的设备任务互斥。

## 正式发布入口

先提交需要发布的源码，再运行（不会创建 Tag、Release 或推送）：

```powershell
pwsh -File tools/release.ps1 -Commit HEAD
```

入口将提交解析为完整 SHA，再用 `git archive` 导出独立快照。未提交文件不会进入发布包。
依次执行资源/文档校验 → Release Build → 核心冒烟检查 → 发布 → 防混用回归 → ZIP 压缩/解压 → 文件清单校验 → EXE 启动自检；任一步失败不输出新 ZIP。完整测试只在显式传入 `-FullTests` 时执行；`tools/build-dev.ps1` 和 `tools/publish.ps1` 同样支持该参数。

产物为 `artifacts/dist/GGEA-v1.3.3.2-win-x64-<提交前12位>.zip` 及 `.zip.sha256`。
完整发布后，重复执行 `tools/package-dist.ps1` 会优先读取本次提交的解压验收目录，重新校验现有 ZIP，不会替换运行中的 `artifacts/publish/` 或覆盖同名产物。SHA-256 用于完整性核验，不代替数字签名。
`build-info.json` 包含完整提交、程序版本、构建参数、SDK、构建时间、资源/配置内容版本和逐文件 SHA-256。
资源/配置版本是排序后路径与摘要的内容哈希，无须维护另一个手工版本号。

实际 ZIP 解压验收目录保留为同级的 `*-verified/`。**人工验收也必须从该目录启动程序**，按使用说明检查设备、截图和单项流程；启动自检只检查原生库、PNG、JSON 完整性，不代表游戏、UI 或所有模拟器实测通过。
完整构建证据保留在 `build/release-work-*/`，均不纳入 Git。

底层 `tools/publish.ps1` / `tools/package-dist.ps1` 独立调用默认要求干净工作区；完整正式交付请使用上述统一入口。
打包严格核验已有 EXE 与 build-info，只复制该次构建文件，绝不使用当前仓库配置/说明覆盖旧发布目录；修改发布目录后需重新构建。
包内包括用户说明、发布说明及运行资源，排除 Markdown 和调试符号。不要单独复制 EXE，必须保持原生库、`config/`、`resources/` 的完整目录结构。

## 快速使用

1. 完整解压发布包，启动模拟器并开启 ADB 调试。
2. 打开 `GGEA.exe`，点击右上角「设置」：选择 `adb.exe` 或 BlueStacks 的 `HD-Adb.exe`，确认模拟器主程序、启动参数、区服与游戏语言后保存。
   - MuMu：类型自动识别为「MuMu 模拟器」，GGEA 会自动检测 `MuMuManager.exe` 并默认启动第一个安卓设备（vmindex 0）
   - 雷电：选择 `ldconsole.exe`，参数自动补全为 `launch --index 0`
   - BlueStacks：选择 `HD-Adb.exe` 或 `HD-Player.exe` 后，程序会自动补全同一安装目录中的另一项；还需在 BlueStacks“设置 → 高级”中启用 Android Debug Bridge
3. 刷新设备，手动选择目标模拟器，后续刷新保留已确认的在线目标；需要时点击「启动模拟器 / 打开游戏」。
4. 先在游戏主界面手动确认一次页面与账号状态，再勾选需要的每日任务并点击「任务开始」。
5. 使用「AP消耗」前，先确认作品与 HARD 关卡配置、滑动距离和模板均已在当前模拟器实测；执行中如出现异常，立即停止并查看 `logs/`。
6. 截图、框选 ROI、保存模板与模板匹配测试属于维护工具，操作说明见发布包中的 `使用说明.txt`。

## 新增模拟器适配

模拟器支持由 `EmulatorTypes.Profiles` 元数据与 `IEmulatorAdapter` 内置适配器共同驱动：
档案提供名称、识别关键字、ADB/启动程序文件名和默认参数；适配器封装安装探测、启动、设备发现与专属输入能力。

新增一种模拟器通常分两种情况：

1. 在 `EmulatorTypes.Profiles` 追加一条 `EmulatorProfile`：
   - `Type`：类型标识，写入配置的 `emulatorType`
   - `DisplayName`：设置页下拉显示名
   - `DefaultArgs`：一键启动默认参数（可留空）
   - `DetectionKeywords`：识别 ADB / 模拟器路径的关键字（如 "MuMu"、"LDPlayer"、"BlueStacks"）
   - `AdbExecutableNames` / `LauncherExecutableNames`：配套程序文件名
2. 标准 ADB 且无需专属行为时可复用 `GenericEmulatorAdapter`；需要固定端口、安装探测或专属输入时新增内置适配器，并在 `EmulatorAdapterFactory` 注册。

不要在 `MainWindow` 中增加品牌判断或端口规则；未经过真机验证的能力必须保持禁用并给出原因。

## 目录结构

```text
GGEA/
├── AGENTS.md / agent.md
├── README.md
├── 使用说明.txt           发布包根目录的最终用户说明
├── GGEA.sln
├── src/GGEA.App/       WPF 主程序
├── resources/common/templates/ 识别模板
├── config/             运行配置
├── tools/              开发 / 发布脚本
└── tests/GGEA.Tests/   单元测试
```

## Hard 关卡数据维护

`resources/common/hard_stages.json` 维护各作品的 Hard 关卡信息，格式：

```json
{
  "hardStages": [
    {
      "work": "Mobile Suit Gundam",
      "hardCount": 3,
      "screenshot": "mobile_suit_gundam.png"
    }
  ]
}
```

- `work`：作品名（第一段）
- `hardCount`：该作品的 Hard 关卡数量，1~5（第二段）
- `screenshot`：该作品的截图文件（第三段）

## 主要关卡导航

主要关卡使用“左端绝对定位 + 居中相对导航”策略：

- 第一次从主界面进入画布时，逐次向左拖动并查找初代高达锚点；识别后再大幅拖动一次到达边界；
- 从作品具体关卡页退出后，当前作品固定居中，直接按两个作品的绝对坐标差快速切换；
- 仅在左端复位时识别固定的初代高达锚点，目标作品进入和连续切换仍使用坐标计算；
- 最后一次作品定位滑动保留五秒以内的动画设置；较长设置改为连续截图确认画布停稳，再识别并点击作品节点；
- 整屏滑动无法把节点带入安全点击区时，才会补一次固定半屏滑动；其余路径仍优先使用已校准的整屏滑动；
- 普通与挑战模式共享作品绝对位置，挑战模式接口已经预留但尚未启用。

`resources/common/stage_work_positions.json` 保存作品稳定绝对位置。新增作品时追加配置即可，
不要在代码中写死作品总数。标准滑动画布距离可通过 `stageSwipeCanvasDelta` 调整，
该值需要根据真实模拟器行为校准。左端复位使用 `mobile_suit_gundam.png` 作为锚点，
最大搜索次数与锚点阈值均可通过配置调整。

半屏滑动补偿在 `config/ggea.config.json` 中按手指移动方向区分（与画布移动方向相反）：

- `stageHalfSwipeLeftTapCorrection`：手指从右向左拖动（1500 → 960），缺省值 `200`；
- `stageHalfSwipeRightTapCorrection`：手指从左向右拖动（420 → 960），缺省值 `-200`。

两个参数均支持正负数：正数让点击 X 向右偏移，负数向左偏移，`0` 关闭对应方向补偿。
若需要将原有补偿方向反转，可将左滑设为 `-200`、右滑设为 `200`。
补偿同时计入预计视口偏移，并在补偿后检查点击安全区；完整滑动不应用此参数。
修改配置后重启程序生效，具体数值需实机校准。

右侧“AP消耗”TAB 会从 `hard_stages.json` 加载作品及其 Hard1～Hard5 可选范围。
勾选关卡后可拖拽作品调整顺序；每日任务中的“消耗AP”会严格按列表从上到下执行。
当前顺序及勾选状态可以保存为多个命名配置，并通过下拉框快速切换。
AP 消耗已接入略过流程：进入作品详情后先识别右下角“选择”按钮；暗态表示作品未解锁，会安全返回画布并继续下一部作品。可用时点击按钮进入关卡页，优先识别当前 HARD 标题；标题连续未命中 2 秒时改为识别左侧轨道的黄色选中节点，确认 HARD 编号后按五槽位映射倒序选中已解锁关卡，并依次执行“略过 → SKIP RESULT → OK → 次数上限暗态确认”。

左侧“每日任务”列表同样支持拖拽排序，列表从上到下的顺序就是“任务开始”按钮实际执行任务的顺序。
拖拽排序参考 Ant Design 表格示例：拖动行时显示虚线插入指示，目标在拖动行下方时显示底边线、上方时显示顶边线。
“领取奖励”与“消耗AP”任务左侧带齿轮设置入口，点击任务行或齿轮可跳转到对应的“奖励”或“AP消耗”TAB。
“奖励”TAB 中的“领取邮件奖励”已接入现有礼物箱流程，“领取每日使命奖励”已接入使命页全部领取。

## 当前限制

- “开发&强化一次”默认勾选；切换到不兼容或品牌未确认的设备时禁用，勾选意愿保留。
- 账号区域的“设备已连接”仅表示 ADB 在线，并不表示已完成游戏账号状态检测。

- 图像识别依赖随发布包提供的 OpenCvSharp 原生库；不要单独移动或删除 `OpenCvSharpExtern.dll`。
- 若出现 `Microsoft.ML.OnnxRuntime.NativeMethods` 初始化异常，先安装或修复上述 VC++ x64 运行库并重启程序。仍失败时重新完整解压匹配版本的发布包，保留 `onnxruntime.dll` 与 `resources/common/ocr/`；新诊断会记录底层异常和错误码，不能仅凭外层错误认定缺少运行库。
- 领取每日使命奖励依赖网络与账号同步状态，遇到人机验证、数据同步确认或下载确认时会中止启动。
- ADB 与游戏界面需要用户本机正确配置；游戏更新、账号状态差异或模板过期均可能导致自动化停止。

证据截图保存位置：自动留存的商店确认原始截图与同名诊断说明统一保存到程序目录 logs/shop；主界面识别证据包保存在 logs/recognition。开发扫描与位置验证工具的截图及配套 JSON 统一保存到项目 logs/captures。旧目录中的历史证据不会自动迁移；MuMu 截图进程间传输临时文件仍在读取后删除，识别模板仍保存在 resources/common/templates。

## 本机目录管理

`artifacts/` 只保存发布目录和分发包；本机依赖、运行组件、构建缓存及历史资料分别存放于 `dependencies/`、`runtime/`、`build/`、`research/` 等根目录。目录用途、清理条件和旧布局迁移方法见 [本机目录约定](docs/local-directories.md)。

## 区服与语言素材

设置 → 模拟器与游戏中选择区服和游戏实际显示语言。日服包名为 `com.bandainamcoent.gget_JP`，国际服为 `com.bandainamcoent.gget_WW`，包名由区服自动确定，不再提供自定义输入。国际服支持简体中文、英语、韩语、中文（台湾）和中文（香港特别行政区），日服只支持日语。保存后在空闲状态重建服务并生效，不修改助手的中文界面。

| 目录 | 用途 |
| --- | --- |
| `resources/common/` | 通用资源，现有素材全部原样迁入 |
| `resources/zh-CN/` | 简体中文 |
| `resources/ja/` | 日语 |
| `resources/en/` | 英语 |
| `resources/ko/` | 韩语 |
| `resources/zh-TW/` | 中文（台湾） |
| `resources/zh-HK/` | 中文（香港特别行政区） |

例如，将中文按钮从 `common/templates/gift/button_collect_all.png` 移到 `zh-CN/templates/gift/button_collect_all.png`，日语对应素材放在 `ja/templates/gift/button_collect_all.png`。所有语言保持相对子路径和文件名一致，无需修改任务代码。配置和代码仍使用逻辑路径 `resources/templates/...`；读取时先查当前语言；没有语言文件则直接使用 common。图像识别中，语言素材未命中时，在同一帧按原阈值和区域补试一次同路径同名 common 素材；命中即停止，不回退到其他语言。配置与 OCR 文件仍仅按文件是否存在选择，不参与图像匹配回退。

中文基准移出 common 后，其他语言必须提供对应文件；缺失时按原有缺失素材处理停止相关操作，不降低匹配阈值。目录扫描合并两层并去重，不要求复制完整素材树。模板采集写入当前语言目录，外部自定义目录保持原样。手工整理素材后重新启动助手，以清除已经加载的模板缓存。

选择一种语言不代表该语言的流程已经适配完成：日语已针对上述五项每日任务完成 MuMu 1920×1080 实机操作和生产服务截图回放，消耗 AP 另已完成 ID 1、2 的 HARD 1～3 实机略过及生产服务截图回放（未触发 AP 不足与玩家升级）；其余任务及其他语言尚未逐项验证；不能据此宣称全流程或所有模拟器兼容。升级时旧 `resources/stage_work_positions.json` 用户坐标继续优先读取，新目录文件同样受到更新保留规则保护。

## 开源许可

自有代码采用 [Apache-2.0](LICENSE)，适用范围见 [许可证说明](LICENSE_SCOPE.md)。第三方组件、OCR 模型、游戏截图、识别模板和图标按各自权利与许可处理，不因本项目开源而自动获得再分发授权。本项目为非官方社区工具。

开源准备状态见 [核查与待办](docs/open-source/readiness.md)，组件声明见 [第三方软件说明](resources/common/licenses/NOTICE.txt)，游戏素材见 [来源与隐私清单](docs/open-source/assets.md)。源码与当前正式版已公开；素材授权及第三方原生构建适用性等待确认事项仍保留在清单中，不因发布完成而视为解决。
