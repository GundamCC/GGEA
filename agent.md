# GGEA 开发规范

## 1. 项目定位

项目名称：

**GGEA — SD Gundam G Generation ETERNAL Assistant**

目标游戏：

**《SD高达G世纪 永恒》**

运行环境：

- Windows
- Android 模拟器
- ADB 控制游戏

当前目标是制作一个简单、稳定、易用的桌面游戏管理工具。

主要功能：

- 一键每日
- 奖励领取
- 游戏内略过 / 扫荡
- 简单账号状态检测
- 模拟器管理
- 图像识别
- 自动点击

核心流程：

```text
截图
↓
识别目标
↓
点击
↓
等待页面变化
↓
识别下一个目标
```

不要过度设计。

---

# 2. 技术栈

## 主语言

**C#**

优先使用：

- C#
- .NET
- WPF
- OpenCV 相关 C# 封装或简单图像处理库
- ADB

绝大多数代码应使用 C# 完成，包括：

- UI
- ADB
- 自动化流程
- 图像识别调用
- 配置
- 日志
- 任务
- 模拟器管理

## C++

只有在以下情况才考虑使用 C++：

- C# 无法满足性能需求
- 必须调用原生 OpenCV 能力
- 某个算法使用 C++ 明显更加合理
- 已确认 C# 实现存在实际瓶颈

不要因为“以后可能需要”而提前建立复杂的 C++ 模块。

如果使用 C++，应通过简单稳定的 DLL C API 与 C# 通信。

---

# 3. 推荐工程结构

```text
GGEA/
├── AGENTS.md
├── README.md
├── GGEA.sln
│
├── src/
│   └── GGEA.App/
│       ├── UI/
│       ├── Device/
│       ├── Vision/
│       ├── Automation/
│       ├── Tasks/
│       ├── Config/
│       └── Logging/
│
├── native/
│   └── GGEA.Native/       # 仅必要时建立
│
├── resources/
│   └── templates/
│
├── tools/
├── config/
└── tests/
```

保持项目结构简单。

---

# 4. UI

使用 **WPF** 构建 Windows 桌面界面。

UI 目标：

> 简洁、现代、清晰、美观。

避免：

- 传统 WinForms 风格
- 大量复杂边框
- 过度动画
- 花哨渐变
- 拥挤布局

建议采用：

- 清晰的信息层级
- 合理留白
- 卡片式区域
- 统一圆角
- 统一按钮尺寸
- 简洁图标
- 深色 / 浅色主题至少保证一种完成度较高

第一版主界面至少包含：

```text
GGEA

模拟器
[设备选择] [刷新]

连接状态

每日任务
[开始]

账号状态

截图 / 运行状态

日志
```

自动化运行时不得阻塞 UI 主线程。

---

# 5. ADB

第一阶段实现：

```text
ListDevices
Screenshot
Tap
Swipe
KeyEvent
```

优先直接调用 `adb.exe`。

截图：

```text
adb -s SERIAL exec-out screencap -p
```

点击：

```text
adb -s SERIAL shell input tap X Y
```

ADB 错误必须记录日志并显示清晰错误。

---

# 6. 图像识别

第一阶段以模板匹配为主。

需要支持：

```text
FindTemplate
Confidence
ROI
X / Y
Width / Height
CenterX / CenterY
```

模板统一放在：

```text
resources/templates/
```

模板主要通过：

```text
ADB实际截图
↓
选择ROI
↓
裁剪
↓
保存PNG
```

获得。

优先使用真实游戏截图制作识别模板，不要为了 UI 自动化专门拆游戏资源。

识别失败时禁止继续执行对应点击。

---

# 7. 模板采集工具

GGEA 应逐步提供简单的开发辅助工具。

推荐支持：

```text
获取当前模拟器截图
↓
鼠标拖选区域
↓
裁剪预览
↓
输入模板名称
↓
保存PNG
```

模板保存到：

```text
resources/templates/
```

同时最好能够测试：

```text
匹配结果
Confidence
ROI
坐标
```

方便快速制作每日任务素材。

---

# 8. 自动化

自动化流程使用普通 C# 代码。

基础能力：

```text
WaitForImage
ClickImage
ClickIfExists
WaitImageDisappear
Tap
Swipe
Delay
```

等待操作需要：

```text
Timeout
PollInterval
Threshold
```

禁止无限等待。

优先：

```text
识图
↓
获得目标中心
↓
点击
↓
确认下一页面
```

减少盲目使用固定坐标。

---

# 9. 当前不做

除非明确要求，否则不要实现：

