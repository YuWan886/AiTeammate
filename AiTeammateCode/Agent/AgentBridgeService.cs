using Godot;
using MegaCrit.Sts2.Core.Nodes;
using AiTeammate.AiTeammateCode.Settings;

namespace AiTeammate.AiTeammateCode.Agent;

/// <summary>
/// Owns the external-agent bridge lifecycle. Polled once per second by
/// <see cref="AgentBridgeSupervisor"/>; settings changes restart the server.
/// 外部 Agent 桥接的启动/停止与状态。
/// </summary>
internal static class AgentBridgeService
{
    private static readonly Lock Sync = new();
    private static AgentBridgeServer? _server;
    private static string _fingerprint = "";
    private static string _lastError = "";
    private static int _port;
    private static bool _allowActions = true;

    public static bool AllowActions
    {
        get { lock (Sync) return _allowActions; }
    }

    public static int Port
    {
        get { lock (Sync) return _port; }
    }

    public static bool IsRunning
    {
        get { lock (Sync) return _server != null; }
    }

    public static string LastError
    {
        get { lock (Sync) return _lastError; }
    }

    /// <summary>Attached dsh clients on the live server. 已连接的 dsh 客户端数。</summary>
    public static int ConnectedClients
    {
        get { lock (Sync) return _server?.ConnectedClients ?? 0; }
    }

    /// <summary>
    /// True while an external agent is attached with action rights. The mod then lets it act as the
    /// brain of the AI player (see <see cref="AgentDecisionBroker"/>) instead of the LLM backend.
    /// 对接了 dsh：AI 的决策交给 dsh。
    /// </summary>
    public static bool ExternalAgentDriving
    {
        get
        {
            lock (Sync)
                return _server != null && _allowActions && (_server?.ConnectedClients ?? 0) > 0;
        }
    }

    /// <summary>
    /// Raised on the main thread whenever the attached client count changes, so UI that marks
    /// "dsh is driving" can follow connect/disconnect without polling. 客户端数量变化事件。
    /// </summary>
    public static event Action<int>? ClientCountChanged;

    private static int _lastClientCount;

    /// <summary>Fans a count transition out once; safe to call every tick. 转发客户端数量变化。</summary>
    private static void NotifyClientCount()
    {
        int clients;
        try { clients = ConnectedClients; }
        catch { return; }

        if (clients == _lastClientCount)
            return;

        _lastClientCount = clients;

        // No client left: stop parking AI decisions, otherwise the AI would keep waiting for an
        // answer that can no longer arrive.
        if (clients == 0)
        {
            try { AgentDecisionBroker.Abort("dsh client disconnected"); }
            catch (Exception ex) { MainFile.Logger.Warn($"[Agent] abort pending decision failed: {ex.Message}"); }
        }

        try { ClientCountChanged?.Invoke(clients); }
        catch (Exception ex) { MainFile.Logger.Warn($"[Agent] client-count listener failed: {ex.Message}"); }
    }

    /// <summary>Attaches the settings watchdog under the game root. 挂载看门狗。</summary>
    public static void EnsureAttached(Node gameRoot)
    {
        foreach (var child in gameRoot.GetChildren())
        {
            if (child is AgentBridgeSupervisor && GodotObject.IsInstanceValid(child))
                return;
        }

        gameRoot.AddChild(new AgentBridgeSupervisor { Name = "AiTeammateAgentBridge" });
    }

    /// <summary>Applies the live settings. Safe to call every second. 应用设置。</summary>
    public static void SuperviseTick()
    {
        // Runs before the settings fingerprint short-circuit: the client count moves on its own
        // timeline (dsh connecting/disconnecting), not only when settings change.
        NotifyClientCount();

        AiSettings settings;
        try { settings = AiSettingsStore.Current; }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Agent] settings unavailable: {ex.Message}");
            return;
        }

        var enabled = settings.EnableAgentBridge;
        var port = Math.Clamp(settings.AgentBridgePort, 1024, 65535);
        var token = settings.AgentBridgeToken ?? "";
        var allowActions = settings.AgentBridgeAllowActions;
        var fingerprint = $"{enabled}|{port}|{token}|{allowActions}";

        lock (Sync)
        {
            _port = port;
            _allowActions = allowActions;

            if (fingerprint == _fingerprint)
                return;

            // The settings page can be opened before NGame exists. Starting the TCP server then
            // would let a bridge thread reach game code before MainThread captured its context.
            if (enabled && NGame.Instance == null)
            {
                _lastError = "The game is not ready yet.";
                _fingerprint = "";
                StopLocked();
                return;
            }

            _fingerprint = fingerprint;

            StopLocked();

            if (!enabled)
            {
                _lastError = "";
                return;
            }

            var server = new AgentBridgeServer(port, token, allowActions);
            try
            {
                server.Start();
                _server = server;
                _lastError = "";
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                try { server.Dispose(); } catch { }
                MainFile.Logger.Warn($"[Agent] failed to start bridge on 127.0.0.1:{port}: {ex.Message}");
            }
        }
    }

    /// <summary>Forces a stop/start cycle (settings page button). 重新启动桥接。</summary>
    public static void Restart()
    {
        lock (Sync)
            _fingerprint = "";
        SuperviseTick();
    }

    /// <summary>Stops the server when the game is shutting down. 关闭桥接。</summary>
    public static void Shutdown()
    {
        lock (Sync)
        {
            _fingerprint = "";
            StopLocked();
        }
    }

    private static void StopLocked()
    {
        if (_server == null)
            return;

        try { _server.Dispose(); } catch { }
        _server = null;
        MainFile.Logger.Info("[Agent] bridge stopped");
    }
}
