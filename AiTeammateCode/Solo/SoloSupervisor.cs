using Godot;

namespace AiTeammate.AiTeammateCode.Solo;

/// <summary>
/// Watches the run state and re-binds AI solo control when the normal hooks missed it.
/// "Continue" after restarting the game goes through a different load path than a fresh run
/// (<c>RunManager.SetUpSavedSingleplayer</c> → <c>NGame.LoadRun</c>), and the local net id /
/// run state come online at different moments, so a one-shot bind during loading can fail.
/// 单人爬塔看门狗：读档后若 AI 没有接管，每秒重试一次绑定。
/// </summary>
public sealed partial class SoloSupervisor : Node
{
    private const double TickSeconds = 1;

    private double _timer;

    /// <summary>Attaches one supervisor under the game root. 挂载看门狗。</summary>
    public static void EnsureAttached(Node gameRoot)
    {
        foreach (var child in gameRoot.GetChildren())
        {
            if (child is SoloSupervisor && GodotObject.IsInstanceValid(child))
                return;
        }

        gameRoot.AddChild(new SoloSupervisor { Name = "AiTeammateSoloSupervisor" });
    }

    public override void _Process(double delta)
    {
        _timer += delta;
        if (_timer < TickSeconds)
            return;

        _timer = 0;
        try
        {
            SoloRunController.SuperviseTick();
        }
        catch (System.Exception ex)
        {
            MainFile.Logger.Warn($"[Solo] supervisor tick failed: {ex.Message}");
        }
    }
}
