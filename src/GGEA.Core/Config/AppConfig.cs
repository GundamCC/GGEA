using GGEA.App.Automation;
using System.Text.Json.Serialization;

namespace GGEA.App.Config;

/// <summary>
/// GGEA 运行配置。
/// 对应 config/ggea.config.json，未配置的字段使用默认值。
/// </summary>
public sealed class AppConfig
{
    /// <summary>保留未知字段供独立扩展迁移，宿主不解释或执行其中的业务状态。</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? ExtensionData { get; set; }

    /// <summary>助手界面语言；空值表示尚未保存，首次启动采用 Windows 界面语言。</summary>
    public string? UiLanguage { get; set; }

    /// <summary>用户主动下载软件更新后，校验成功即自动重启安装；旧配置缺失时默认开启。</summary>
    public bool AutoInstallUpdates { get; set; } = true;

    /// <summary>已加入每日的强敌任务；为空表示未添加或已经删除。</summary>
    public FierceEnemyDailySettings? FierceEnemyDaily { get; set; }

    /// <summary>保存上次使用的主题；旧配置未指定时仍默认夜间模式。</summary>
    public bool IsDarkMode { get; set; } = true;

    /// <summary>自动推图允许调整的部队及出击提示策略，与自动刷本勾选项互相独立。</summary>
    public AutoProgressSettings AutoProgress { get; set; } = new();

    /// <summary>ADB 可执行文件路径，可直接写 "adb"（依赖 PATH）或完整路径。</summary>
    [JsonPropertyName("adbPath")]
    public string AdbPath { get; set; } = "adb";

    /// <summary>检索与手动选择过的 ADB 路径；随配置保存，重启后仍可切换。</summary>
    public List<string> AdbPaths { get; set; } = new();

    /// <summary>首次检索完成标记；随包分发的默认配置不得设为 true。</summary>
    public bool AdbDiscoveryCompleted { get; set; }

    /// <summary>普通 ADB 命令超时时间（毫秒）。</summary>
    [JsonPropertyName("commandTimeoutMs")]
    public int CommandTimeoutMs { get; set; } = 15000;

    /// <summary>截图命令超时时间（毫秒），截图通常比普通命令慢。</summary>
    [JsonPropertyName("screenshotTimeoutMs")]
    public int ScreenshotTimeoutMs { get; set; } = 30000;

    /// <summary>配置 MuMuManager 时默认使用原生截图，按游戏包选择显示；关闭后显式使用 ADB。</summary>
    [JsonPropertyName("preferMuMuNativeScreenshot")]
    public bool PreferMuMuNativeScreenshot { get; set; } = true;

    /// <summary>日志输出目录（相对程序目录）。</summary>
    [JsonPropertyName("logDirectory")]
    public string LogDirectory { get; set; } = "logs";

    /// <summary>识别模板保存目录（相对程序目录）。</summary>
    [JsonPropertyName("templateDirectory")]
    public string TemplateDirectory { get; set; } = "resources/templates";

    /// <summary>模板匹配默认置信度阈值。</summary>
    [JsonPropertyName("defaultMatchThreshold")]
    public double DefaultMatchThreshold { get; set; } = 0.85;

    /// <summary>模拟器可执行文件路径（如 MuMuManager.exe / dnplayer.exe），为空时一键启动不可用。</summary>
    [JsonPropertyName("emulatorPath")]
    public string EmulatorPath { get; set; } = "";

    /// <summary>模拟器类型：mumu / ld（雷电）/ bs（BlueStacks）/ custom（自定义）；默认 custom，选择 ADB 路径后自动识别。</summary>
    [JsonPropertyName("emulatorType")]
    public string EmulatorType { get; set; } = "custom";

    /// <summary>模拟器启动参数（如 MuMu: control launch --vmindex 0；雷电: launch --index 0）。</summary>
    [JsonPropertyName("emulatorArgs")]
    public string EmulatorArgs { get; set; } = "";

