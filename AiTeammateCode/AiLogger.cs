using System.Text;
using Godot;
using AiTeammate.AiTeammateCode.Settings;

namespace AiTeammate.AiTeammateCode;

/// <summary>
/// Append-only decision log. A single <see cref="FileAccess"/> handle is kept open and seeked to
/// the end, so logging a line is O(line) instead of the previous read-whole-file + rewrite.
/// </summary>
public class AiLogger
{
    private const string LogDir = ModPaths.LogDir;
    private const string LogFileName = "session.log";

    private string _logPath = "";
    private readonly object _lock = new();
    private Godot.FileAccess? _handle;
    private bool _initialized;

    public void Initialize()
    {
        if (_initialized) return;

        ModPaths.EnsureDir(LogDir);
        _logPath = $"{LogDir}/{LogFileName}";

        RotateLogFile(_logPath, LogDir, "session", Math.Clamp(AiSettingsStore.Current.LogRetention, 1, 20));
        OpenHandle();

        _initialized = true;

        Log("SYSTEM", "AI Teammate 日志会话开始");
        Log("SYSTEM", $"日志路径: {_logPath}");
    }

    private void OpenHandle()
    {
        lock (_lock)
        {
            _handle?.Dispose();
            _handle = null;

            if (!Godot.FileAccess.FileExists(_logPath))
            {
                using (Godot.FileAccess.Open(_logPath, Godot.FileAccess.ModeFlags.Write))
                {
                    // Create an empty file so READ_WRITE can open it below.
                }
            }

            _handle = Godot.FileAccess.Open(_logPath, Godot.FileAccess.ModeFlags.ReadWrite);
            _handle?.SeekEnd();
        }
    }

    /// <summary>Rotate <paramref name="currentPath"/> to a timestamped name and prune old logs.
    /// 轮转日志并保留最近 <paramref name="maxOldLogs"/> 个历史文件。</summary>
    internal static void RotateLogFile(string currentPath, string logDir, string logPrefix, int maxOldLogs)
    {
        try
        {
            if (!Godot.FileAccess.FileExists(currentPath))
                return;

            var modified = Godot.FileAccess.GetModifiedTime(currentPath);
            var dt = modified > 0
                ? DateTimeOffset.FromUnixTimeSeconds((long)modified).DateTime
                : DateTime.Now;
            var archivePath = $"{logDir}/{logPrefix}_{dt:yyyyMMdd_HHmmss}.log";

            if (Godot.FileAccess.FileExists(archivePath))
                DirAccess.RemoveAbsolute(archivePath);

            DirAccess.RenameAbsolute(currentPath, archivePath);
            PruneOldLogs(logDir, logPrefix, maxOldLogs);
        }
        catch
        {
            // Best-effort rotation.
        }
    }

    internal static void PruneOldLogs(string logDir, string prefix, int maxCount)
    {
        try
        {
            if (!DirAccess.DirExistsAbsolute(logDir))
                return;

            using var dir = DirAccess.Open(logDir);
            if (dir == null)
                return;

            var oldLogs = new List<(string Path, ulong Modified)>();
            dir.ListDirBegin();
            var fileName = dir.GetNext();
            while (!string.IsNullOrEmpty(fileName))
            {
                if (fileName.StartsWith(prefix + "_", StringComparison.Ordinal) && fileName.EndsWith(".log", StringComparison.Ordinal))
                {
                    var fullPath = $"{logDir}/{fileName}";
                    ulong modifiedTime = 0;
                    try { modifiedTime = Godot.FileAccess.GetModifiedTime(fullPath); }
                    catch { }
                    oldLogs.Add((fullPath, modifiedTime));
                }
                fileName = dir.GetNext();
            }
            dir.ListDirEnd();

            foreach (var (path, _) in oldLogs.OrderByDescending(x => x.Modified).Skip(maxCount))
            {
                try { DirAccess.RemoveAbsolute(path); }
                catch { }
            }
        }
        catch
        {
            // Best-effort pruning.
        }
    }

    public void Log(string category, string message)
    {
        if (!_initialized || !AiSettingsStore.Current.EnableDecisionLog)
            return;

        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{category}] {message}\n";

        lock (_lock)
        {
            var handle = _handle;
            if (handle == null || !GodotObject.IsInstanceValid(handle))
            {
                OpenHandleWithoutLock();
                handle = _handle;
            }

            try
            {
                handle?.StoreString(line);
                handle?.Flush();
            }
            catch
            {
                // A failed write must never break gameplay; reopen on the next line.
                try { _handle?.Dispose(); } catch { }
                _handle = null;
            }
        }
    }

    private void OpenHandleWithoutLock()
    {
        if (!Godot.FileAccess.FileExists(_logPath))
        {
            using (Godot.FileAccess.Open(_logPath, Godot.FileAccess.ModeFlags.Write))
            {
            }
        }

        _handle = Godot.FileAccess.Open(_logPath, Godot.FileAccess.ModeFlags.ReadWrite);
        _handle?.SeekEnd();
    }

    public void LogDecision(string thinking, IReadOnlyList<Decision.DecisionAction> actions)
    {
        if (!string.IsNullOrWhiteSpace(thinking))
            Log("AI_THINK", thinking);

        foreach (var action in actions)
        {
            switch (action.Type)
            {
                case "play_card":
                    Log("AI_ACTION", $"出牌 [{action.CardName}] -> {(string.IsNullOrWhiteSpace(action.Target) ? "无目标" : action.Target)} | {action.Reason}");
                    break;
                case "end_turn":
                    Log("AI_ACTION", "结束回合");
                    break;
                case "suggest_card":
                    Log("AI_SUGGEST", $"建议卡牌 [{action.CardName}] | {action.Reason}");
                    break;
                case "deferred_attack":
                case "deferred_skill":
                    Log("AI_DEFER", $"协同延迟 [{action.CardName}] -> {(string.IsNullOrWhiteSpace(action.Target) ? "无目标" : action.Target)} | 等待: {action.WaitFor}");
                    break;
            }
        }
    }

    public void LogDecisionFailure(string reason) =>
        Log("AI_DECISION", $"决策失败: {reason}，使用备选方案");

    public void LogCardPlay(string cardName, string? target) =>
        Log("AI_PLAY", string.IsNullOrWhiteSpace(target) ? $"打出 [{cardName}]" : $"打出 [{cardName}] -> {target}");

    public void LogChat(string sender, string message) => Log("CHAT", $"[{sender}] {message}");

    public void LogEvent(string eventType, string detail) => Log("EVENT", $"{eventType} | {detail}");

    public void LogError(string context, string error) => Log("ERROR", $"{context}: {error}");

    public void Flush()
    {
        lock (_lock)
        {
            try { _handle?.Flush(); }
            catch { }
        }
    }
}

