using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace AiTeammate.AiTeammateCode.Compat;

/// <summary>
/// Multiplayer delivery of <see cref="AiNicknameMessage"/>. Registering is idempotent per net
/// service, so every run-start / lobby / AI-join hook can call <see cref="EnsureRegistered"/>.
/// 联机昵称同步（每个 NetService 只注册一次）。
/// </summary>
internal static class AiNicknameSync
{
    private static INetGameService? _registered;

    /// <summary>Binds the handler to the current run's net service. 注册昵称消息处理器。</summary>
    public static void EnsureRegistered()
    {
        EnsureRegistered(RunManager.Instance?.NetService);
    }

    /// <summary>Binds the handler to a specific service (the lobby's, before the run starts).</summary>
    public static void EnsureRegistered(INetGameService? net)
    {
        if (ReferenceEquals(net, _registered))
            return;

        var previous = _registered;
        _registered = net;

        if (previous != null)
        {
            try { previous.UnregisterMessageHandler<AiNicknameMessage>(HandleMessage); }
            catch (Exception ex) { MainFile.Logger.Warn($"[Nickname] 注销联机处理器失败: {ex.Message}"); }
        }

        if (net == null)
            return;

        try
        {
            net.RegisterMessageHandler<AiNicknameMessage>(HandleMessage);
            MainFile.Logger.Info($"[Nickname] 已在 {net.GetType().Name} 上注册昵称同步");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Nickname] 注册联机处理器失败: {ex.Message}");
        }
    }

    /// <summary>
    /// Host sends the current nickname once the AI player exists, so every client starts the run
    /// with the same name instead of its own local setting. 房主在 AI 入局时广播当前昵称。
    /// </summary>
    public static void OnAiPlayerJoined()
    {
        EnsureRegistered();

        if (RunManager.Instance?.NetService?.Type != NetGameType.Host)
            return;

        var nickname = AiNames.ConfiguredNickname;
        // Let the other clients finish their own AI detection / handler registration first.
        MainThread.Post(() => Broadcast(nickname));
    }

    /// <summary>Sends the nickname to the other clients; a no-op outside a live multiplayer run.</summary>
    public static void Broadcast(string? nickname)
    {
        EnsureRegistered();

        var net = RunManager.Instance?.NetService;
        if (net == null || !net.IsConnected)
            return;

        if (net.Type is NetGameType.None or NetGameType.Singleplayer or NetGameType.Replay)
            return;

        try
        {
            net.SendMessage(new AiNicknameMessage { nickname = nickname ?? string.Empty });
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Nickname] 广播昵称失败: {ex.Message}");
        }
    }

    private static void HandleMessage(AiNicknameMessage message, ulong senderId)
    {
        MainThread.Post(() => AiNames.ApplyRemote(message.nickname, senderId));
    }
}
