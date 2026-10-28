# 贡献者头像自动同步

中日文 README 引用 `docs/contributors/` 中的圆形 SVG 头像，点击后进入成员的 GitHub 主页。头像内嵌 PNG，不依赖第三方图片加工服务。

`Update contributor avatars` 工作流每天在 UTC 02:23（香港时间 10:23）检查 `periaperia`、`Torfaen` 的公开 GitHub 头像。GitHub 调度和图片缓存可能造成延迟，不保证即时更新。可在仓库 Actions 页面选择该工作流，点击 **Run workflow** 手动同步；修改同步脚本或工作流并推送到 `main` 也会触发检查。

下载和解码全部成功后才更新文件；网络或格式错误会使工作流失败，原有头像继续使用。头像未变化时不产生提交。只暂存两个指定 SVG，通过内置 `GITHUB_TOKEN` 提交到 `main`，无需个人 Token，不更新产品版本、不打包、不发布。分支规则阻止机器人写入或分支发生并发更新时会失败，不绕过规则或强推；请查看 Actions 日志。

公开仓库连续 60 天没有活动时，GitHub 可能自动停用定时工作流，届时需在 Actions 中重新启用。参见 [GitHub 定时工作流说明](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#schedule)。

本地同步使用 Windows PowerShell 7：

```powershell
pwsh -File tools/update-contributor-avatars.ps1
```

仅验证生成结果时，可用 `-OutputDirectory` 指向临时目录。脚本采用 Windows 图像解码器，将 GIF 等格式转换为静态 PNG；圆形由 SVG 裁切，保留原始头像比例。更换贡献者时同步脚本名单、工作流暂存路径及两份 README 链接。
