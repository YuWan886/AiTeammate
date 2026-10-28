using Godot;

namespace AiTeammate.AiTeammateCode.Agent;

/// <summary>
/// Applies the external-agent settings once per second. Attached to the game root so it survives
/// room changes; the actual TCP server lives in <see cref="AgentBridgeService"/>.
/// 外部 Agent 设置看门狗。
/// </summary>
public sealed partial class AgentBridgeSupervisor : Node
{
    private const double TickSeconds = 1;

    private double _timer;

    public override void _Ready()
    {
        try { AgentBridgeService.SuperviseTick(); }
        catch (Exception ex) { MainFile.Logger.Warn($"[Agent] supervisor init failed: {ex.Message}"); }
    }

    public override void _Process(double delta)
    {
        _timer += delta;
        if (_timer < TickSeconds)
            return;

        _timer = 0;
        try
        {
            AgentBridgeService.SuperviseTick();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Agent] supervisor tick failed: {ex.Message}");
        }
    }

    public override void _ExitTree()
    {
        try { AgentBridgeService.Shutdown(); } catch { }
    }
}
