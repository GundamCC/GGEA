using System.IO;
using GGEA.App.IO;
using System.IO.Compression;
using System.Text.Json;
using GGEA.App.Logging;

namespace GGEA.App.Vision;

/// <summary>同一帧实际使用的模板与匹配结果；低于阈值的候选仅用于诊断，不用于点击。</summary>
internal sealed record RecognitionCandidate(string Name, byte[] Png, int Width, int Height, Roi Roi, double Threshold, TemplateMatchResult? Best);

/// <summary>纯观察携带的原始帧与候选；由等待流程在真正超时后决定是否保存，不含执行副作用。</summary>
internal sealed record RecognitionFrame(string Serial, byte[] Png, ScreenImage Screen, RecognitionCandidate[] Candidates);

/// <summary>识别流程提供的固定名称附件；只写入本地诊断包，不接受外部路径。</summary>
internal sealed record RecognitionAttachment(string Name, byte[] Bytes);

/// <summary>将识别失败证据保存为本地 ZIP；每类一分钟一次，最多五个固定槽位，不上传画面。</summary>
public sealed class RecognitionEvidence
{
    private readonly string _directory;
    private readonly IGgeaLog _log;
    // 同一进程同一目录必须共享锁、限频和轮转状态，导航器重建不能从槽位一覆盖别的流程。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, EvidenceState> States =
        new(FileSystemPaths.Comparer);
    private readonly EvidenceState _state;

    /// <summary>目录拥有轮转状态，实例只负责提交证据。</summary>
    private sealed class EvidenceState
    {
        public readonly object Gate = new();
        public readonly Dictionary<string, DateTime> LastSaved = new();
        public int Slot;
    }

    /// <summary>以规范化目录共享状态，使不同流程与重建实例遵守同一五槽位上限。</summary>
    public RecognitionEvidence(string directory, IGgeaLog log)
    {
        _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        _log = log;
        _state = States.GetOrAdd(_directory, _ => new EvidenceState());
    }

    /// <summary>保存流程最后观察到的帧，不为取证再次截图或复用上一次流程的状态。</summary>
    internal Task SaveAsync(string kind, RecognitionFrame frame)
        => SaveAsync(kind, frame.Serial, frame.Png, frame.Screen, frame.Candidates);

    /// <summary>等待文件写入结束，不另行截图；保存失败不改变业务识别结果。</summary>
    internal Task SaveAsync(string kind, string serial, byte[] png, ScreenImage screen, params RecognitionCandidate[] candidates)
        => Task.Run(() => Save(kind, serial, png, screen, candidates));

    /// <summary>特征识别保留自己的计数与阶段，不把 SIFT 内点数伪装成模板置信度。</summary>
    internal Task SaveContextAsync(string kind, string serial, ScreenImage screen, object context,
        params RecognitionAttachment[] attachments)
        => Task.Run(() => Save(kind, serial, ImageCodec.EncodePng(screen.Bgra, screen.Width, screen.Height),
            screen, [], context, attachments));

    /// <summary>先在临时文件完成 ZIP，再替换自己的固定槽位，避免暴露半写入诊断包。</summary>
    private void Save(string kind, string serial, byte[] png, ScreenImage screen, RecognitionCandidate[] candidates,
        object? context = null, RecognitionAttachment[]? attachments = null)
    {
        lock (_state.Gate)
        {
            var key = kind + "|" + serial;
            var now = DateTime.UtcNow;
            if (_state.LastSaved.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(1)) return;
            _state.LastSaved[key] = now; // 写入失败同样限频，避免磁盘异常时反复阻塞识图。
            string? temporary = null;
            try
            {
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, $"recognition-{Environment.ProcessId}-{_state.Slot % 5 + 1}.zip");
                temporary = path + ".tmp";
                using (var file = File.Create(temporary))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
                {
                    Write(zip, "frame.png", png);
                    for (var index = 0; index < candidates.Length; index++)
                        Write(zip, $"template-{index}.png", candidates[index].Png);
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "frame.png", "diagnostic.json" };
                    for (var index = 0; index < candidates.Length; index++) names.Add($"template-{index}.png");
                    foreach (var attachment in attachments ?? [])
                    {
                        if (Path.GetFileName(attachment.Name) != attachment.Name ||
                            attachment.Name.Contains('\\') || attachment.Name.Contains('/') ||
                            !names.Add(attachment.Name))
                            throw new InvalidDataException("Invalid evidence attachment name");
                        Write(zip, attachment.Name, attachment.Bytes);
                    }
                    // 核心程序集有独立身份；诊断记录启动宿主的产品版本，不能误报为核心库版本。
                    var metadata = new
                    {
                        utc = now, kind, serial, version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(),
                        width = screen.Width, height = screen.Height,
                        note = context is null
                            ? "流程超时前的原始观察帧；小区域精确搜索，大区域未达阈值时执行原图复查。低分候选仅为已评估位置中的最佳结果，不保证全局最优，不用于点击；空候选表示无有效搜索结果。"
                            : "失败阶段最后一次实际观察帧；context 记录阶段、目标及特征判定，附件保留首页观察帧和实际使用的单位素材。诊断内容不授权点击。",
                        context,
                        candidates = candidates.Select((c, i) => new { c.Name, templateFile = $"template-{i}.png", c.Width, c.Height, configuredRoi = c.Roi, c.Threshold, best = c.Best,
                            accepted = c.Best is not null && c.Best.Confidence >= c.Threshold })
                    };
                    Write(zip, "diagnostic.json", JsonSerializer.SerializeToUtf8Bytes(metadata, new JsonSerializerOptions { WriteIndented = true }));
                }
                File.Move(temporary, path, overwrite: true);
                _state.Slot++;
                _state.LastSaved[key] = now;
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
