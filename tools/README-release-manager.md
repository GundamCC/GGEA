# GGEA 本地构建管理器

`GGEA.ReleaseManager.exe` 提供普通版、开发版的本地构建、打包与产物打开入口。工具不提供人工验收勾选、源码推送、远程发布或凭证管理功能，也不进入普通用户的程序包。

## 使用

选择源码仓库后，可执行：

- **建立普通版本**：调用 `tools/build-normal.ps1`，从当前工作区生成 `artifacts/publish/`。
- **打包普通版本**：调用同一脚本的 `-Package`，在 `artifacts/dist/` 生成 ZIP 和 SHA-256。
- **完整构建并打包**：调用 `tools/release.ps1`，从固定的干净提交导出源码、构建、测试并核验产物。此脚本只生成本地包，不进行远程写入。
- **建立开发版本**：调用 `tools/build-dev.ps1`，Debug 构建、核心冒烟检查后更新 `dev/`。
- **打包开发版本**：调用 `tools/build-dev.ps1 -Package`，生成独立的 `artifacts/dev-dist/` 包，不压缩已有运行目录中的配置和日志。
- **打开普通版本 / 开发版本 / 文件路径**：启动对应产物或打开产物目录；缺失时在执行记录中提示。

普通版和开发版工作区构建允许未提交修改，产物记录当前提交及脏状态；完整构建要求干净提交。测试或校验失败不会冒充成功。完整历史回归仅在显式传入 `-FullTests` 时执行；日常定向检查用 `tools/test.ps1 -Filter 'FullyQualifiedName~类名或方法名'`。

## 本机配置

源码仓库路径保存到 `%LOCALAPPDATA%\GGEA\ReleaseManager\settings.json`，字段为 `schemaVersion`（新版为 2，兼容读取旧版 1）和 `sourceRepository`，不含凭证。相对路径以发布管理器 EXE 所在目录为基准（根目录运行时为 `.`），不受启动工作目录影响。同盘选择保存为相对路径，跨盘选择保留绝对路径。首次运行向上查找源码目录；旧绝对路径配置优先迁移到工具所在仓库，未找到时保留原目标。已有相对路径选择在重启后恢复；选择路径后自动保存。点击“配置目录”可打开本机存储位置。

旧版 schemaVersion 1 的绝对路径按上述规则迁移；新版跨盘绝对路径保持用户选择。现有环境变量和 Git 登录保持不变，公共工具不读取或迁移认证。配置损坏、出现未知字段或不支持的版本时保留原文件并提示，不覆盖原数据；请在项目之外备份并修复配置后重启。构建子进程不继承 `GH_TOKEN` / `GITHUB_TOKEN`。

Git 忽略 `.local/`、`.env`、`.env.*` 和明确命名的本机管理器配置；无秘密的 `.env.example` 可提交。忽略规则不保护已跟踪文件。打包检查拒绝维护者私有目录、配置备份、常见凭证或证书文件混入普通版、开发版和正式候选包。

## 构建工具自身

```powershell
pwsh -File tools/build-release-manager.ps1
```

脚本生成根目录 `GGEA.ReleaseManager.exe` 并校验摘要；该 EXE 已被 Git 忽略。重建前需关闭该 EXE。工具需要完整源码目录、Git、.NET SDK 与 PowerShell；优先使用 PowerShell 7。工具调整不递增 GGEA 产品版本。

## 在线更新附件约定

正式发布入口迁移与旧客户端过渡步骤见 [发布迁移说明](../docs/release-migration.md)。在完成正式打包、双仓库过渡发布及升级核验前，不视为线上迁移完成。

新客户端优先读取源码仓库 `GundamCC/GGEA` 的最新正式 Release。继续上传精确命名的 `GGEA-v四段版本-win-x64.zip` 与同名 `.zip.sha256`，不需要新增服务器或 Token。附件尚未上传完整时，客户端会提示稍后重试，不会安装半成品。ZIP 保留现有打包脚本的一层根目录（允许清单提交号前 12 位后缀）和 `build-info.json`；客户端兼容 Windows ZIP 分隔符并核对目录提交号。发布校验额外要求 `GGEA.Updater.exe`；应用项目的 Publish 目标自动生成自包含更新器。

在线安装使用 GitHub HTTPS、精确附件地址、SHA-256 和包内文件清单校验；SHA-256 不等同于独立数字签名，信任边界为官方 GitHub 仓库及账号。开发版只允许检查；包含本次更新支持的本地普通版可以在线升级为正式包，旧普通版需先手动更换程序；今后版本需继续保留发布清单格式和更新器。

更新器在安装目录 `.ggea-update` 留存日志与备份，按旧、新清单更新程序文件，不清空安装目录。所有已有 `config/` 文件及 `resources/stage_work_positions.json` 保留，未知文件保留；被替换的官方资源原文件可从备份找回。发布脚本的逐文件校验仍针对原始发布包，不能用更新后的用户安装目录重新生成正式包。

### 文件级差异附件

支持差异更新的客户端先寻找 `GGEA-v目标版本-from-v旧版本-win-x64.delta.zip` 和同名 `.sha256`。附件必须与完整 ZIP 一同发布，大小不小于完整包时客户端直接使用完整包。没有匹配附件的旧版本继续全量更新，备用源 v1 协议也继续提供完整包。首次获取支持差异更新的客户端仍需完整包；源码实现不代表附件已经发布。

