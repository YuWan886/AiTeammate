using System.Text;
using Godot;

namespace AiTeammate.AiTeammateCode;

public class AiLogger
{
    private const string LogDir = "user://ai_teammate/logs";
    private const string LogFileName = "session.log";
    private const int MaxOldLogs = 5;
    // Flush after every write so logs are visible in real-time and survive crashes
    private const int FlushThreshold = 1;

    private string _logPath = "";
    private readonly StringBuilder _buffer = new();
    private readonly object _lock = new();
    private bool _initialized;

    public void Initialize()
    {
        if (_initialized) return;

        if (!DirAccess.DirExistsAbsolute(LogDir))
            DirAccess.MakeDirAbsolute(LogDir);

        _logPath = $"{LogDir}/{LogFileName}";

        // Rotate: if session.log already exists, rename it with timestamp
        RotateLogFile(_logPath, LogDir, "session", MaxOldLogs);

        _initialized = true;

        Log("SYSTEM", $"AI Teammate 日志会话开始");
        Log("SYSTEM", $"日志路径: {_logPath}");
    }

    /// <summary>
    /// Rotate a log file: rename current log to timestamped name, prune old logs to keep maxCount.
    /// </summary>
    internal static void RotateLogFile(string currentPath, string logDir, string logPrefix, int maxOldLogs)
    {
        try
        {
            if (!Godot.FileAccess.FileExists(currentPath))
                return;

            // Get last modified time of the existing log file
            string existingContent = "";
            ulong fileTime = 0;
            try
            {
                using var readFile = Godot.FileAccess.Open(currentPath, Godot.FileAccess.ModeFlags.Read);
                if (readFile != null)
                {
                    existingContent = readFile.GetAsText();
                    fileTime = Godot.FileAccess.GetModifiedTime(currentPath);
                }
            }
            catch { }

            // Rename: session.log → session_20260519_215906.log
            var dt = fileTime > 0
                ? DateTimeOffset.FromUnixTimeSeconds((long)fileTime).DateTime
                : DateTime.Now;
            var ts = dt.ToString("yyyyMMdd_HHmmss");
            var archivePath = $"{logDir}/{logPrefix}_{ts}.log";
            try
            {
                using var archiveFile = Godot.FileAccess.Open(archivePath, Godot.FileAccess.ModeFlags.Write);
                archiveFile?.StoreString(existingContent);
            }
            catch { /* best-effort archive */ }

            // Delete original so a fresh session.log can be created
            try { DirAccess.RemoveAbsolute(currentPath); }
            catch { }

            // Prune old logs: keep only maxOldLogs most recent files matching logPrefix_*.log
            PruneOldLogs(logDir, logPrefix, maxOldLogs);
        }
        catch { /* best-effort rotation */ }
    }

    /// <summary>
    /// Delete oldest log files matching {prefix}_*.log, keeping at most maxCount files.
    /// </summary>
    internal static void PruneOldLogs(string logDir, string prefix, int maxCount)
    {
        try
        {
            if (!DirAccess.DirExistsAbsolute(logDir))
                return;

            var dir = DirAccess.Open(logDir);
            if (dir == null)
                return;

            var pattern = $"{prefix}_*.log";
            var oldLogs = new List<(string path, ulong modified)>();

            dir.ListDirBegin();
            var fileName = dir.GetNext();
            while (!string.IsNullOrEmpty(fileName))
            {
                if (fileName.StartsWith(prefix + "_") && fileName.EndsWith(".log"))
                {
                    var fullPath = $"{logDir}/{fileName}";
                    ulong modTime = 0;
                    try { modTime = Godot.FileAccess.GetModifiedTime(fullPath); }
                    catch { }
                    oldLogs.Add((fullPath, modTime));
                }
                fileName = dir.GetNext();
            }
            dir.ListDirEnd();

            // Sort by modified time descending (newest first), delete excess
            if (oldLogs.Count > maxCount)
            {
                var toDelete = oldLogs
                    .OrderByDescending(x => x.modified)
                    .Skip(maxCount)
                    .Select(x => x.path)
                    .ToList();

                foreach (var path in toDelete)
                {
                    try { DirAccess.RemoveAbsolute(path); }
                    catch { }
                }
            }
        }
        catch { /* best-effort pruning */ }
    }

    public void Log(string category, string message)
    {
        if (!_initialized) return;

        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{category}] {message}";
        bool shouldFlush;
        lock (_lock)
        {
            _buffer.AppendLine(line);
            shouldFlush = _buffer.Length >= FlushThreshold;
        }

