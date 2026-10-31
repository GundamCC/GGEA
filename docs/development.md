# 开发、构建与发布

以下命令均在桌面程序仓库根目录执行。

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

文件级差异包可通过 `tools/release.ps1 -BaseDirectory <旧版原始解压目录>` 随完整包生成，也可单独执行 `tools/package-delta.ps1`。附件命名、基线校验、完整包兜底与发布步骤见[构建工具说明](../tools/README-release-manager.md#文件级差异附件)。差异包不能单独作为安装包分发。

实际 ZIP 解压验收目录保留为同级的 `*-verified/`。**人工验收也必须从该目录启动程序**，按使用说明检查设备、截图和单项流程；启动自检只检查原生库、PNG、JSON 完整性，不代表游戏、UI 或所有模拟器实测通过。
完整构建证据保留在 `build/release-work-*/`，均不纳入 Git。

底层 `tools/publish.ps1` / `tools/package-dist.ps1` 独立调用默认要求干净工作区；完整正式交付请使用上述统一入口。
打包严格核验已有 EXE 与 build-info，只复制该次构建文件，绝不使用当前仓库配置/说明覆盖旧发布目录；修改发布目录后需重新构建。
包内包括用户说明、发布说明及运行资源，排除 Markdown 和调试符号。不要单独复制 EXE，必须保持原生库、`config/`、`resources/` 的完整目录结构。

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

## 本机目录管理

`artifacts/` 只保存发布目录和分发包；本机依赖、运行组件、构建缓存及历史资料分别存放于 `dependencies/`、`runtime/`、`build/`、`research/` 等根目录。目录用途、清理条件和旧布局迁移方法见 [本机目录约定](local-directories.md)。