    /// <summary>由区服规范化得到的目标游戏包名；保留持久化字段兼容旧配置，不提供自定义输入。</summary>
    [JsonPropertyName("gamePackage")]
    public string GamePackage { get; set; } = GameProfiles.WorldwidePackage;

    /// <summary>区服标识；空值用于兼容仅保存包名的旧配置。</summary>
    public string GameServer { get; set; } = "";

    /// <summary>游戏实际显示语言，与助手界面语言无关；旧配置加载时按包名补全。</summary>
    public string GameLanguage { get; set; } = "";

    /// <summary>任务结束（含刷本完成、失败及中止）或网络错误时发送 Windows 通知；旧配置缺少时默认开启。</summary>
    [JsonPropertyName("enableWindowsNotifications")]
    public bool EnableWindowsNotifications { get; set; } = true;

    /// <summary>启动模拟器后等待新设备上线的总超时（毫秒）。</summary>
    [JsonPropertyName("deviceWaitTimeoutMs")]
    public int DeviceWaitTimeoutMs { get; set; } = 180000;

    /// <summary>等待系统启动完成（sys.boot_completed=1）的总超时（毫秒）。</summary>
    [JsonPropertyName("bootCompletedTimeoutMs")]
    public int BootCompletedTimeoutMs { get; set; } = 180000;

    /// <summary>tap to start 识别模板路径（相对程序目录）。</summary>
    [JsonPropertyName("tapToStartTemplate")]
    public string TapToStartTemplate { get; set; } = "resources/templates/entry/tap_to_start.png";

    /// <summary>打开游戏后等待健康提示页展示的时长（毫秒）。</summary>
    [JsonPropertyName("splashDelayMs")]
    public int SplashDelayMs { get; set; } = 3000;

    /// <summary>等待 tap to start 出现的总超时（毫秒）。</summary>
    [JsonPropertyName("tapToStartTimeoutMs")]
    public int TapToStartTimeoutMs { get; set; } = 30000;

    /// <summary>全局 TAP TO NEXT 模板路径之一（活动登录奖励页跳过按钮，可能不存在）。</summary>
    [JsonPropertyName("tapToNextTemplate")]
    public string TapToNextTemplate { get; set; } = "resources/templates/entry/tap_to_next.png";

    /// <summary>等待活动登录奖励页（TAP TO NEXT）出现的总超时（毫秒），超时视为无活动页。</summary>
    [JsonPropertyName("loginRewardWaitMs")]
    public int LoginRewardWaitMs { get; set; } = 8000;

    /// <summary>全局 TAP TO NEXT 模板路径之一（登录奖励页，可能不存在）。</summary>
    [JsonPropertyName("tapToNextLoginRewardTemplate")]
    public string TapToNextLoginRewardTemplate { get; set; } = "resources/templates/entry/tap_to_next_login_reward.png";

    /// <summary>登录奖励演出中右上角“略过”按钮模板（相对程序目录）。</summary>
    [JsonPropertyName("loginRewardSkipTemplate")]
    public string LoginRewardSkipTemplate { get; set; } = "resources/templates/common/skip.png";

    /// <summary>登录奖励目标未出现多久后点击画面中央并开始识别“略过”（毫秒）。</summary>
    [JsonPropertyName("loginRewardFallbackDelayMs")]
    public int LoginRewardFallbackDelayMs { get; set; } = 5000;

    /// <summary>登录奖励结束后可能出现的公告标题模板（相对程序目录）。</summary>
    [JsonPropertyName("loginRewardAnnouncementTemplate")]
    public string LoginRewardAnnouncementTemplate { get; set; } = "resources/templates/home/announcement.png";

    /// <summary>公告弹窗底部“关闭”按钮模板（相对程序目录）。</summary>
    [JsonPropertyName("loginRewardAnnouncementCloseTemplate")]
    public string LoginRewardAnnouncementCloseTemplate { get; set; } = "resources/templates/common/close.png";

    /// <summary>登录奖励点击后静默等待公告，以及命中公告后等待关闭按钮的单阶段上限（毫秒）。</summary>
    [JsonPropertyName("loginRewardAnnouncementWaitMs")]
    public int LoginRewardAnnouncementWaitMs { get; set; } = 4000;

