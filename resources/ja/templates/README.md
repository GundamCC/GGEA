# 日服任务识别素材

素材裁自 2026-10-22 的 MuMu 日服 2.7.0、1920×1080 原始 ADB 截图。素材覆盖主界面导航、五项每日任务、消耗 AP、自动刷本、铁球工厂解体、角色招募与本次推图入口；不表示其他日服任务已经完成适配。

- 战舰：巡航入口、回收亮态和暗态、奖励 OK。
- 商店：免费价格栏、购买按钮、完整零钻石声明、商品名称、所需数量零及确认标题；共用 `resources/common/ui_layout/shop.json`。
- 奖励：邮件及使命的全部领取亮态和暗态、关闭按钮。获得机体展示沿用公共 TAP TO NEXT OCR。
- 开发：首个路线图、首次显示设置标题、路线图标题、全部开发、两层执行及取消。单位立绘继续复用通用透明素材。
- 强化培育：页面标题、一次略过亮态和暗态及执行按钮。SKIP RESULT 复用通用英文素材。

普通按钮沿用中文基准尺寸。日文路线图、关卡菜单和强化培育标题按完整文本宽度裁剪，均位于原有共用搜索区域内。商店零价声明与商品名分别扩大为 600×78、420×41，并同步重裁通用/简体中文素材，保持各语言使用同一固定证据区域。

商店风险区域覆盖所需金额及余额行，底边为 y=690；不包含日服 y≈780 的固定红字付款顺序说明。真实免费列表与确认截图保存在测试的 `Fixtures/Shop/ja_free_*.png`；回归测试仍拒绝付费价格、被改动的零价证据和红色金额。

本地原始截图与裁剪坐标记录在忽略目录 `research/japanese-adaptation/`。人工 ADB 操作已完成五项任务；生产服务使用这些截图回放验证，回放不会操作设备或再次消费资源。

## 消耗 AP 补采

ID 1（机动战士高达）与 ID 2（机动战士 Z 高达）的 HARD 3、2、1 各略过三次，共消耗 180 AP（880 → 700），未使用恢复道具或钻石。新增四张模板：

| 模板 | 原始截图 | 裁剪区域 x,y,w,h |
| --- | --- | --- |
| `stage/main_stage.png` | `50-ap-main.png` | 216,18,470,68 |
| `stage/select.png` | `53-ap-id1.png` | 1495,831,359,125 |
| `stage/sortie/title_select_stage.png` | `54-ap-id1-select.png` | 216,18,520,65 |
| `stage/skip/count_limit.png` | `58-ap-id1-h3-done.png` | 1590,680,160,60 |

完整日文标题宽于中文，仍位于既有共用标题 ROI 内；选择按钮和次数上限保持中文尺寸，无需调整共用布局。结果标题 SKIP RESULT 与左侧 HARD/数字/COMPLETE 继续复用通用识别。

六关均完成生产略过服务截图回放。部分右上角长标题 OCR 置信度不足，现有左侧节点回退可在稳定画面确认编号和已通过状态；入场 COMPLETE 动画帧不作为已通过证据，不降低阈值。Z 高达 HARD 2 的前后原图保存于 `tests/GGEA.Tests/Fixtures/ApConsumption/`，验证可略过与次数上限的区别、左侧节点确认。AP 不足及玩家升级未在本次实机操作中触发，未补采或声称验证这些分支。

## 自动刷本补采

保持系列部队五个单位槽为空，仅对自由部队执行游戏内自动编成（补入 ν 高达 LV90），随后使用游戏 AUTO 完成机动战士高达 HARD 3 两遍，AP 700 → 680。由 ADB 操作入口及结算并采集原图；不据此声称 GGEA 整个实机流程已经无人干预运行。

| 模板 | 原始截图 | 裁剪区域 x,y,w,h |
| --- | --- | --- |
| `stage/sortie/sortie.png` | `91-farm-formed.png` | 1494,929,366,133 |
| `stage/sortie/button_auto-form.png` | `91-farm-formed.png` | 1148,974,256,73 |
| `stage/sortie/auto-form.png` | `90-farm-autoform.png` | 820,0,280,149 |
| `dialog/empty_slot_hint.png` | `93-farm-empty-warning.png` | 680,333,560,106 |
| `dialog/sortie.png` | `93-farm-empty-warning.png` | 968,790,362,123 |
| `stage/sortie/continue.png` | `95-farm-battle.png` | 1496,929,361,121 |
| `stage/sortie/again.png` | `97-farm-again.png` | 1110,928,361,120 |
| `stage/sortie/select_stage.png` | `97-farm-again.png` | 1523,960,290,52 |

长日文自动编成标题与选择关卡文字保留完整宽度；空位提示共用区域由 400×500 扩为 600×500，其余区域及识别阈值保持不变。空槽加号、AUTO 与速度图标复用共用识别。出击按钮沿用原有带 10 AP 的完整按钮采集方式，本次未扩展到其它 AP 消耗关卡。

