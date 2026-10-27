# 全局画面处理规范与检查清单

本清单基于当前代码、配置和已有模板检查；不代表所有游戏弹窗均已覆盖，也不把尚未接入的候选描述为已实现。

## 已落地的统一机制

`Automation/GlobalScreenHandler.cs` 是现有全局规则的统一所有者：网络错误安全拦截、失败后网络诊断、普通 TAP TO NEXT 与右上角略过的识别阈值、动作与按设备冷却均集中在此。`AutomationService.GlobalScreens` 持有会话实例；主界面检测、每日任务和出击流程共享该实例。没有后台抢占截图或点击的线程。

处理顺序固定为：

1. 响应取消，捕获并解码有效截图；普通截图最多三次尝试，购买最终确认禁止重试。
2. 在同帧检查网络错误，命中抛出 `ConnectionErrorException`，不能降级为目标未命中。
3. 执行调用方的任务终止检查，例如战斗失败、AP 不足。
4. 仅在等待达到原有延迟（默认十秒）或调用方显式请求恢复时，允许普通继续遮罩和右上角略过动作；同帧先处理 TAP TO NEXT，再处理略过。
5. 没有全局动作或遮罩阻塞时，才匹配业务目标。

全局继续结果区分 `None`（无遮罩）、`Blocked`（有遮罩但冷却中）、`ActionSent`（TAP TO NEXT 已发送）和 `SkipActionSent`（右上角略过已发送）。后三种结果均禁止继续使用旧帧进行业务点击。点击后等待一秒，并在等待入口重新截图、安全检查、任务终止检查和遮罩消失确认；发送动作不是任务完成证据。`HandleGlobalContinuationAsync` 向导航恢复返回上述四态；兼容入口 `HandleTapToNextAsync` 的布尔值仅表示发送成功，调用方必须重新观察。略过后即使控件消失，也不能将 `WaitForImageOrTapToNextAsync` 判为完成；必须继续等待真实目标或 TAP TO NEXT。全局略过仅点击已识别按钮，不继承启动流程中点击中央唤出控件的动作。已有等待总超时与取消机制继续适用。

`ScreenshotAsync`、`FindImageAsync`、主界面检测均保持只读，不自动关闭继续遮罩。对调用方提供的 PNG 执行 `MatchImageAsync` 等纯匹配也不自行触发全局动作；业务应从受保护的截图入口取得当前帧。设备执行权仍由现有 `DeviceExecutionGate` 管理。

## 已有内容与处理归属