    /// <summary>进入登录奖励“略过”恢复阶段后的等待上限（毫秒）。</summary>
    [JsonPropertyName("loginRewardPageWaitMs")]
    public int LoginRewardPageWaitMs { get; set; } = 8000;

    /// <summary>人机验证“我们需要确认您是人类”标题模板（相对程序目录）。</summary>
    [JsonPropertyName("humanVerificationTemplate")]
    public string HumanVerificationTemplate { get; set; } = "resources/templates/entry/human_verification.png";

    /// <summary>任务失败后用于确认网络错误弹窗标题的模板（相对程序目录）。</summary>
    [JsonPropertyName("connectionErrorTemplate")]
    public string ConnectionErrorTemplate { get; set; } = "resources/templates/entry/connection_error.png";

    /// <summary>“确认解除数据同步”弹窗标题模板（相对程序目录）。</summary>
    [JsonPropertyName("dataSyncConfirmTemplate")]
    public string DataSyncConfirmTemplate { get; set; } = "resources/templates/entry/confirm_disable_data_sync.png";

    /// <summary>“下载”确认弹窗标题模板（相对程序目录）。</summary>
    [JsonPropertyName("downloadPromptTemplate")]
    public string DownloadPromptTemplate { get; set; } = "resources/templates/entry/download_prompt.png";

    /// <summary>左下角已激活主界面按钮模板。</summary>
    [JsonPropertyName("mainScreenActivatedTemplate")]
    public string MainScreenActivatedTemplate { get; set; } = "resources/templates/home/home_screen_button_activated.png";

    /// <summary>左下角未选中主界面按钮模板。</summary>
    [JsonPropertyName("mainScreenInactiveTemplate")]
    public string MainScreenInactiveTemplate { get; set; } = "resources/templates/home/home_screen_button.png";

    /// <summary>左上角返回按钮模板；两种主界面按钮均未命中时用于安全回退。</summary>
    [JsonPropertyName("mainScreenBackTemplate")]
    public string MainScreenBackTemplate { get; set; } = "resources/templates/common/back.png";

    /// <summary>主界面按钮的固定搜索区域，基准分辨率为 1920×1080。</summary>
    [JsonPropertyName("mainScreenButtonRoi")]
    public ScreenRoiConfig MainScreenButtonRoi { get; set; } = new() { X = 0, Y = 975, Width = 300, Height = 105 };

    /// <summary>返回按钮的固定搜索区域，基准分辨率为 1920×1080。</summary>
    [JsonPropertyName("mainScreenBackRoi")]
    public ScreenRoiConfig MainScreenBackRoi { get; set; } = new() { X = 0, Y = 0, Width = 206, Height = 91 };

    /// <summary>主界面专用匹配阈值，不受全局业务模板阈值影响。</summary>
    [JsonPropertyName("mainScreenThreshold")]
    public double MainScreenThreshold { get; set; } = 0.85;

    /// <summary>确认主界面所需的连续命中帧数。</summary>
    [JsonPropertyName("mainScreenConfirmationFrames")]
    public int MainScreenConfirmationFrames { get; set; } = 2;

    /// <summary>连续主界面确认帧之间的间隔（毫秒）。</summary>
    [JsonPropertyName("mainScreenConfirmationIntervalMs")]
    public int MainScreenConfirmationIntervalMs { get; set; } = GameTiming.ConfirmationIntervalMs;

    /// <summary>点击返回按钮后等待画面稳定的时长（毫秒）。</summary>
    [JsonPropertyName("mainScreenBackSettleDelayMs")]
    public int MainScreenBackSettleDelayMs { get; set; } = GameTiming.ConfirmationIntervalMs;

    /// <summary>等待主界面出现的总超时（毫秒）。</summary>
    [JsonPropertyName("mainScreenWaitMs")]
    public int MainScreenWaitMs { get; set; } = 90000;

