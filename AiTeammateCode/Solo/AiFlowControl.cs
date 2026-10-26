namespace AiTeammate.AiTeammateCode.Solo;

/// <summary>
/// Pause gate for every automated action in solo mode. The player can interrupt the AI climb at any
/// time to talk to it: chat messages pause the flow while the conversation lasts, and the chat
/// panel's pause button holds it indefinitely. All solo automation waits on
/// <see cref="WaitWhilePausedAsync"/> or checks <see cref="IsPaused"/> between actions.
/// </summary>
public static class AiFlowControl
{
    private static readonly Lock Sync = new();
    private static bool _manualPaused;
    private static long _autoResumeAtTicks;
    private static int _published = -1;

    /// <summary>Raised whenever the effective pause state flips. 暂停状态变化。</summary>
    public static event Action<bool>? PausedChanged;

    public static bool ManualPaused
    {
        get { lock (Sync) return _manualPaused; }
    }

    public static bool IsPaused
    {
        get
        {
            lock (Sync)
            {
                return _manualPaused || DateTime.UtcNow.Ticks < _autoResumeAtTicks;
            }
        }
    }

    /// <summary>Toggles the sticky pause used by the chat panel button. 手动暂停/继续。</summary>
    public static void ToggleManualPause() => SetManualPaused(!ManualPaused);

    public static void SetManualPaused(bool paused)
    {
        lock (Sync)
        {
            _manualPaused = paused;
            if (paused)
                _autoResumeAtTicks = 0;
        }

        Publish();
    }

    /// <summary>
    /// Pauses the AI for a conversation window. Repeated calls extend the deadline, so a long
    /// exchange keeps the AI held until the human stops talking.
    /// </summary>
    public static void BeginConversationPause(TimeSpan window)
    {
        lock (Sync)
        {
            if (_manualPaused)
                return;

            var until = DateTime.UtcNow.Add(window).Ticks;
            if (until > _autoResumeAtTicks)
                _autoResumeAtTicks = until;
        }

        Publish();
    }

    /// <summary>Shortens the conversation pause so the AI resumes shortly after its reply lands.</summary>
    public static void EndConversationPauseSoon(TimeSpan delay)
    {
        lock (Sync)
        {
            if (_manualPaused)
                return;

            var until = DateTime.UtcNow.Add(delay).Ticks;
            if (_autoResumeAtTicks == 0 || until < _autoResumeAtTicks)
                _autoResumeAtTicks = until;
        }

        Publish();
    }

    public static void ClearConversationPause()
    {
        lock (Sync)
        {
            _autoResumeAtTicks = 0;
        }

        Publish();
    }

    /// <summary>Waits until the human lets the AI continue. 等待继续。</summary>
    public static async Task WaitWhilePausedAsync(CancellationToken ct = default)
    {
        while (IsPaused)
            await Task.Delay(200, ct);
    }

    private static void Publish()
    {
        var paused = IsPaused;
        var flag = paused ? 1 : 0;
        if (Interlocked.Exchange(ref _published, flag) != flag)
            PausedChanged?.Invoke(paused);
    }

    // The timeout-based conversation pause expires on its own; poll so listeners (and the combat
    // resume hook) observe that transition even when nothing is actively waiting.
    static AiFlowControl()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(400);
                Publish();
            }
        });
    }
}