        if (shouldFlush)
            Flush();
    }

    public void LogDecision(string thinking, IReadOnlyList<Decision.DecisionAction> actions)
    {
        if (!_initialized) return;

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

    public void LogDecisionFailure(string reason)
    {
        Log("AI_DECISION", $"决策失败: {reason}，使用备选方案");
    }

    public void LogCardPlay(string cardName, string? target)
    {
        Log("AI_PLAY", string.IsNullOrWhiteSpace(target)
            ? $"打出 [{cardName}]"
            : $"打出 [{cardName}] -> {target}");
    }

    public void LogChat(string sender, string message)
    {
        Log("CHAT", $"[{sender}] {message}");
    }

    public void LogEvent(string eventType, string detail)
    {
        Log("EVENT", $"{eventType} | {detail}");
    }

    public void LogCombatStart(int round, string enemies)
    {
        Log("COMBAT", $"回合 {round} 开始 | 敌人: {enemies}");
    }

    public void LogCombatEnd(string result)
    {
        Log("COMBAT", $"战斗{result}");
    }

    public void LogError(string context, string error)
    {
        Log("ERROR", $"{context}: {error}");
    }

    public void Flush()
    {
        if (string.IsNullOrEmpty(_logPath)) return;

        string text;
        lock (_lock)
        {
            if (_buffer.Length == 0) return;
            text = _buffer.ToString();
            _buffer.Clear();
        }

        try
        {
            // Read existing + append new content
            string existing = "";
            if (Godot.FileAccess.FileExists(_logPath))
            {
                using var readFile = Godot.FileAccess.Open(_logPath, Godot.FileAccess.ModeFlags.Read);
                if (readFile != null)
                    existing = readFile.GetAsText();
            }

            using var writeFile = Godot.FileAccess.Open(_logPath, Godot.FileAccess.ModeFlags.Write);
            if (writeFile != null)
                writeFile.StoreString(existing + text);
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[AiTeammate] Failed to flush log: {ex.Message}");
        }
    }

    public void SessionEnd()
    {
        Log("SYSTEM", "会话结束");
        Flush();
    }
}

/// <summary>
/// 专门记录发送给大模型的请求和响应内容，用于调试解析失败等问题。
/// 输出到独立的 ai_traffic.log 文件（旧日志自动归档并保留最近5个）。
/// </summary>
public class AiTrafficLogger
{
    private const string LogDir = "user://ai_teammate/logs";
    private const string LogFileName = "ai_traffic.log";
    private const int MaxOldLogs = 5;
    private readonly string _logPath;
    private readonly object _lock = new();
    private static AiTrafficLogger? _instance;

    public static AiTrafficLogger Instance => _instance ??= new AiTrafficLogger();

    private AiTrafficLogger()
    {
        if (!DirAccess.DirExistsAbsolute(LogDir))
            DirAccess.MakeDirAbsolute(LogDir);

        _logPath = $"{LogDir}/{LogFileName}";

        // Rotate: if ai_traffic.log already exists, rename it with timestamp
        AiLogger.RotateLogFile(_logPath, LogDir, "ai_traffic", MaxOldLogs);

        WriteLine("══════════ AI Traffic 日志开始 ══════════");
    }

    /// <summary>
    /// 记录发送给 LLM 的请求
    /// </summary>
    public void LogRequest(string requestType, string systemPrompt, string userPrompt, IReadOnlyList<Backend.AiMessage>? history, int maxTokens, float temperature)
    {
        var sb = new StringBuilder();
        var sep = new string('═', 80);
        sb.AppendLine($"\n{sep}");
        sb.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] REQUEST → {requestType}");
        sb.AppendLine($"  MaxTokens={maxTokens} Temperature={temperature}");
        sb.AppendLine($"  ── System ──");
        sb.AppendLine(TruncateForLog(systemPrompt, 3000));
        sb.AppendLine($"  ── History ({history?.Count ?? 0}) ──");
        sb.AppendLine(FormatHistory(history));
        sb.AppendLine($"  ── User ──");
        sb.AppendLine(TruncateForLog(userPrompt, 3000));
        sb.AppendLine($"  ── END ──");
        WriteLine(sb.ToString());
    }

    /// <summary>
    /// 记录 LLM 返回的响应
    /// </summary>
    public void LogResponse(string requestType, bool success, string content, int tokensUsed, long latencyMs)
    {
        var sb = new StringBuilder();
        var sep = new string('─', 80);
        sb.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] RESPONSE ← {requestType}");
        sb.AppendLine($"  Success={success} Tokens={tokensUsed} Latency={latencyMs}ms");
        sb.AppendLine($"  ── Content ──");
        sb.AppendLine(TruncateForLog(content, 5000));
        sb.AppendLine($"  ── END ──");
        sb.AppendLine(sep);
        WriteLine(sb.ToString());
    }

    /// <summary>
    /// 记录错误
    /// </summary>
    public void LogError(string requestType, string errorMessage)
    {
        var sb = new StringBuilder();
        var sep = new string('─', 80);
        sb.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] ERROR ← {requestType}");
        sb.AppendLine($"  {errorMessage}");
        sb.AppendLine(sep);
        WriteLine(sb.ToString());
    }

    private void WriteLine(string text)
    {
        lock (_lock)
        {
            try
            {
                string existing = "";
                if (Godot.FileAccess.FileExists(_logPath))
                {
                    using var readFile = Godot.FileAccess.Open(_logPath, Godot.FileAccess.ModeFlags.Read);
                    if (readFile != null)
                        existing = readFile.GetAsText();
                }

                using var writeFile = Godot.FileAccess.Open(_logPath, Godot.FileAccess.ModeFlags.Write);
                writeFile?.StoreString(existing + text + "\n");
            }
            catch { /* best-effort logging */ }
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
