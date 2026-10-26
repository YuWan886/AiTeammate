using Godot;

namespace AiTeammate.AiTeammateCode.Compat;

/// <summary>
/// Captures the Godot main-thread <see cref="SynchronizationContext"/> so background work can
/// marshal back without touching game nodes from a thread-pool thread.
/// </summary>
public static class MainThread
{
    private static SynchronizationContext? _context;

    /// <summary>Called from NGame._Ready (main thread). 在主线程捕获同步上下文。</summary>
    public static void Capture() => _context ??= SynchronizationContext.Current;

    /// <summary>True when the calling thread is the Godot main thread (or no context was captured yet).</summary>
    public static bool IsMainThread
    {
        get
        {
            var context = _context;
            return context == null || ReferenceEquals(SynchronizationContext.Current, context);
        }
    }

    public static void Post(Action action)
    {
        if (action == null)
            return;

        var context = _context;
        if (context != null)
        {
            context.Post(static state => ((Action)state!)(), action);
            return;
        }

        // No captured context yet (e.g. settings page opened from the main menu before NGame):
        // hop to the next frame, which is the Godot main thread.
        Callable.From(action).CallDeferred();
    }

    /// <summary>
    /// Runs <paramref name="func"/> on the main thread and waits for its result. Used for game state
    /// reads that must not happen concurrently with the main thread — most importantly the
    /// localization formatter: <c>SmartFormat</c> keeps a shared object pool, so formatting a card
    /// description from a thread-pool thread while the UI formats the same table corrupts it
    /// ("Collection was modified" / "Format string is missing a closing brace").
    /// Calls made on the main thread run inline. Throws <see cref="TimeoutException"/> when the main
    /// thread does not pick the work up in time; callers should treat that as "no text available".
    /// </summary>
    public static T Invoke<T>(Func<T> func, int timeoutMs = 8000)
    {
        ArgumentNullException.ThrowIfNull(func);

        if (IsMainThread)
            return func();

        var context = _context;
        if (context == null)
            return func();

        T result = default!;
        Exception? failure = null;
        // Deliberately not disposed: if the main thread picks the work up after we time out, the
        // callback still touches it. The callback holds the only long-lived reference.
        var completed = new ManualResetEventSlim(false);

        context.Post(_ =>
        {
            try { result = func(); }
            catch (Exception ex) { failure = ex; }
            finally { completed.Set(); }
        }, null);

        if (!completed.Wait(timeoutMs))
            throw new TimeoutException("Main thread did not run the requested work in time.");

        if (failure != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

        return result;
    }

    /// <summary>Runs <paramref name="action"/> on the main thread and waits for it to finish.</summary>
    public static void Run(Action action) => Invoke(() => { action(); return true; });
}