/// <summary>
/// Request/response traffic log for debugging malformed model output. Off by default; only
/// active while the "请求/响应日志" setting is enabled.
/// </summary>
public class AiTrafficLogger
{
    private const string LogDir = ModPaths.LogDir;
    private const string LogFileName = "ai_traffic.log";

    private readonly string _logPath;
    private readonly object _lock = new();
    private Godot.FileAccess? _handle;
    private static AiTrafficLogger? _instance;

    public static AiTrafficLogger Instance => _instance ??= new AiTrafficLogger();

    private static bool Enabled => AiSettingsStore.Current.EnableTrafficLog;

    private AiTrafficLogger()
    {
        ModPaths.EnsureDir(LogDir);
        _logPath = $"{LogDir}/{LogFileName}";
        AiLogger.RotateLogFile(_logPath, LogDir, "ai_traffic",
            Math.Clamp(AiSettingsStore.Current.LogRetention, 1, 20));

        if (!Godot.FileAccess.FileExists(_logPath))
        {
            using (Godot.FileAccess.Open(_logPath, Godot.FileAccess.ModeFlags.Write))
            {
            }
        }

        _handle = Godot.FileAccess.Open(_logPath, Godot.FileAccess.ModeFlags.ReadWrite);
        _handle?.SeekEnd();
    }

    public void LogRequest(string requestType, string systemPrompt, string userPrompt,
        IReadOnlyList<Backend.AiMessage>? history, int maxTokens, float temperature)
    {
        if (!Enabled) return;

        var sb = new StringBuilder();
        var sep = new string('═', 80);
        sb.AppendLine($"\n{sep}");
        sb.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] REQUEST → {requestType}");
        sb.AppendLine($"  MaxTokens={maxTokens} Temperature={temperature}");
        sb.AppendLine("  ── System ──");
        sb.AppendLine(TruncateForLog(systemPrompt, 3000));
        sb.AppendLine($"  ── History ({history?.Count ?? 0}) ──");
        sb.AppendLine(FormatHistory(history));
        sb.AppendLine("  ── User ──");
        sb.AppendLine(TruncateForLog(userPrompt, 3000));
        sb.AppendLine("  ── END ──");
        Write(sb.ToString());
    }

    public void LogResponse(string requestType, bool success, string content, int tokensUsed, long latencyMs)
    {
        if (!Enabled) return;

        var sb = new StringBuilder();
        var sep = new string('─', 80);
        sb.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] RESPONSE ← {requestType}");
        sb.AppendLine($"  Success={success} Tokens={tokensUsed} Latency={latencyMs}ms");
        sb.AppendLine("  ── Content ──");
        sb.AppendLine(TruncateForLog(content, 5000));
        sb.AppendLine("  ── END ──");
        sb.AppendLine(sep);
        Write(sb.ToString());
    }

    public void LogError(string requestType, string errorMessage)
    {
        if (!Enabled) return;

        var sb = new StringBuilder();
        var sep = new string('─', 80);
        sb.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] ERROR ← {requestType}");
        sb.AppendLine($"  {errorMessage}");
        sb.AppendLine(sep);
        Write(sb.ToString());
    }

    private void Write(string text)
    {
        lock (_lock)
        {
            try
            {
                _handle?.StoreString(text + "\n");
                _handle?.Flush();
            }
            catch
            {
                try { _handle?.Dispose(); } catch { }
                _handle = null;
            }
        }
    }

    private static string TruncateForLog(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        if (text.Length <= maxLen) return text;
        return text[..maxLen] + $"\n  ... (truncated, total {text.Length} chars)";
    }

    private static string FormatHistory(IReadOnlyList<Backend.AiMessage>? history)
    {
        if (history == null || history.Count == 0)
            return "(empty)";

        var sb = new StringBuilder();
        foreach (var msg in history.Take(6))
        {
            var content = TruncateForLog(msg.Content, 240).Replace("\r", " ").Replace("\n", "\\n");
            sb.AppendLine($"  [{msg.Role}] {content}");
        }

        if (history.Count > 6)
            sb.AppendLine($"  ... ({history.Count - 6} more messages)");

        return sb.ToString().TrimEnd();
    }
}