    /// <summary>主界面布局文件（相对程序目录）。</summary>
    [JsonPropertyName("mainUiLayout")]
    public string MainUiLayout { get; set; } = "resources/ui_layout/main.json";

    /// <summary>个人基地布局文件（相对程序目录）。</summary>
    [JsonPropertyName("personalBaseLayout")]
    public string PersonalBaseLayout { get; set; } = "resources/ui_layout/personal_base.json";

    /// <summary>商店布局文件（相对程序目录）。</summary>
    [JsonPropertyName("shopLayout")]
    public string ShopLayout { get; set; } = "resources/ui_layout/shop.json";

    /// <summary>全部回收按钮模板（可用状态）。</summary>
    [JsonPropertyName("recycleAllTemplate")]
    public string RecycleAllTemplate { get; set; } = "resources/templates/base/button_recycle_all.png";

    /// <summary>全部回收按钮模板（暗灰不可用状态）。</summary>
    [JsonPropertyName("recycleAllDisabledTemplate")]
    public string RecycleAllDisabledTemplate { get; set; } = "resources/templates/base/button_recycle_all_disabled.png";

    /// <summary>收获结果弹窗 OK 模板。</summary>
    [JsonPropertyName("harvestOkTemplate")]
    public string HarvestOkTemplate { get; set; } = "resources/templates/dialog/button_ok.png";

    /// <summary>战舰巡航按钮模板。</summary>
    [JsonPropertyName("battleshipTemplate")]
    public string BattleshipTemplate { get; set; } = "resources/templates/base/battleship.png";

    /// <summary>可选战舰模板遮罩，为空表示不使用。</summary>
    [JsonPropertyName("battleshipMask")]
    public string BattleshipMask { get; set; } = "";

    /// <summary>商店免费商品购买按钮模板。</summary>
    [JsonPropertyName("shopFreeBuyTemplate")]
    public string ShopFreeBuyTemplate { get; set; } = "resources/templates/shop/button_premium_free_buy.png";

    /// <summary>完整“钻石＋免费”价格栏模板；保留原配置键名以兼容旧设置。</summary>
    [JsonPropertyName("shopFreeTextTemplate")]
    public string ShopFreeTextTemplate { get; set; } = "resources/templates/shop/free.png";

    /// <summary>商店确认购买按钮模板。</summary>
    [JsonPropertyName("shopConfirmBuyTemplate")]
    public string ShopConfirmBuyTemplate { get; set; } = "resources/templates/shop/button_confirm_buy.png";

    /// <summary>商店结果弹窗关闭按钮模板。</summary>
    [JsonPropertyName("shopResultCloseTemplate")]
    public string ShopResultCloseTemplate { get; set; } = "resources/templates/common/close.png";

    /// <summary>钻石图标模板（钻石安全锁）。</summary>
    [JsonPropertyName("diamondIconTemplate")]
    public string DiamondIconTemplate { get; set; } = "resources/templates/safety/diamond_icon.png";

    /// <summary>礼物页布局文件（相对程序目录）。</summary>
    [JsonPropertyName("giftLayout")]
    public string GiftLayout { get; set; } = "resources/ui_layout/gift.json";

    /// <summary>礼物页“全部领取”按钮模板。</summary>
    [JsonPropertyName("giftCollectAllTemplate")]
    public string GiftCollectAllTemplate { get; set; } = "resources/templates/gift/button_collect_all.png";

    /// <summary>礼物页“全部领取”灰色禁用状态模板。</summary>
    [JsonPropertyName("giftCollectAllDisabledTemplate")]
    public string GiftCollectAllDisabledTemplate { get; set; } = "resources/templates/gift/button_collect_all_disabled.png";

    /// <summary>礼物领取结果弹窗 OK 模板。</summary>
    [JsonPropertyName("giftOkTemplate")]
    public string GiftOkTemplate { get; set; } = "resources/templates/dialog/button_ok.png";

    /// <summary>礼物页关闭按钮模板。</summary>
    [JsonPropertyName("giftCloseTemplate")]
    public string GiftCloseTemplate { get; set; } = "resources/templates/common/close.png";