`Fixtures/Sortie/ja-*.png` 保留实机原图；结算用第二遍独立画面验证第一遍采集的素材。回归覆盖仅调整自由部队、未授权空位出击时取消、剩余次数决定再战或返回。地形不适、下载、战败、AP 不足、玩家升级与再次出击禁用未在本次实机触发，未补采这些日文分支。

## 铁球工厂原流程补采

补齐以下十三张日文模板后，通过本地验证入口直接调用生产 `BallFactoryRunner`、`BallFactoryService`、`DevelopService`、`MainScreenNavigator`、`DismantleEntryService` 与 `DismantleService`，使用 MuMu 原生截图及 ADB 输入完成真实流程；入口只增加原图和输入日志，不替换识别、缩短等待或修改游戏筛选条件。不是仅手动点击或截图回放。

本次两批各 32 台，共制造 64 台。原流程每轮重置自动解体条件、勾选开发单位；日服重置结果是保留 4 台同种未锁定单位、SR/R/N，且不限定 LV1。实际解体 60 台新铁球及 2 台原有 LV30 开发单位，获得 31,000 CAPITAL、1,240 GP；原有已锁定铁球保留，另有 4 台新铁球保留。最终 CAPITAL 为 1,019,660、GP 为 2,540，单位总数 914。第二轮明确识别无推荐单位后返回主界面；按用户停留页面要求再进入强化页。不能把计划 1,280 GP 当作实际到账。

| 模板 | 原始截图 | 裁剪区域 x,y,w,h |
| --- | --- | --- |
| `upgrade/title_upgrade.png` | `144-ball-redo-start.png` | 30,14,150,69 |
| `upgrade/button_dismantle_gray.png` | `144-ball-redo-start.png` | 811,862,254,74 |
| `upgrade/title_dismantle.png` | `145-redo-dismantle.png` | 210,13,148,69 |
| `upgrade/button_auto_dismantle.png` | `145-redo-dismantle.png` | 82,849,253,69 |
| `upgrade/auto_dismantle.png` | `148-redo-auto.png` | 833,70,254,48 |
| `upgrade/button_reset.png` | `148-redo-auto.png` | 1555,823,253,68 |
| `upgrade/button_dismantle.png` | `134-ball-selected64.png` | 1498,816,357,118 |
| `upgrade/dismantle_confirm.png` | `136-ball-dismantle-confirm.png` | 872,70,177,48 |
| `upgrade/button_execute_dismantle.png` | `136-ball-dismantle-confirm.png` | 970,938,357,112 |
| `dialog/no_recommended_units.png` | `141-ball-no-recommended.png` | 704,500,508,50 |
| `upgrade/sort.png` | `146-redo-sort.png` | 870,70,179,48 |
| `upgrade/option/option_dev_unit_off.png` | `150-redo-option-off.png` | 110,548,264,76 |
| `upgrade/option/option_dev_unit_on.png` | `151-redo-option-on.png` | 110,548,264,76 |

复选框模板保留左侧颜色检测区域及标签的主要文字，尺寸适配既有区域；未改变共用 ROI 或阈值。确定、OK 与筛选开关图标复用现有语言/通用资源。解体确认和结果的日文标题不同，原有严格确认阈值及结果 OK 居中校验继续生效。

`Fixtures/Develop/ja-*.png` 来自本次产品服务实际执行的独立原帧；回归覆盖强化入口、零轮/一轮后无推荐单位、重置与开发勾选顺序、结果 OK 位置，以及没有空提示时不能报告完成。本地全程原图及操作日志保存在忽略目录 `research/japanese-ball-live/`，裁剪来源仍在 `research/japanese-adaptation/`。本次实机未触发筛选 ON 重置分支、单位上限、金币不足或多轮超过 100 台清理；不声明这些日服分支已实机验证。

## 角色招募两人补采

游戏导航与不消费的确认预览用于采集日文素材。关闭预览后，直接调用与窗口相同的生产 `ScoutService.RunAsync`，通过真实 MuMu 截图、ADB 输入、同帧 CAPITAL 提示与执行按钮确认、结果 TAP TO NEXT OCR 以及返回招募标题确认完成两轮。两人的消费和结果操作均由原服务执行。第二轮返回列表后，完成回调触发现有取消令牌，未进入第三轮；这不是“没有可招募角色”的结束状态，也没有给产品添加数量设置功能。

实际招募マッシュ与オルテガ，各 3,000 CAPITAL，总计 6,000；余额 1,019,660 → 1,013,660。最终停在招募列表，下一位为ガイア，未执行第三次招募。

| 模板 | 原始截图 | 裁剪区域 x,y,w,h |
| --- | --- | --- |
| `scout/title_scout.png` | `158-scout-list.png` | 217,17,278,67 |
| `scout/scout.png` | `158-scout-list.png` | 1494,832,364,123 |
| `dialog/execute_hint.png` | `159-scout-preview.png` | 650,333,621,88 |

