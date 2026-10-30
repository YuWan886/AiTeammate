using System.Text.Json.Nodes;
using AiTeammate.AiTeammateCode.Localization;
using AiTeammate.AiTeammateCode.Settings;

namespace AiTeammate.AiTeammateCode.Agent;

/// <summary>
/// Invitation side of auto takeover. The bridge is request/response only, so a dsh plugin cannot be
/// pushed a "the game needs you" event; instead it polls <c>sts2_takeover</c> and the mod decides.
///
/// Claiming is what makes the whole thing safe: the first plugin that asks while the run is idle
/// takes the run, later polls are answered with <c>claimed</c>, and a claim that never turns into
/// real activity expires so a broken session cannot block the run forever.
/// 对局等待 dsh 认领；先到先得。
/// </summary>
internal static class AgentTakeoverInvite
{
    /// <summary>
    /// First message of the conversation the plugin opens. Placeholders: <c>{Host}</c>,
    /// <c>{Port}</c>, <c>{Token}</c> (a whole clause or an empty string), <c>{Workspace}</c>,
    /// <c>{Character}</c>.
    /// </summary>
    public const string DefaultPrompt =
        "来玩《杀戮尖塔 2》。本局有 AI 队友，交给你接管。游戏侧的外部 Agent 桥接已经开在 {Host}:{Port}。{Token}\n" +
        "先调用 sts2_status 确认已连接，再用 sts2_autoplay 打开自动接管，然后循环 sts2_wait 跟随游戏：\n" +
        "- phase=ai_decision 时用 sts2_decide 回答同一份 JSON；\n" +
        "- 战斗回合用 sts2_play_cards / sts2_end_turn；\n" +
        "- 地图、奖励、商店、事件、宝箱等界面用 sts2_choose / sts2_proceed / sts2_claim_rewards / sts2_select_cards；\n" +
        "一直打到本局结束（phase=game_over 或 menu）再总结收尾，中途不要停。";

    /// <summary>Quiet time after the AI joins before an invitation is offered (a manual session wins).</summary>
    private const int WarmupSeconds = 30;

    /// <summary>No dsh request for this long means nothing is actually driving the run.</summary>
    private const int DrivingIdleSeconds = 15;

    /// <summary>A claim that produced no activity for this long may be retried (creation failed).</summary>
    private const int ClaimExpirySeconds = 60;

    private const int MaxClaimsPerRun = 3;

    private static readonly Lock Sync = new();

    private static string _runToken = "";
    private static string _character = "";
    private static string _claimedToken = "";
    private static string _status = "";
    private static DateTime _joinedAtUtc;
    private static DateTime _claimedAtUtc;
    private static int _claims;

    /// <summary>An AI teammate joined: open a take-over window for this run.</summary>
    public static void OnAiPlayerJoined(string character)
    {
        lock (Sync)
        {
            _runToken = Guid.NewGuid().ToString("N");
            _character = character ?? "";
            _joinedAtUtc = DateTime.UtcNow;
            _claimedToken = "";
            _claimedAtUtc = DateTime.MinValue;
            _claims = 0;
            _status = AiLoc.T("settings.agent.takeover.status.waiting", "Waiting for dsh to claim the run.");
        }
    }

    /// <summary>Run is over: close the window.</summary>
    public static void OnRunEnded()
    {
        lock (Sync)
        {
            _runToken = "";
            _claimedToken = "";
            _status = AiLoc.T("settings.agent.takeover.status.ended", "The run is over.");
        }
    }

