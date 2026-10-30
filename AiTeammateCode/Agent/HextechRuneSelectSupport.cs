using System.Reflection;
using MegaCrit.Sts2.Core.Models;

namespace AiTeammate.AiTeammateCode.Agent;

/// <summary>
/// Drives the HextechRunes (ARAM: Mayhem) rune-selection overlay from the dsh bridge.
///
/// The screen belongs to another mod (a Steam Workshop bundle with per-game-version DLLs), so everything
/// here is reached by reflection. Hextech ships a presenter surface on purpose
/// (<c>PresenterChoose</c> / <c>PresenterConfirmEnemyOnly</c>): committing through it skips the screen's
/// anti-double-click guard and its optional "select, then confirm" preference, and it still works when a
/// third-party presenter owns the UI.
/// </summary>
internal static class HextechRuneSelectSupport
{
    public const string ScreenTypeName = "HextechRunes.HextechRuneSelectionScreen";

    private const BindingFlags InstanceFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>True when the overlay on top is Hextech's rune picker.</summary>
    public static bool IsScreen(object? overlay) =>
        overlay != null && overlay.GetType().FullName == ScreenTypeName;

    public static bool IsLocked(object screen) => Read<bool>(screen, "PresenterLocked");

    public static bool IsEnemyOnly(object screen) => Read<bool>(screen, "PresenterEnemyOnly");

    /// <summary>Rune candidates; empty when the screen only asks for a confirmation.</summary>
    public static List<RelicModel> Options(object screen)
    {
        var relics = Read<IReadOnlyList<RelicModel>>(screen, "CurrentRelics");
        return relics == null ? new List<RelicModel>() : relics.Where(r => r != null).ToList();
    }

    /// <summary>Extra footer text Hextech shows under a candidate (may be empty).</summary>
    public static string FooterText(object screen, int slot) =>
        TryInvoke(screen, "PresenterOptionFooter", out var footer, slot) && footer is string text ? text : "";

    /// <summary>Rarity tier name of a candidate (may be empty).</summary>
    public static string RarityText(object screen, int slot) =>
        TryInvoke(screen, "PresenterOptionRarity", out var rarity, slot) && rarity != null
            ? rarity.ToString() ?? ""
            : "";

    /// <summary>
    /// True when the screen has nothing to pick and only waits for its confirm/continue button: either the
    /// act ran out of rune options ("continue only") or the enemy-hex controls are what gets confirmed.
    /// </summary>
    public static bool CanConfirmEmpty(object screen) =>
        Read<bool>(screen, "_continueOnly") || Read<bool>(screen, "PresenterEnemyControls");

    /// <summary>Commits the rune in <paramref name="slot"/>.</summary>
    public static bool Choose(object screen, int slot) =>
        TryInvoke(screen, "PresenterChoose", out _, slot);

    /// <summary>Confirms a screen that has no rune candidates (continue / enemy-only).</summary>
    public static bool ConfirmEmpty(object screen) =>
        TryInvoke(screen, "PresenterConfirmEnemyOnly", out _);

    // ---- Reflection helpers ---------------------------------------------------------------

    private static T Read<T>(object instance, string member)
    {
        var type = instance.GetType();

        var property = type.GetProperty(member, InstanceFlags);
        if (property != null)
            return property.GetValue(instance) is T propertyValue ? propertyValue : default!;

        var field = type.GetField(member, InstanceFlags);
        if (field != null)
            return field.GetValue(instance) is T fieldValue ? fieldValue : default!;

        return default!;
    }

    private static bool TryInvoke(object instance, string methodName, out object? result, params object?[] args)
    {
        result = null;
        try
        {
            var method = instance.GetType()
                .GetMethods(InstanceFlags)
                .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == args.Length);
            if (method == null) return false;

            result = method.Invoke(instance, args);
            return true;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[HextechRunes] 调用 {methodName} 失败: {ex.GetBaseException().Message}");
            return false;
        }
    }
}
