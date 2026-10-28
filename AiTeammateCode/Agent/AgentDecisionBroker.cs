using System.Text.Json;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Entities.Players;

namespace AiTeammate.AiTeammateCode.Agent;

/// <summary>
/// Lets an attached dsh client act as the brain of the AI player.
///
/// The mod keeps its hands (combat executor, room automation, reward claiming, map voting, sync),
/// but instead of asking the configured LLM backend for a decision it parks the call here and waits
/// for <c>sts2_decide</c>. The client sees the exact prompt the LLM would have seen and answers with
/// the same JSON the parser expects, so nothing downstream changes.
/// 对接 dsh 后，AI 的决策改由 dsh 给出（dsh 当大脑，mod 当手）。
/// </summary>
internal static class AgentDecisionBroker
{
    private const int MaxWaitSeconds = 300;
    private const int PollMs = 500;

    private sealed class Pending
    {
        public string Id = "";
        public string Kind = "";
        public string PlayerName = "";
        public ulong PlayerNetId;
        public string SystemPrompt = "";
        public string Prompt = "";
        public DateTimeOffset CreatedAt = DateTimeOffset.UtcNow;
        public readonly TaskCompletionSource<string?> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly Lock Sync = new();
    private static Pending? _pending;
    private static int _sequence;

    /// <summary>
    /// True when an external agent is attached with action rights: it becomes the AI's decision source.
    /// 外部 Agent 正在接管 AI 的大脑。
    /// </summary>
    public static bool Enabled => AgentBridgeService.ExternalAgentDriving;

    public static bool HasPending
    {
        get { lock (Sync) return _pending != null; }
    }

    /// <summary>JSON view of the outstanding decision, or null. Main-thread safe.</summary>
    public static JsonObject? SnapshotJson()
    {
        lock (Sync)
        {
            if (_pending == null)
                return null;

            return new JsonObject
            {
                ["id"] = _pending.Id,
                ["kind"] = _pending.Kind,
                ["player"] = _pending.PlayerName,
                ["net_id"] = _pending.PlayerNetId,
                ["age_seconds"] = Math.Round((DateTimeOffset.UtcNow - _pending.CreatedAt).TotalSeconds, 1),
                ["max_wait_seconds"] = MaxWaitSeconds,
                ["system_prompt"] = _pending.SystemPrompt,
                ["prompt"] = _pending.Prompt,
                ["answer_with"] = "sts2_decide {decision: <the JSON object the prompt asks for>}"
            };
        }
    }

    /// <summary>
    /// Blocks the AI's decision call until the client answers, the client goes away, or the wait
    /// budget expires. Returns null when nothing usable arrived (callers fall back to their defaults).
    /// </summary>
    public static async Task<string?> RequestAsync(Player aiPlayer, string kind, string systemPrompt,
        string prompt, CancellationToken ct)
    {
        if (!Enabled)
            return null;

        Pending pending;
        Pending? superseded = null;
        lock (Sync)
        {
            // Only one decision can be outstanding: resolve a stale one instead of leaving its
            // caller waiting for the full timeout.
            superseded = _pending;
            pending = new Pending
            {
                Id = "d" + (++_sequence),
                Kind = kind,
                PlayerName = aiPlayer?.Character?.Id.Entry ?? "ai",
                PlayerNetId = aiPlayer?.NetId ?? 0,
                SystemPrompt = systemPrompt,
                Prompt = prompt
            };
            _pending = pending;
        }

        if (superseded != null)
        {
            MainFile.Logger.Warn($"[Agent] decision {superseded.Id} superseded by {pending.Id}");
            superseded.Completion.TrySetResult(null);
        }

        MainFile.Logger.Info($"[Agent] waiting for dsh decision {pending.Id} ({kind})");
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(MaxWaitSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            while (!pending.Completion.Task.IsCompleted)
            {
                if (!Enabled)
                    break; // client disconnected or actions disabled mid-wait

                try
                {
                    await Task.Delay(PollMs, linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (pending.Completion.Task.IsCompleted)
            {
                var content = await pending.Completion.Task;
                if (!string.IsNullOrWhiteSpace(content))
                    MainFile.Logger.Info($"[Agent] dsh answered decision {pending.Id} ({kind})");
                else
                    MainFile.Logger.Info($"[Agent] decision {pending.Id} ({kind}) cancelled");
                return content;
            }

            MainFile.Logger.Warn($"[Agent] dsh decision {pending.Id} ({kind}) not answered in time");
            return null;
        }
        finally
        {
            lock (Sync)
            {
                if (ReferenceEquals(_pending, pending))
                    _pending = null;
            }

            pending.Completion.TrySetResult(null);
        }
    }

    /// <summary>Completes the parked decision with the client's answer. Returns an error string on refusal.</summary>
    public static (bool Ok, string Message) Resolve(string decision)
    {
        Pending? pending;
        lock (Sync)
            pending = _pending;

        if (pending == null)
            return (false, "No decision is pending right now. Call sts2_state / sts2_wait first.");

        var content = Normalize(decision);
        if (content.Length == 0)
            return (false, "Missing 'decision'. Pass the JSON object the prompt asked for.");

        if (!LooksLikeDecision(content, out var error))
            return (false, error + " The decision stays pending; answer again with valid JSON.");

        lock (Sync)
        {
            if (ReferenceEquals(_pending, pending))
                _pending = null;
        }

        pending.Completion.TrySetResult(content);
        return (true, $"Answered the {pending.Kind} decision ({pending.Id}).");
    }

    /// <summary>Unblocks a pending decision with a failure, e.g. when the client disconnects.</summary>
    public static void Abort(string reason)
    {
        Pending? pending;
        lock (Sync)
        {
            pending = _pending;
            _pending = null;
        }

        if (pending == null)
            return;

        MainFile.Logger.Info($"[Agent] decision {pending.Id} aborted: {reason}");
        pending.Completion.TrySetResult(null);
    }

    // ---- Helpers -------------------------------------------------------------------------

    private static string Normalize(string decision)
    {
        var text = (decision ?? "").Trim();
        if (text.Length == 0)
            return "";

        // Tolerate fenced code blocks: the agent may wrap the object as a markdown code block.
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0)
                text = text[(firstNewline + 1)..];
            if (text.EndsWith("```", StringComparison.Ordinal))
                text = text[..^3];
            text = text.Trim();
        }

        return text;
    }

    /// <summary>
    /// Very light sanity check: it must be a JSON object carrying at least one field the mod's
    /// parsers look for. Rejecting early is better than letting the game fall back silently.
    /// </summary>
    private static bool LooksLikeDecision(string content, out string error)
    {
        error = "";
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "The decision must be a JSON object.";
                return false;
            }

            string[] known =
            {
                "actions", "end_turn", "choice", "choices", "indices", "index", "upgrade_indices",
                "thinking", "reason", "chat_message", "deferred_actions", "reroll"
            };

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (known.Any(k => string.Equals(k, property.Name, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }

            error = "That JSON has none of the fields this decision expects "
                    + "(actions / end_turn / choice / choices / indices / upgrade_indices).";
            return false;
        }
        catch (JsonException ex)
        {
            error = "Invalid JSON: " + ex.Message;
            return false;
        }
    }
}