    /// <summary>
    /// Answers <c>sts2_takeover</c>. The text is JSON: either
    /// <c>{"take":true,"run_token","prompt","workspace","host","port","character"}</c> or
    /// <c>{"take":false,"reason"}</c>. A successful answer claims the run.
    /// </summary>
    public static string TryTakeoverJson()
    {
        static string No(string reason) =>
            new JsonObject { ["take"] = false, ["reason"] = reason }.ToJsonString(AgentJson.Options);

        AiSettings settings;
        try { settings = AiSettingsStore.Current; }
        catch { return No("settings_unavailable"); }

        if (!settings.AutoLaunchAgentSession)
            return No("disabled");

        if (!AgentBridgeService.AllowActions)
            return No("read_only");

        string runToken;
        string character;
        DateTime joinedAt;
        int claims;

        lock (Sync)
        {
            runToken = _runToken;
            character = _character;
            joinedAt = _joinedAtUtc;
            claims = _claims;
        }

        if (runToken.Length == 0)
            return No("no_ai_teammate");

        var now = DateTime.UtcNow;
        if ((now - joinedAt).TotalSeconds < WarmupSeconds)
            return No("warmup");

        if (AgentBridgeService.ClientIdleSeconds < DrivingIdleSeconds)
            return No("already_driving");

        if (claims >= MaxClaimsPerRun)
            return No("exhausted");

        lock (Sync)
        {
            // Re-read under the lock: two clients may be asking at the same time.
            if (_runToken != runToken)
                return No("run_changed");

            if (_claimedToken == runToken
                && (DateTime.UtcNow - _claimedAtUtc).TotalSeconds < ClaimExpirySeconds)
                return No("claimed");

            _claimedToken = runToken;
            _claimedAtUtc = DateTime.UtcNow;
            _claims++;
            _status = AiLoc.T("settings.agent.takeover.status.claimed", "Claimed by dsh (time {Count}).",
                ("Count", _claims.ToString()));
        }

        var workspace = ResolveWorkspace(settings.AgentLaunchWorkspace);
        var prompt = BuildPrompt(settings, character, workspace);

        MainFile.Logger.Info($"[Agent] 已把对局交给 dsh 认领：run={runToken[..8]} workspace={workspace}");
        AiLog($"dsh 认领对局（run {runToken[..8]}，工作区 {workspace}）");

        return new JsonObject
        {
            ["take"] = true,
            ["run_token"] = runToken,
            ["character"] = character,
            ["prompt"] = prompt,
            ["workspace"] = workspace,
            ["host"] = "127.0.0.1",
            ["port"] = Math.Clamp(settings.AgentBridgePort, 1024, 65535)
        }.ToJsonString(AgentJson.Options);
    }

    /// <summary>Status line for the settings page. 设置页状态。</summary>
    public static string Describe()
    {
        try
        {
            lock (Sync)
                return _status.Length == 0 ? AiLoc.T("settings.agent.takeover.idle", "Idle.") : _status;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Fills the prompt placeholders; the plugin sends the result as the first message.</summary>
    public static string BuildPrompt(AiSettings settings, string character, string workspace)
    {
        var template = (settings.AgentLaunchPrompt ?? "").Trim();
        if (template.Length == 0)
            template = DefaultPrompt;

        var token = (settings.AgentBridgeToken ?? "").Trim();
        var tokenClause = token.Length == 0
            ? ""
            : AiLoc.T("settings.agent.launch.prompt.token",
                "Access token: {Token} — put the same value in the dsh-sts2 plugin config.", ("Token", token));

        return template
            .Replace("{Host}", "127.0.0.1", StringComparison.Ordinal)
            .Replace("{Port}", Math.Clamp(settings.AgentBridgePort, 1024, 65535).ToString(), StringComparison.Ordinal)
            .Replace("{Token}", tokenClause, StringComparison.Ordinal)
            .Replace("{Workspace}", workspace ?? "", StringComparison.Ordinal)
            .Replace("{Character}", character ?? "", StringComparison.Ordinal);
    }

    /// <summary>Workspace the new conversation belongs to; falls back to the user profile.</summary>
    public static string ResolveWorkspace(string raw)
    {
        var workspace = (raw ?? "").Trim();
        if (workspace.Length > 0 && System.IO.Directory.Exists(workspace))
            return workspace;

        var fallback = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (workspace.Length > 0)
            MainFile.Logger.Warn($"[Agent] 工作区不存在，改用 {fallback}：{workspace}");

        return fallback;
    }

    private static void AiLog(string text)
    {
        try { AiTeammateManager.Instance?.AiLog?.Log("AGENT", text); }
        catch { }
    }
}