| 内容 | 当前依据与位置 | 处理范围和规则 | 本次状态 |
| --- | --- | --- | --- |
| 网络中断/连接错误 | `ConnectionErrorDetector`、`entry/connection_error.png` | 所有受保护截图优先中止；不自动重试网络按钮、不自行重启游戏 | 已统一到全局入口，保留 0.90 阈值与顶部 ROI |
| 普通 TAP TO NEXT | `entry/tap_to_next.png`、`entry/tap_to_next_login_reward.png`、`develop/tap_to_next_showcase.png` | 等待兜底或显式恢复；匹配中心点击、冷却、废弃旧帧 | 已统一动作与 0.80 阈值；登录专用识别复用阈值 |
| 右上角略过 | `SkipButtonRecognizer`、配置 `LoginRewardSkipTemplate`（默认 `common/skip.png`） | 右上角 280×140、0.85 阈值；沿用全局等待延迟、冷却和安全检查 | 已接入全局等待与恢复；启动专属恢复共用识别器，仍保留原时序 |
| 截图失败/损坏/设备断连 | `ScreenshotCapture`、`ScreenshotCaptureException` | 全任务基础设施失败；有界重试后中止，不能算作页面消失 | 保留统一截图入口及原错误语义 |
| 人机验证 | `MainWindow.IsHumanVerificationVisibleAsync`、配置 `HumanVerificationTemplate` | 用户已确认只在启动阶段出现；禁止自动解题或继续点击 | 启动阶段专属，不加入全局规则 |
| 确认解除数据同步 | `MainWindow.IsDataSyncConfirmVisibleAsync`、`entry/confirm_disable_data_sync.png` | 用户已确认只在启动阶段出现；交由用户处理 | 启动阶段专属，不加入全局规则 |
| 启动下载确认 | `MainWindow.IsDownloadPromptVisibleAsync`、`entry/download_prompt.png` | 启动阶段中止提示；不能和出击数据下载直接合并 | 保留启动专属处理 |
| 出击数据下载 | `DownloadDialogService`、`SortieFlowService` | 先同帧确认下载说明，再按允许下载设置识别下载/取消按钮；重新观察后续页 | 已有跨流程服务，保留场景授权；不注册为无条件全局动作 |
| 玩家升级 | `StageSkipService` 的 PLAYER RANK UP；`SortieFlowService` 的 player_level_up | 属于跨任务弹窗候选，但两套模板、ROI 和动作不同：略过需等动画后确认继续，战斗结算点击升级提示 | 保留两种策略，未强行共用点击或放宽阈值 |
| 公告 | `GameEntryService`、主界面恢复回调；`home/announcement.png` | 登录/回主界面范围内关闭；必须识别公告身份，不能仅看到通用 OK 就点击 | 保留现有复用入口；不在购买/战斗任意阶段关闭 |
| AP 不足/回复 AP | `StageSkipService`、`SortieFlowService`、`ApConsumptionService` | 跨任务终止状态；取消/关闭后返回明确 AP 不足结果，不自动补充或购买 | 保留业务归属，不作为通用继续 |
| 战斗失败/再次出击禁用 | `SortieFlowService.HandleTerminalStateAsync` | 战斗流程最高优先级终止；先于奖励、继续与再次出击 | 保留已有同帧终止保护 |
| 钻石风险/购买确认 | `DiamondSafetyChecker`、`ShopService`、`ShopConfirmationRecognizer` | 资源消耗入口的共同安全原则；风险命中否决，未命中不等于免费，购买仍需独立免费证据 | 保留购买专属授权，不全屏扫描后擅自接受通用确认 |
| 地形不适/队伍空位 | `SortieFlowService` | 出击场景内按两个独立用户选项决定出击或取消 | 保留任务专属处理 |
| 战斗开场/掉落奖励 TAP TO NEXT | `SortieFlowService`，`stage/sortie` 下两张模板 | 是流程状态信号；分别保留 0.75/0.80 阈值及失败/AP 优先级 | 不加入普通全局模板池，避免越过结算状态 |
| 返回、主界面、奖励 OK、关卡略过/扫荡、再出击、出售/开发/强化确认 | 各任务及 `MainScreenNavigator` | 同名按钮不能代表统一语义；交由拥有业务状态的流程处理 | 明确禁止仅凭通用按钮注册全局点击 |
| 招募确认与招募结果 TAP TO NEXT | `ScoutService`，`dialog/execute_hint.png`、`dialog/execute.png` 与 `scout/tap_to_next.png` | 属于资源消耗流程：提示与执行按钮必须同帧命中；执行后等待 6 秒才识别底部继续提示 | 不注册为全局动作；沿用只读截图的网络错误拦截，继续后确认返回招募页才计数 |

维护、强制更新、异地登录、会话过期等尚未找到对应的现成识别规则，不能声称已支持。需要真实截图、模板和预期处理后才能新增。

## 后续候选的接入要求

人机验证和解除数据同步确认按用户确认保留为启动阶段专属，不再列为全局候选。后续仅考虑玩家升级与公告的场景化复用，补充各自真实画面的正反例后再扩大作用域。右上角演出略过已全局化，关卡略过/扫荡仍属于资源消耗业务，二者不可混用。

新增规则必须说明：页面身份模板与 ROI、适用场景、优先级、是否允许动作、动作后确认条件、总等待上限、取消行为、缺失模板的失败语义及对应测试。识别共用不代表任务结果共用，不能用“关闭了弹窗”替代“业务成功”。

不得让全局处理调用会再次触发自身的等待原语；复用当前帧识别，一轮最多发送一个动作。全局处理器只执行被授权的安全动作，下载许可、资源消费、战斗结果与任务完成仍归原任务所有。

## 验证范围

回归覆盖网络错误与继续提示同帧的优先级、任务停止回调优先级、只读入口不点击、动作后重拍与消失确认、截图异常和取消，以及既有登录、主界面与出击流程。合成页面序列用于验证控制流，不能替代游戏内所有弹窗的真实截图与实机验证。
