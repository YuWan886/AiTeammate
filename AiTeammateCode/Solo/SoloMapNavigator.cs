using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using AiTeammate.AiTeammateCode.Decision;

namespace AiTeammate.AiTeammateCode.Solo;

/// <summary>
/// Picks the next map node for the solo AI climb. Triggered every time the map screen opens,
/// because in solo the local player (driven by the AI) is the one who has to vote.
/// </summary>
public static class SoloMapNavigator
{
    private static CancellationTokenSource? _cts;
    private static bool _navigationInFlight;
    private static int _navigationFromFloor = -1;

    public static void OnMapOpened()
    {
        if (!SoloRunController.IsActive)
            return;

        // NMapScreen.Open() early-returns when the map is already open, but a Harmony postfix on it
        // still runs. Anything that re-opens the map (a room's proceed button staying clickable
        // underneath it, the top-bar map button) would restart the picker — cancelling the request
        // in flight — so the AI never finishes a decision and the run sits on the map forever.
        // Only a trigger from a new floor counts as a new map.
        var floor = RunManager.Instance?.DebugOnlyGetState()?.TotalFloor ?? -1;
        if (_navigationInFlight && floor == _navigationFromFloor)
            return;

        _cts?.Cancel();
        // Not disposed on purpose: a navigation that overlaps this one still holds the old token.
        _cts = new CancellationTokenSource();
        _navigationInFlight = true;
        _navigationFromFloor = floor;
        _ = NavigateAsync(_cts, _cts.Token);
    }

    private static async Task NavigateAsync(CancellationTokenSource owner, CancellationToken ct)
    {
        try
        {
            // Let the map finish opening and its points settle.
            await Task.Delay(1200, ct);
            await AiFlowControl.WaitWhilePausedAsync(ct);

            var screen = NMapScreen.Instance;
            if (screen == null || !Godot.GodotObject.IsInstanceValid(screen) || !screen.IsOpen)
                return;

            var points = UiHelper.FindAll<NMapPoint>(screen)
                .Where(p => Godot.GodotObject.IsInstanceValid(p)
                            && p.IsEnabled
                            && p.Point != null
                            && p.State == MapPointState.Travelable)
                .ToList();

            if (points.Count == 0)
            {
                MainFile.Logger.Info("[Solo] map has no travelable points");
                return;
            }

            var manager = AiTeammateManager.Instance;
            var aiPlayer = manager.AiPlayer;
            if (aiPlayer == null)
                return;

            var descriptions = points.Select(p => ContextBuilder.DescribeMapPoint(p.Point)).ToList();
            var (choice, reason) = await manager.DecisionService.DecideMapNavigationAsync(aiPlayer, descriptions, ct);

            await AiFlowControl.WaitWhilePausedAsync(ct);

            if (!screen.IsOpen || !SoloRunController.IsActive)
                return; // the human took over, or the run/mode ended, in the meantime

            if (choice < 0 || choice >= points.Count)
                choice = PickFallback(points, aiPlayer);
            choice = Math.Clamp(choice, 0, points.Count - 1);

            var chosen = points[choice];
            if (!string.IsNullOrWhiteSpace(reason))
                manager.AiLog.Log("AI_THINK", $"[地图] {reason}");
            manager.AiLog.Log("AI_ACTION", $"选路: {descriptions[choice]}");

            MainFile.Logger.Info($"[Solo] map pick {choice}/{points.Count}: {descriptions[choice]} | {reason}");
            screen.OnMapPointSelectedLocally(chosen);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Solo] map navigation failed: {ex.Message}");
        }
        finally
        {
            // A newer navigation may have taken over; only its owner clears the guard.
            if (ReferenceEquals(_cts, owner))
            {
                _navigationInFlight = false;
                _navigationFromFloor = -1;
            }
        }
    }

    private static int PickFallback(List<NMapPoint> points, MegaCrit.Sts2.Core.Entities.Players.Player player)
    {
        var hpPct = player.Creature != null && player.Creature.MaxHp > 0
            ? player.Creature.CurrentHp * 100 / player.Creature.MaxHp
            : 100;

        MapPointType preferred;
        if (hpPct <= 45)
            preferred = MapPointType.RestSite;
        else if (hpPct >= 75)
            preferred = MapPointType.Elite;
        else
            preferred = MapPointType.Monster;

        var index = points.FindIndex(p => p.Point.PointType == preferred);
        if (index >= 0)
            return index;

        index = points.FindIndex(p => p.Point.PointType == MapPointType.Monster);
        if (index >= 0)
            return index;

        return System.Random.Shared.Next(points.Count);
    }
}