    /// <summary>开发页布局文件（相对程序目录）。</summary>
    [JsonPropertyName("developLayout")]
    public string DevelopLayout { get; set; } = "resources/ui_layout/develop.json";

    /// <summary>开发路线图第一个路线图模板。</summary>
    [JsonPropertyName("roadmapFirstTemplate")]
    public string RoadmapFirstTemplate { get; set; } = "resources/templates/develop/dev_0079.png";

    /// <summary>目标机体原生模板（开发&强化一次），同一来源覆盖大小两档比例。</summary>
    [JsonPropertyName("developMsTemplate")]
    public string DevelopMsTemplate { get; set; } = "resources/templates/develop/dev_unit/ogk_g0070u00100.png";

    /// <summary>目标机体大图模板（与小图共同覆盖不同缩放比例）；为空表示不启用。</summary>
    [JsonPropertyName("developMsLargeTemplate")]
    public string DevelopMsLargeTemplate { get; set; } = "";

    /// <summary>原生素材按大小两档默认比例匹配；旧自定义截图配置迁移时保持关闭。</summary>
    [JsonPropertyName("developMsFixedScales")]
    public bool DevelopMsFixedScales { get; set; } = true;

    /// <summary>目标机体匹配置信度阈值（过低会误选其他机体）。</summary>
    [JsonPropertyName("developMsThreshold")]
    public double DevelopMsThreshold { get; set; } = 0.86;

    /// <summary>开发页“全部开发”按钮模板。</summary>
    [JsonPropertyName("developAllTemplate")]
    public string DevelopAllTemplate { get; set; } = "resources/templates/develop/button/button_bulk_development.png";

    /// <summary>开发页“全部开发”按钮灰色禁用状态模板；为空表示不检测禁用状态。</summary>
    [JsonPropertyName("developAllDisabledTemplate")]
    public string DevelopAllDisabledTemplate { get; set; } = "";

    /// <summary>开发确认弹窗“执行开发”按钮模板。</summary>
    [JsonPropertyName("developExecuteTemplate")]
    public string DevelopExecuteTemplate { get; set; } = "resources/templates/develop/button/button_execute_develop.png";

    /// <summary>主要关卡作品模板目录（相对程序目录）。</summary>
    [JsonPropertyName("stageTemplateDirectory")]
    public string StageTemplateDirectory { get; set; } = "resources/templates/stage/main_stage";

    /// <summary>出击准备页右下角“出击”按钮模板（自动刷取第一步识别目标）。</summary>
    [JsonPropertyName("sortieTemplate")]
    public string SortieTemplate { get; set; } = "resources/templates/stage/sortie/sortie.png";

    /// <summary>出击后进入关卡时的 TAP TO NEXT 提示模板（自动刷取流程识别目标）。</summary>
    [JsonPropertyName("sortieTapToNextTemplate")]
    public string SortieTapToNextTemplate { get; set; } = "resources/templates/stage/sortie/tap_to_next.png";

    /// <summary>出击后出现的地形适性提示模板；命中后按自动刷本设置选择出击或取消。</summary>
    [JsonPropertyName("sortieTerrainTemplate")]
    public string SortieTerrainTemplate { get; set; } = "resources/templates/stage/sortie/terrain.png";

    /// <summary>出击后出现的下载提示模板；命中后才识别下载或取消按钮。</summary>
    [JsonPropertyName("sortieDownloadTemplate")]
    public string SortieDownloadTemplate { get; set; } = "resources/templates/stage/sortie/download.png";

    /// <summary>战斗界面二倍速图标：开启状态模板。</summary>
    [JsonPropertyName("speedOnTemplate")]
    public string SpeedOnTemplate { get; set; } = "resources/templates/stage/sortie/double_speed_on.png";

    /// <summary>战斗界面二倍速图标：关闭状态模板。</summary>
    [JsonPropertyName("speedOffTemplate")]
    public string SpeedOffTemplate { get; set; } = "resources/templates/stage/sortie/double_speed_close.png";

