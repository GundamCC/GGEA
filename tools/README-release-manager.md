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

源码仓库路径保存到 `%LOCALAPPDATA%\GGEA\ReleaseManager\settings.json`，字段为 `schemaVersion` 和 `sourceRepository`，不含凭证。首次运行仍向上查找源码目录；选择路径后自动保存，重启后恢复。点击“配置目录”可打开本机存储位置。

旧版没有持久化配置文件，无需迁移旧 JSON。现有环境变量和 Git 登录保持不变，公共工具不读取或迁移认证。配置损坏、出现未知字段或不支持的版本时保留原文件并提示，不覆盖原数据；请在项目之外备份并修复配置后重启。构建子进程不继承 `GH_TOKEN` / `GITHUB_TOKEN`。

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