完整日文标题与两行 CAPITAL 提示位于原有生产 ROI 内；执行按钮复用已验证日文素材，英文 TAP TO NEXT 沿用原 OCR，不重复创建未使用模板。没有可招募角色、金币不足及对应暗态未在本次实机触发，尚未采集这些日文状态；不能据此声称招募全部角色和资源不足分支已适配。

`Fixtures/Scout/ja-*.png` 为本次生产服务执行时的七张独立原帧，覆盖两人的列表、确认、结果和两人完成后的列表。回归确认六次点击后取消，第三轮按钮不得点击；另一实际日文解体确认框不能授权招募。原始全程截图及日志位于本地忽略目录 `research/japanese-ball-live/20261022-174958/` 与 `scout.log`；预采集及坐标记录在 `research/japanese-adaptation/`。

## 高达00推图两关补采

2026-10-22 使用生产 `StageService`、`AutoProgressRunner`、`AutoProgressBattleService`、`AutoFormationService` 和 `SortieFlowService`，从 NORMAL 12 推进至 NORMAL 14；12、13 均 COMPLETE 3/3，14 未出击。AP 680 → 660，未补充 AP、未重复出击已完成关卡。

第一关按默认设置自动编制系列和自由部队。结算故事入口缺少日文素材时暂停采集；接续发现剧情灰色背景会误命中空位提示，修复为同帧确认提示、左侧取消和右侧出击按钮，不改变阈值。重新接续经原恢复机制确认选关，再读取到未通过的 NORMAL 13。第二关因仍有空位提示、且单位和驾驶员均已填满而按原默认编制策略停止；本次验证改用现有“依旧出击”策略继续，未改变产品默认设置。第二关结算确认返回后读到 NORMAL 14，定向验证立即停止。

| 模板 | 本地原始截图（`research/japanese-ball-live/` 下） | 裁剪区域 x,y,w,h |
| --- | --- | --- |
| `stage/sortie/button_sortie_preparations.png` | `20261022-182334/0021.png` | 1498,839,354,110 |
| `stage/sortie/auto_all.png` | `20261022-182822/0053.png` | 1424,13,172,79 |
| `stage/sortie/story.png` | `20261022-182822/0084.png` | 1489,930,354,114 |

MENU、SKIP、二倍速、继续、选择关卡和 TAP TO NEXT 沿用现有素材/OCR。青色 AUTO 在较暗的呼吸动画帧仍可能证据不足，此时保持 Unknown，不按关闭状态切换；不为消除 WARN 放宽颜色校验。红色及关闭 AUTO、观看关卡、HARD 和系列终点奖励不在本次实机范围内。

`Fixtures/Sortie/ja-progress-*.png` 保存独立原帧：准备入口来自 `20261022-182822/0008.png`，青色稳定与证据不足帧来自同目录 `0074.png`、`0067.png`，故事按钮及误判背景来自 `20261022-183723/0003.png`、`0005.png`。回归检查日文按钮互不混淆、AUTO 安全状态，以及旧提示独立匹配确实误中而新对话框检查拒绝该剧情帧；真实空位弹窗和取消动作仍被保留。

原始执行日志为 `progress-run.log`、`progress-resume.log`、`progress-resume2.log`、`progress-second.log`；最终证据是 `20261022-184746/0057.png`。这些全程调试文件保留在本地忽略目录，不进入产品包。

## 下载确认框补采

按 `resources/zh-CN/templates/stage/sortie/download.png` 的语义采集完整说明句：“ステージ開始に必要なデータをダウンロードします。”，而非弹窗标题。原始截图为本地 `research/japanese-adaptation/170-download-dialog.png`，独立验证帧为 `171-download-dialog-check.png`。整个采集过程没有点击下载或取消，游戏保持在原对话框。

| 模板 | 裁剪区域 x,y,w,h | 用途 |
| --- | --- | --- |
| `stage/sortie/download.png` | 559,384,787,38 | 完整数据下载说明句，不包含可变下载大小 |
| `dialog/download.png` | 970,791,359,120 | 完整下载按钮，保留外框与文字 |

完整日文说明句超过旧 666 像素区域；共用 `DownloadHintRoi` 横向扩大到整个对话框的 1080 像素，保持 y=60、高 420 及原阈值。普通出击、推图与恢复均复用该区域。取消按钮复用已有 `dialog/cancel.png`，按钮仍在原正中底部 825×300 范围内。

完整蓝色按钮与其他对话框共用外框，不能把按钮命中单独视为下载授权；生产出击流程先确认完整下载说明，再根据设置识别下载/取消按钮。`Fixtures/Sortie/ja-download-dialog.png` 保存独立原帧，回放验证说明句、两个完整按钮、允许/禁止下载分支及真实出击取消链路；空位确认框无法提供下载说明证据，仍按空位策略取消。实机下载过程和下载结束后的导航不在本次验证范围内。

实机回放发现下载框会被空位提示抢先识别，因此空位确认中的出击按钮复用现有 `StageEntryButtonRecognizer`，同时校验完整按钮与中央出击文字，保留原素材和阈值；下载框不再进入空位处理。