- 战斗 AI
- 自动寻路
- YOLO
- 深度学习
- MaaFramework
- MaaAssistantArknights 代码集成
- 插件系统
- Lua
- 工作流 DSL
- Web 后端
- 数据库
- 微服务
- 云服务
- 复杂任务框架

保持 GGEA 简单。

---

# 10. EXE 打包

GGEA 最终必须能够作为 Windows EXE 运行。

开发过程中需要保证：

```text
Release
↓
Publish
↓
生成可直接运行的EXE
```

优先使用 .NET Publish。

推荐目标：

```text
win-x64
Release
Self-contained
Single-file
```

例如：

```bash
dotnet publish src/GGEA.App/GGEA.App.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true
```

最终产物应放入：

```text
artifacts/publish/
```

不要把：

- bin/
- obj/
- 临时编译文件
- 本机缓存

提交到 Git。

是否提交最终 Release 二进制，根据后续发布方案决定。

---

# 11. 开发与测试规则

每次修改功能后必须：

```text
完成代码
↓
Build
↓
运行核心冒烟与本次改动的少量相关测试
↓
必要时进行定向本地验证；模拟器体验交由用户试用反馈
↓
确认成功
↓
Git Commit
```

日常只构建一次，按本次改动选择 Debug 或 Release；默认使用快速测试范围：

```bash
dotnet build GGEA.sln
pwsh -File tools/test.ps1 -NoBuild
```

有直接相关的回归用例时，将其与冒烟集合并执行，不再重复运行冒烟：

```powershell
pwsh -File tools/test.ps1 -NoBuild -Filter 'FullyQualifiedName~ConfigServiceTests.Load_InvalidJson'
```

`dotnet test` 也默认筛选核心冒烟集。完整历史测试保留，但不作为每次修改和打包的默认步骤；只有明确要求全量回归，或跨核心模块改动确有必要时，使用 `pwsh -File tools/test.ps1 -Full`。不默认执行覆盖率、长流程、多品牌矩阵或 Debug/Release 双配置，不为省时修改生产等待、安全校验或测试断言。

不同模拟器的连接、截图、识别和游戏流程以用户实机体验与反馈为主要验收依据。AI 默认不操作游戏或开展反复截图、长时间巡检；收到具体问题后再做针对性验证。新增测试集中在实际反馈问题与本次核心风险，不扩展无关穷举组合。

仅在修改发布链路或用户要求打包时执行相应 Publish / 产物检查，保留版本、资源、摘要、ZIP 与 EXE 自检。检查通过后停止重复验证，报告已执行范围与尚待用户确认的实机条件。

如果测试失败：

**禁止提交。**

先修复问题，再重新测试。

---

# 12. Git 自动提交规则

完成一个明确功能，并且本地测试通过后：

**自动创建 Git Commit。**

不需要等待用户再次要求提交。

每个 Commit 尽量对应一个完整、独立的功能修改。

例如：

```text
feat: 添加ADB设备检测

feat: 添加模拟器截图功能

feat: 添加模板匹配

feat: 添加模板采集工具

fix: 修复截图超时问题

ui: 优化主界面布局
```

提交前必须确认：

```text
git status
```

不要提交：

- 密钥
- Token
- 本机路径
- 用户私人配置
- 编译缓存
- 临时截图
- 调试文件

除非用户明确要求：

**自动 Commit，但不要自动 Push。**

---

# 13. Codex 工作流程

每次接到任务：

1. 阅读 `AGENTS.md`
2. 检查当前仓库状态
3. 阅读相关现有代码
4. 实现需求
5. 不修改无关代码
6. 编译
7. 测试
8. 本地验证
9. 修复发现的问题
10. 仅在修正后重跑受影响的检查
11. Git Commit
12. 汇报结果

完成后汇报：

- 实现了什么
- 修改了哪些主要文件
- Build 是否通过
- Tests 是否通过
- EXE 是否验证
- Git Commit Hash
- 当前仍存在什么限制

---

# 14. 当前第一阶段目标

GGEA v0.1 需要完成：

- WPF 主界面
- 简洁现代 UI
- ADB 设备检测
- 模拟器选择
- 模拟器截图
- 截图显示
- Tap
- Swipe
- 图像模板匹配
- ROI
- Confidence
- WaitForImage
- ClickImage
- 日志
- 模板采集基础工具
- EXE Release 打包
- 基础单元测试

真实每日任务流程在获得游戏截图和模板后再实现。

不要虚构游戏页面、坐标和任务流程。

---

# 15. 核心原则

GGEA 当前阶段遵循：

> 能用 C# 简单解决，就不要引入 C++。

> 能用“识图 → 点击 → 验证”解决，就不要建立复杂自动化架构。

优先级：

**稳定 > 易用 > 易维护 > 性能 > 架构复杂度。**