    /// <summary>战斗结束后的“继续”按钮模板（自动刷取流程识别目标）。</summary>
    [JsonPropertyName("continueTemplate")]
    public string ContinueTemplate { get; set; } = "resources/templates/stage/sortie/continue.png";

    /// <summary>战斗结果页“再次出击”按钮模板（识别到即代表完成一次刷取任务）。</summary>
    [JsonPropertyName("againTemplate")]
    public string AgainTemplate { get; set; } = "resources/templates/stage/sortie/again.png";

    /// <summary>
    /// 战斗结算完成页右下角“选择关卡”文字模板。
    /// 它必须与“再次出击”同时命中，才可确认已经离开继续页，防止将其误判为“继续”。
    /// </summary>
    [JsonPropertyName("selectStageTemplate")]
    public string SelectStageTemplate { get; set; } = "resources/templates/stage/sortie/select_stage.png";

    /// <summary>“再次出击”按钮关闭状态模板（AP 不足时按钮变灰，识别到即中止刷本）。</summary>
    [JsonPropertyName("againCloseTemplate")]
    public string AgainCloseTemplate { get; set; } = "resources/templates/stage/sortie/again_disabled.png";

    /// <summary>刷本失败提示模板（战斗失败画面，识别到后回到选择关卡）。</summary>
    [JsonPropertyName("failedTemplate")]
    public string FailedTemplate { get; set; } = "resources/templates/stage/sortie/failed.png";

    /// <summary>下载界面“下载”按钮模板（仅在下载提示已命中后识别）。</summary>
    [JsonPropertyName("downloadTemplate")]
    public string DownloadTemplate { get; set; } = "resources/templates/dialog/download.png";

    /// <summary>下载界面“取消”按钮模板。</summary>
    [JsonPropertyName("downloadCancelTemplate")]
    public string DownloadCancelTemplate { get; set; } = "resources/templates/dialog/cancel.png";

    /// <summary>默认允许 GGEA 自动下载游戏数据；保留已保存的关闭选择，关闭时点击取消并提醒用户。</summary>
    [JsonPropertyName("allowGgeaDownload")]
    public bool AllowGgeaDownload { get; set; } = true;

    /// <summary>空位出击提示模板（出击阵容存在空位时弹出的提示，用于验证）。</summary>
    [JsonPropertyName("emptySlotHintTemplate")]
    public string EmptySlotHintTemplate { get; set; } = "resources/templates/dialog/empty_slot_hint.png";

    /// <summary>AP 不足提示模板（点击出击后 AP 不足时弹出的提示）。</summary>
    [JsonPropertyName("noApTemplate")]
    public string NoApTemplate { get; set; } = "resources/templates/stage/sortie/no_ap.png";

    /// <summary>玩家升级提示模板（战斗结算中玩家升级时弹出，点击画面后继续）。</summary>
    [JsonPropertyName("playerLevelUpTemplate")]
    public string PlayerLevelUpTemplate { get; set; } = "resources/templates/common/player_level_up.png";

    /// <summary>奖励图标模板（第二个继续按钮识别阶段同步检测）。</summary>
    [JsonPropertyName("rewardTemplate")]
    public string RewardTemplate { get; set; } = "resources/templates/common/reward.png";

    /// <summary>掉落机体奖励的 TAP TO NEXT 提示模板（与再次出击按钮识别并行）。</summary>
    [JsonPropertyName("tapToNextRewardTemplate")]
    public string TapToNextRewardTemplate { get; set; } = "resources/templates/stage/sortie/tap_to_next_reward.png";

    /// <summary>空位出击对话框“出击”确认按钮模板。</summary>
    [JsonPropertyName("emptySlotSortieTemplate")]
    public string EmptySlotSortieTemplate { get; set; } = "resources/templates/dialog/sortie.png";

    /// <summary>空位出击对话框“取消”按钮模板。</summary>
    [JsonPropertyName("emptySlotCancelTemplate")]
    public string EmptySlotCancelTemplate { get; set; } = "resources/templates/dialog/cancel.png";

