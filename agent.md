# GGEA 开发规范

## 当前项目与规则入口

GGEA 是 Windows 10/11 x64 桌面助手，通过 ADB 和受支持的设备截图通道操作 Android 模拟器。当前已具备每日任务、AP 消耗、自动周回、招募、推图、大师联盟、插件和在线更新；不要再按最初 v0.1 原型阶段理解项目。

本文件描述当前工程约束；验证、日志、版本、提交与跨仓库协作遵守 `AGENTS.md` 和 `docs/workspace-policy.md`。用户只要求分析或 Review 时保持只读。开始工作时核对实际仓库、分支、远程、暂存区与已有修改，不在父目录或归档仓库开发。

## 技术栈与职责

- 主程序为 C#、.NET 8、WPF。现有 OpenCvSharp/OpenCV 用于图像解码与模板匹配，Microsoft.ML.OnnxRuntime CPU 用于离线 OCR；这些已经是正式依赖，不再适用“禁止 OpenCV、深度学习”的原型约束。
- `src/GGEA.App` 包含 UI、Config、Device、Vision、Automation、Tasks、Plugins、Updates、Localization 与 Logging。复用现有职责边界，业务状态不得复制到表现层。
- `src/GGEA.Plugin.Abstractions` 是公共插件协议；可选扩展在独立 GGEA-Extensions 仓库维护。接口变化必须核对兼容性，不能无意把可选扩展依赖带回主程序。
- `src/GGEA.Updater` 是独立更新进程；保留安装锁、文件校验、回滚与用户目录保护。`src/GGEA.ReleaseManager` 是开发发布工具，不进入用户包。
- 新依赖和抽象需要当前任务的具体收益。不要为假想需求引入新服务、工作流框架或模型，也不要为了少量 Diff 省略必要的失败处理与资源释放。

## 识别、设备与安全边界

- 保持“截图 → 识别 → 有证据的点击 → 新截图确认”链路。未知页面、歧义或识别失败不得授权点击；保持取消、有限等待、设备互斥及 AP/钻石安全限制。
- 使用真实截图、已有模型与资源，不虚构游戏页面或因测试失败放宽阈值。设备和游戏操作须符合 `AGENTS.md` 的实机验证授权范围。
- 当前资源按 `resources/common` 与 `resources/zh-CN`、`ja`、`en`、`ko`、`zh-TW`、`zh-HK` 分层；OCR 模型及许可位于 `resources/common/ocr`。不要再把 `resources/templates` 当作统一新资源位置。
- 管理原生对象、事件订阅与后台任务的生命周期。高成本识别及设备等待不得阻塞 UI 线程。
- 原生运行库探测失败不得启动自动化；区分包内 DLL 缺失、架构错误与依赖加载失败。VC++ 安装仅由用户明确选择启动，保留微软官方入口，安装失败不得当成成功。

## 界面与文档

- 沿用现有 WPF 主题、组件和布局，确保文字可读、关键操作反馈明确；视觉改动检查实际画面及相关状态。
- UI 使用 `Localization/L.cs` 与嵌入的 `Localization/ja.json`，新增用户文案同步日文；日志与业务真值不因语言切换改变。
- 使用说明以真实行为为准。同步实际受影响的中日文 README、离线说明和网站页面；不要将源码更新称为已经发布。
- 关键类与方法用中文注释解释职责、约束和生命周期，避免逐行翻译代码。

## 构建、验证与发布

- 产品版本唯一来源为 `src/GGEA.App/GGEA.App.csproj`。按项目版本规则在任务完成时更新一次，并同步 README、RELEASE.txt、使用说明.txt、CHANGELOG.md，执行 `Get-ReleaseVersion`。
- 日常进行一次相关配置 Build；使用 `tools/test.ps1 -NoBuild -Filter 'FullyQualifiedName~相关测试'` 合并冒烟与定向验证。纯文档无需程序构建；失败必须保留并修复，不能关闭检查制造通过。
- 使用现有 `tools/build-dev.ps1`、`tools/build-normal.ps1`、`tools/publish.ps1` 和 `tools/package-dist.ps1`，先阅读入口参数和产物规则。普通验收包、开发包与固定提交正式包不可混称。
- Windows 发布使用 win-x64、自包含 .NET 与单文件主程序；OpenCV、ONNX、运行库安装脚本、配置、资源及许可仍需完整外置。自包含 .NET 不代表包含 VC++ 系统运行库。
- 打包改动检查真实发布输出、ZIP 文件清单与相关启动路径；正式发布保留源码身份、摘要、资源和 EXE 自检。
- 完成必要验证、归属与敏感信息检查后显式暂存并本地 Commit；默认不 Push、不部署、不创建 Release。不提交缓存、凭证或用户数据。
- 交付说明实际验证结果、未验证条件及各仓库影响，不将本地编译、提交与正式发布混为一谈。
