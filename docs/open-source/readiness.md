# 开源准备与发布状态

更新日期：2026-10-28。新源码仓库与正式版已经公开；下文分别记录已完成事项和仍待确认的权利与验证事项。

## 公开入口

- 源码：[GundamCC/GGEA](https://github.com/GundamCC/GGEA)，后续在该仓库的 `main` 分支开发。
- 问题反馈：[GitHub Issues](https://github.com/GundamCC/GGEA/issues)。
- 正式版：[v1.3.3.2 发行页](https://github.com/GundamCC/GGEA-Release/releases/tag/v1.3.3.2)。
- 下载：[Windows x64 ZIP](https://github.com/GundamCC/GGEA-Release/releases/download/v1.3.3.2/GGEA-v1.3.3.2-win-x64.zip) · [SHA-256 校验文件](https://github.com/GundamCC/GGEA-Release/releases/download/v1.3.3.2/GGEA-v1.3.3.2-win-x64.zip.sha256)。

## 已完成

- 自有代码采用 Apache-2.0，另附 NOTICE 和授权范围；第三方、模型和游戏美术不混入自有代码授权。
- 补齐第三方软件许可、来源及摘要，并随构建输出分发。参见 [组件声明](../../resources/common/licenses/NOTICE.txt) 与 [OCR 说明](../../resources/common/ocr/NOTICE.txt)。
- 建立 [633 张图片的来源与隐私清单](assets.md)，完成所记录范围的目视检查；授权依据仍为待确认，未替换图标或修改识别模板。
- 依赖本机研究目录的实机脚本移出公共工具。发布管理器仅保留本地构建和打包；远程发布、凭证工具及维护者配置保存在被忽略的本机目录。
- 原私有仓库完成分支整合后，将已提交源码导出到新仓库，形成独立初始提交 [`9fbe302`](https://github.com/GundamCC/GGEA/commit/9fbe30250579a2e87393de4aeb5aaead0ba09100)。1141 个文件与导出源树核对一致，未携带旧 Git 历史、本机私有目录或构建缓存。
- 新仓库 `main` 已推送至 GitHub，并核对远程提交与本地一致；原私有仓库保留，未将整个 `.git` 或其他分支、引用镜像到新仓库。
- 在新仓库完成独立 Build 与 30 项核心冒烟、版本及资源检查；正式打包使用固定源码提交的导出快照。
- v1.3.3.2 完成 Release Build、30 项核心冒烟、资源与版本一致性、ZIP 解压、逐文件摘要、防混用检查及 EXE 启动自检。另核验许可证文件与 39 份上游许可摘要，确认私有发布工具和未使用的 FFmpeg DLL 不在包内。
- 安装包及校验文件已发布至 `GGEA-Release`，远程状态为正式版，两个附件均上传完成且摘要匹配。发行说明提交为 [`f99cf21`](https://github.com/GundamCC/GGEA-Release/commit/f99cf21f26a08bd6b81e5fa99e265b316499ec61)。
- [旧发行包清单](legacy-releases.md)中的 30 个旧 Release 及 60 个附件已下架。删除后曾复查剩余 0 个 Release，随后新增本页所述正式版；不再用“剩余 0 个”描述当前发行仓库。
- README 已补齐新源码、问题反馈与当前正式版下载入口，移除“没有可下载安装包”和“尚未公开”的过期说明。

## 当前发行身份

| 项目 | 值 |
| --- | --- |
| 产品版本 | v1.3.3.2 |
| 源码提交 | `9fbe30250579a2e87393de4aeb5aaead0ba09100` |
| 发行渠道 / 平台 | Release / Windows x64 |
| ZIP 文件名 | `GGEA-v1.3.3.2-win-x64.zip` |
| ZIP SHA-256 | `64bf3a1f5aa391ddb31a0ac9cdc7faf4f1d1285f217100a55bc8a2e1113c7bfd` |

后续文档提交不改变上述已发布二进制对应的源码提交；重新打包或发布时按实际产物更新记录。

## 仍待确认与维护边界

- 游戏素材、图标的可再分发依据，以及第三方原生构建的适用性仍待确认。已经公开是交付事实，不等于这些权利事项已解决；说明或免责声明不能代替实际依据。
- 本次发布未执行模拟器实机验收，Build、冒烟及启动自检不代表所有模拟器和游戏流程均兼容。
- 旧 Git 标签未删除，原私有仓库和旧历史仍保留；第三方下载、缓存或副本不受本次下架控制。如需进一步清理，应明确目标与范围。
- 官网与独立文档站尚无已核验的公开入口，当前使用仓库内文档。
- 后续保持源码与发行包提交可追溯，依赖升级时更新许可，素材变化时更新来源、摘要与隐私检查记录；私有发布工具和凭证继续留在本机。