从两个**未运行、未修改的原始正式包解压目录**生成附件：

```powershell
pwsh -File tools/package-delta.ps1 -BaseDirectory <旧版原始解压目录> -PublishDirectory <新版已验证解压目录> -OutputDirectory <输出目录>
```

也可在 `tools/release.ps1` 或 `tools/package-dist.ps1` 传入 `-BaseDirectory <旧版原始解压目录>`，在完整 ZIP 验收后生成差异附件；不传时保留原全量流程。构建管理器现有按钮仍只生成完整包，需要差异附件时使用上述命令。可为多个旧版本分别生成附件，不覆盖已有同名产物。脚本验证两端发布身份、文件集合与摘要，复读 ZIP 验证内容；它不代替完整发布的 EXE 自检，也不联网发布。

差异 ZIP 沿用目标完整包的一层目录，包含目标 `build-info.json`、`delta-info.json`、新增或变化文件，以及所有用户配置默认文件。`delta-info.json` 的 `schemaVersion` 为 1，`baseVersion` 是四段旧版本，`baseManifestSha256` 绑定旧包原始 `build-info.json` 字节摘要。客户端校验附件、已下载文件和基线身份，复制并校验未变化文件，构成完整暂存包后交给原更新器。只删除旧清单管理且新清单不再包含的非用户文件；未知文件、插件、日志与已有用户配置保留。相同内容文件不重复备份与替换，失败仍按日志恢复。

差异包连接失败或本机基线不可复用时自动回退完整包；取消、差异包摘要错误或内容校验失败直接停止，不能通过回退掩盖坏包。下载准备仍需足够磁盘空间重建完整包，并在安装前检查备份与替换空间。手动安装必须使用完整 ZIP，不能把差异 ZIP 直接覆盖安装目录。

### 动态备用源（v1.1.11.0 起）

随包的 `config/update-sources.json` 初始为 `{"schemaVersion":1,"sources":[]}`，不预填任何第三方镜像。客户端在每次启动检查或手动检查时，并行读取固定入口：

```text
https://raw.githubusercontent.com/GundamCC/GGEA/main/update-sources.json
```

维护者以后在**GGEA 仓库 main 分支根目录**修改这个文件即可，无须更换客户端、Tag 或 Release 附件；根目录文件随源码提交推送后才生效，随包的 `config/update-sources.json` 不会自动上传。旧客户端仍读取 GGEA-Release 根目录的同名文件，该兼容入口必须保留。入口不存在、离线、超时或配置损坏时保留原有缓存。成功读取并校验后原子写入安装目录 `.ggea-update/sources-cache.json`，即使程序已经是最新版也照常缓存。有效的空列表明确清空远程缓存。先前的 v1.1.10.0 不支持该协议，必须先安装支持版本。

备用源仅由维护者管理，用户界面不提供导入或编辑入口。当前地址列表为空；维护者提供服务器后，可在后续版本配置 `config/update-sources.json`，或通过上述固定入口下发并缓存。没有缓存且 GitHub 完全不可访问的客户端，须等待网络恢复或手动安装包含备用源的新正式包。同 ID 的随包配置优先于缓存，最多使用 4 个来源。

以下是格式示例，`example.com` 为占位地址，不能直接作为可用来源发布：

```json
{
  "schemaVersion": 1,
  "sources": [
    {
      "id": "backup",
      "name": "备用下载站",
      "manifestUrl": "https://updates.example.com/ggea/latest.json",
      "allowedHosts": ["updates.example.com", "download.example.com"]
    }
  ]
}
```

`manifestUrl` 指向普通静态 JSON，不需要实现 GitHub API。版本清单如下，`sha256` 必须替换成完整 ZIP 的真实 64 位十六进制摘要，`packageSize` 必须是真实字节数，`notes` 填写本次更新内容：

```json
{
  "schemaVersion": 1,
  "runtime": "win-x64",
  "version": "1.1.11.0",
  "notes": "本次更新内容",
  "packageName": "GGEA-v1.1.11.0-win-x64.zip",
  "packageUrl": "https://download.example.com/ggea/GGEA-v1.1.11.0-win-x64.zip",
  "packageSize": 123456789,
  "sha256": "<完整 ZIP 的 SHA-256>"
}
```

先上传已经验收的完整 ZIP（与 GitHub 包字节一致），再更新版本清单，最后下发来源配置。清单和下载地址均须 HTTPS；每一次重定向的域名也必须列入该来源 `allowedHosts`，不支持通配符、IP 地址、本机域名、内嵌凭证和非标准端口。不应在配置中填写秘密或长期访问令牌。

GitHub 检查成功时保持 GitHub 为准；连接失败后按来源顺序检查。落后的备用源不会阻止继续寻找新版。下载网络失败后只接受相同版本、包名和字节数的备用包；已取得 GitHub 摘要时，镜像摘要必须相同。若 GitHub 未提供摘要且校验文件也无法访问，则使用已信任来源清单的摘要。摘要冲突、无效地址、包校验错误和用户取消会直接停止，不通过轮换来源绕过安全校验。该机制不提供独立数字签名，信任边界包含分发仓库、维护者授权的备用源及随包配置。