    /// <summary>仅兼容旧配置回读；HARD STAGE 标题由 OCR 识别，默认不再指向已移除的模板目录。</summary>
    [JsonPropertyName("stageTitleTemplateDirectory")]
    public string StageTitleTemplateDirectory { get; set; } = string.Empty;

    /// <summary>具体关卡节点类型标签与状态的透明模板目录；编号由离线 OCR 读取。</summary>
    [JsonPropertyName("stageNodeTemplateDirectory")]
    public string StageNodeTemplateDirectory { get; set; } = "resources/templates/stage/node_states";

    /// <summary>作品详情页右下角可用“选择”按钮模板。</summary>
    [JsonPropertyName("stageWorkSelectTemplate")]
    public string StageWorkSelectTemplate { get; set; } = "resources/templates/stage/select.png";

    /// <summary>作品详情页右下角暗态“选择”按钮模板；命中表示该作品尚未解锁。</summary>
    [JsonPropertyName("stageWorkSelectCloseTemplate")]
    public string StageWorkSelectCloseTemplate { get; set; } = "resources/templates/stage/select_disabled.png";

    /// <summary>略过结果页 SKIP RESULT 标题模板（相对程序目录）。</summary>
    [JsonPropertyName("skipResultTemplate")]
    public string SkipResultTemplate { get; set; } = "resources/templates/stage/skip/skip_result.png";

    /// <summary>略过次数用尽后的“次数上限”暗态模板（相对程序目录）。</summary>
    [JsonPropertyName("skipCountLimitTemplate")]
    public string SkipCountLimitTemplate { get; set; } = "resources/templates/stage/skip/count_limit.png";

    /// <summary>AP 不足时“回复AP”弹窗的关闭按钮模板（相对程序目录）。</summary>
    [JsonPropertyName("apRecoveryCloseTemplate")]
    public string ApRecoveryCloseTemplate { get; set; } = "resources/templates/common/close.png";

    /// <summary>关卡页布局文件（相对程序目录）。</summary>
    [JsonPropertyName("stageLayout")]
    public string StageLayout { get; set; } = "resources/ui_layout/stage.json";

    /// <summary>强化培育关卡页布局文件（相对程序目录）。</summary>
    [JsonPropertyName("upgradeStagesLayout")]
    public string UpgradeStagesLayout { get; set; } = "resources/ui_layout/upgrade_stages.json";

    /// <summary>使命页布局文件（相对程序目录）。</summary>
    [JsonPropertyName("missionLayout")]
    public string MissionLayout { get; set; } = "resources/ui_layout/mission.json";

    /// <summary>使命页“全部领取”按钮模板（相对程序目录）。</summary>
    [JsonPropertyName("missionClaimAllTemplate")]
    public string MissionClaimAllTemplate { get; set; } = "resources/templates/mission/button_claim_all.png";

    /// <summary>使命页“全部领取”暗态模板（已领取后变灰，相对程序目录）。</summary>
    [JsonPropertyName("missionClaimAllDisabledTemplate")]
    public string MissionClaimAllDisabledTemplate { get; set; } = "resources/templates/mission/button_claim_all_disabled.png";

    /// <summary>使命领取结果弹窗 OK 模板（相对程序目录）。</summary>
    [JsonPropertyName("missionClaimResultOkTemplate")]
    public string MissionClaimResultOkTemplate { get; set; } = "resources/templates/dialog/button_ok.png";

    /// <summary>Hard 关卡数据文件（作品 ID、关卡数量、截图，相对程序目录）。</summary>
    [JsonPropertyName("hardStagesFile")]
    public string HardStagesFile { get; set; } = "resources/hard_stages.json";

    /// <summary>作品 → 画布坐标映射文件（相对程序目录）。</summary>
    [JsonPropertyName("stageWorkPositionsFile")]
    public string StageWorkPositionsFile { get; set; } = "resources/stage_work_positions.json";

