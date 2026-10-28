using Godot;
using MegaCrit.Sts2.Core.Nodes;
using AiTeammate.AiTeammateCode.Patches;

namespace AiTeammate.AiTeammateCode.Bootstrap;

/// <summary>
/// Runs the "game is ready" initialization without depending on <c>NGame._Ready</c>.
///
/// <para>
/// Mods are loaded from the game's async startup (<c>OneTimeInitialization.ExecuteVeryEarly</c>,
/// awaited inside <c>NGame._EnterTree</c> → <c>GameStartupWrapper</c>), which happens <b>after</b>
/// the main scene already entered the tree and ran <c>_Ready</c>. So the <see cref="NGamePatch"/>
/// postfix on <c>NGame._Ready</c> never fires on a real launch: the manager, the solo-climb
/// supervisor, the danmaku overlay and the dsh bridge all stayed dead until something else called
/// <see cref="NGamePatch.RefreshChatUi"/> — normally only the settings page button. That is why
/// "Continue" after restarting the game came back with no AI climbing.
/// 不依赖 NGame._Ready 的启动初始化：mod 载入时主场景的 _Ready 早已跑完。
/// </para>
/// </summary>
public static class ModBootstrap
{
    private const string NodeName = "AiTeammateBootstrap";

    private static bool _started;

    /// <summary>
    /// Attaches the readiness poller under the scene tree root. Called once from mod init.
    /// 挂载就绪轮询节点（mod 初始化时调用一次）。
    /// </summary>
    public static void Start()
    {
        if (_started)
            return;

        if (Engine.GetMainLoop() is not SceneTree tree)
            return;

        var root = tree.Root;
        if (root == null || !GodotObject.IsInstanceValid(root))
            return;

        _started = true;

        foreach (var child in root.GetChildren())
        {
            if (child is ModBootstrapNode && GodotObject.IsInstanceValid(child))
                return;
        }

        // Deferred: mod init can still be inside the scene tree's own setup pass.
        var node = new ModBootstrapNode { Name = NodeName };
        root.CallDeferred(Node.MethodName.AddChild, node);
    }
}

/// <summary>
/// Polls until <c>NGame</c> exists, runs the ready-time initialization once, then keeps retrying the
/// content cache until <c>ModelDb</c> can build it. Cheap while idle (0.5s, then 5s).
/// 就绪轮询：初始化管理器、挂载看门狗、补建内容缓存。
/// </summary>
internal sealed partial class ModBootstrapNode : Node
{
    private const double ReadyTickSeconds = 0.5;
    private const double CacheRetrySeconds = 5;
    private const double CacheRetryLimitSeconds = 120;

    private double _timer;
    private double _cacheElapsed;
    private bool _runtimeReady;
    private bool _cacheWarned;

    public override void _Process(double delta)
    {
        _timer += delta;
        if (_timer < (_runtimeReady ? CacheRetrySeconds : ReadyTickSeconds))
            return;

        _timer = 0;

        if (!_runtimeReady)
        {
            if (!TryStartRuntime())
                return;

            _runtimeReady = true;
            return;
        }

        RetryContentCache();
    }

    /// <summary>Runs the ready-time init (manager + supervisors + chat panel). 运行就绪初始化。</summary>
    private bool TryStartRuntime()
    {
        var game = NGame.Instance;
        if (game == null || !GodotObject.IsInstanceValid(game) || !game.IsInsideTree())
            return false;

        try
        {
            // Exactly what the (unreachable) NGame._Ready postfix would have done.
            NGamePatch.AfterGameReady(game);
        }
        catch (Exception ex)
        {
            // LocManager/ModelDb come online a few frames after mods load; retry quietly.
            MainFile.Logger.Info($"[Bootstrap] 游戏尚未就绪，稍后重试: {ex.Message}");
            return false;
        }

        if (!AiTeammateManager.Instance.IsInitialized)
            return false;

        MainFile.Logger.Info("[Bootstrap] 运行时已就绪（管理器/看门狗/聊天面板）");
        return true;
    }

    /// <summary>Builds the content cache once ModelDb exists. 补建内容缓存。</summary>
    private void RetryContentCache()
    {
        var cache = AiTeammateManager.Instance.CacheService;
        if (cache == null || cache.IsLoaded)
        {
            SetProcess(false);
            return;
        }

        _cacheElapsed += CacheRetrySeconds;
        if (_cacheElapsed > CacheRetryLimitSeconds)
        {
            MainFile.Logger.Warn("[Bootstrap] 内容缓存仍未就绪，停止后台重试");
            SetProcess(false);
            return;
        }

        try
        {
            cache.EnsureLoaded();
        }
        catch (Exception ex)
        {
            if (!_cacheWarned)
            {
                _cacheWarned = true;
                MainFile.Logger.Warn($"[Bootstrap] 内容缓存构建失败，稍后重试: {ex.Message}");
            }
        }

        if (cache.IsLoaded)
            SetProcess(false);
    }
}
