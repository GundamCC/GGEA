using System.IO;
using System.IO.Compression;
using System.Text.Json;
using GGEA.App.Logging;

namespace GGEA.App.Vision;

/// <summary>同一帧实际使用的模板与匹配结果；低于阈值的候选仅用于诊断，不用于点击。</summary>
internal sealed record RecognitionCandidate(string Name, byte[] Png, int Width, int Height, Roi Roi, double Threshold, TemplateMatchResult? Best);

/// <summary>纯观察携带的原始帧与候选；由等待流程在真正超时后决定是否保存，不含执行副作用。</summary>
internal sealed record RecognitionFrame(string Serial, byte[] Png, ScreenImage Screen, RecognitionCandidate[] Candidates);

/// <summary>将识别失败证据保存为本地 ZIP；每类一分钟一次，最多五个固定槽位，不上传画面。</summary>
public sealed class RecognitionEvidence
{
    private readonly string _directory;
    private readonly IGgeaLog _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _lastSaved = new();
    private int _slot;

    /// <summary>由主窗口共享实例，使设置重建检测器后仍保留限频与轮转状态。</summary>
    public RecognitionEvidence(string directory, IGgeaLog log) { _directory = directory; _log = log; }

    /// <summary>保存流程最后观察到的帧，不为取证再次截图或复用上一次流程的状态。</summary>
    internal Task SaveAsync(string kind, RecognitionFrame frame)
        => SaveAsync(kind, frame.Serial, frame.Png, frame.Screen, frame.Candidates);

    /// <summary>等待文件写入结束，不另行截图；保存失败不改变业务识别结果。</summary>
    internal Task SaveAsync(string kind, string serial, byte[] png, ScreenImage screen, params RecognitionCandidate[] candidates)
        => Task.Run(() => Save(kind, serial, png, screen, candidates));

    /// <summary>先在临时文件完成 ZIP，再替换自己的固定槽位，避免暴露半写入诊断包。</summary>
    private void Save(string kind, string serial, byte[] png, ScreenImage screen, RecognitionCandidate[] candidates)
    {
        lock (_gate)
        {
            var key = kind + "|" + serial;
            var now = DateTime.UtcNow;
            if (_lastSaved.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(1)) return;
            _lastSaved[key] = now; // 写入失败同样限频，避免磁盘异常时反复阻塞识图。
            string? temporary = null;
            try
            {
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, $"recognition-{Environment.ProcessId}-{_slot % 5 + 1}.zip");
                temporary = path + ".tmp";
                using (var file = File.Create(temporary))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
                {
                    Write(zip, "frame.png", png);
                    for (var index = 0; index < candidates.Length; index++)
                        Write(zip, $"template-{index}.png", candidates[index].Png);
                    var metadata = new
                    {
                        utc = now, kind, serial, version = typeof(RecognitionEvidence).Assembly.GetName().Version?.ToString(),
                        width = screen.Width, height = screen.Height,
                        note = "流程超时前的原始观察帧；小区域精确搜索，大区域未达阈值时执行原图复查。低分候选仅为已评估位置中的最佳结果，不保证全局最优，不用于点击；空候选表示无有效搜索结果。",
                        candidates = candidates.Select((c, i) => new { c.Name, templateFile = $"template-{i}.png", c.Width, c.Height, configuredRoi = c.Roi, c.Threshold, best = c.Best,
                            accepted = c.Best is not null && c.Best.Confidence >= c.Threshold })
                    };
                    Write(zip, "diagnostic.json", JsonSerializer.SerializeToUtf8Bytes(metadata, new JsonSerializerOptions { WriteIndented = true }));
                }
                File.Move(temporary, path, overwrite: true);
                _slot++;
                _lastSaved[key] = now;
                _log.Warn($"识图失败证据已保存: {path}（{kind}，包含实际游戏画面与模板，仅本地保存；反馈前请检查隐私内容）");
            }
            catch (Exception ex)
            {
                // 不把包含路径或截图内容的异常正文写入普通日志。
                _log.Warn($"识图失败证据保存失败: {ex.GetType().Name}；原识别结果保持不变");
            }
            finally
            {
                try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>写入指定 ZIP 条目，所有文件名均由程序固定生成。</summary>
    private static void Write(ZipArchive zip, string name, byte[] bytes)
    {
        using var output = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
        output.Write(bytes);
    }
}