    /// <summary>AP 消耗命名方案文件（相对程序目录）。</summary>
    [JsonPropertyName("apConsumptionProfilesFile")]
    public string ApConsumptionProfilesFile { get; set; } = "config/ap_consumption_profiles.json";

    /// <summary>一次标准横向滑动对应的预计画布距离，需通过真机校准。</summary>
    [JsonPropertyName("stageSwipeCanvasDelta")]
    public int StageSwipeCanvasDelta { get; set; } = 1920;

    /// <summary>手指向左半屏滑动的点击 X 补偿；正数向右、负数向左，0 不补偿。</summary>
    [JsonPropertyName("stageHalfSwipeLeftTapCorrection")]
    public int StageHalfSwipeLeftTapCorrection { get; set; } = 200;

    /// <summary>手指向右半屏滑动的点击 X 补偿；正数向右、负数向左，0 不补偿。</summary>
    [JsonPropertyName("stageHalfSwipeRightTapCorrection")]
    public int StageHalfSwipeRightTapCorrection { get; set; } = -200;

    /// <summary>主要关卡滑动后等待画布停止的时间（毫秒）。</summary>
    [JsonPropertyName("stageSwipeSettleMs")]
    public int StageSwipeSettleMs { get; set; } = 3000;

    /// <summary>最后一次作品定位滑动完成后、点击作品节点前的稳定等待时间（毫秒）。</summary>
    [JsonPropertyName("stageWorkTapSettleMs")]
    public int StageWorkTapSettleMs { get; set; } = 3000;

    /// <summary>主要关卡页面切换等待时间（毫秒）。</summary>
    [JsonPropertyName("stagePageSettleMs")]
    public int StagePageSettleMs { get; set; } = 2500;

    /// <summary>查找初代高达左端锚点时允许执行的最大向左滑动次数。</summary>
    [JsonPropertyName("stageResetSearchMaxSwipes")]
    public int StageResetSearchMaxSwipes { get; set; } = 8;

    /// <summary>初代高达左端锚点的模板匹配阈值。</summary>
    [JsonPropertyName("stageResetAnchorThreshold")]
    public double StageResetAnchorThreshold { get; set; } = 0.85;

    /// <summary>第二层确认弹窗“执行”按钮模板。</summary>
    [JsonPropertyName("developExecuteConfirmTemplate")]
    public string DevelopExecuteConfirmTemplate { get; set; } = "resources/templates/develop/button/button_execute.png";

    /// <summary>机体立绘展示底部 TAP TO NEXT 模板（开发流程显式识别，同时参与全局兜底检测）。</summary>
    [JsonPropertyName("developShowcaseTapTemplate")]
    public string DevelopShowcaseTapTemplate { get; set; } = "resources/templates/develop/tap_to_next_showcase.png";

    /// <summary>开发路线图左上角返回按钮模板。</summary>
    [JsonPropertyName("developBackTemplate")]
    public string DevelopBackTemplate { get; set; } = "resources/templates/develop/button_back.png";

    /// <summary>开发页左上角页面标题模板；为空表示不识别标题。</summary>
    [JsonPropertyName("developTitleTemplate")]
    public string DevelopTitleTemplate { get; set; } = "";

    /// <summary>开发页标题匹配置信度阈值。</summary>
    [JsonPropertyName("developTitleThreshold")]
    public double DevelopTitleThreshold { get; set; } = 0.85;

    /// <summary>进入开发页后等待标题出现的总超时（毫秒）。</summary>
    [JsonPropertyName("developTitleWaitMs")]
    public int DevelopTitleWaitMs { get; set; } = GameTiming.ShortRecognitionTimeoutMs;

    /// <summary>首次进入具体作品路线图时“开发路线图显示设置”标题模板。</summary>
    [JsonPropertyName("developRoadmapDisplaySettingsTitleTemplate")]
    public string DevelopRoadmapDisplaySettingsTitleTemplate { get; set; } =
        "resources/templates/develop/title_roadmap_display_settings.png";
}

/// <summary>
/// 固定屏幕搜索区域配置。
/// </summary>
public sealed class ScreenRoiConfig
{
    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }
}
