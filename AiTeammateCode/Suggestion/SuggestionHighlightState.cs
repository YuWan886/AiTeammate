using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using STS2RitsuLib.Scaffolding.Cards.HandOutline;

namespace AiTeammate.AiTeammateCode.Suggestion;

/// <summary>
/// Shared highlight state for AI card suggestions. The RitsuLib hand-outline rule reads this on
/// every hand update; the highlighter pushes changes here and asks the hand to refresh.
/// </summary>
public static class SuggestionHighlightState
{
    private static readonly Lock Sync = new();
    private static readonly Dictionary<string, Color> Colors = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Applied = new(StringComparer.Ordinal);

    public static bool Registered { get; private set; }

    /// <summary>Must be called from the mod initializer, before ModelDb initializes.</summary>
    public static void Register()
    {
        if (Registered)
            return;

        try
        {
            ModCardHandOutlineRegistry.Register(
                typeof(CardModel),
                ModCardHandOutlineRules.Switch(
                    card => TryGetColor(card, out var color) ? color : null,
                    priority: 100,
                    visibleWhenUnplayable: true,
                    refreshEveryFrame: false));

            Registered = true;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Suggestions] 注册手牌描边失败: {ex.Message}");
        }
    }

    public static bool TryGetColor(CardModel card, out Color color)
    {
        color = default;
        var id = card.Id.Entry;
        if (string.IsNullOrEmpty(id))
            return false;

        lock (Sync)
        {
            return Colors.TryGetValue(id, out color);
        }
    }

    public static void SetFromSuggestions(IReadOnlyList<CardSuggestion> suggestions, Player player, int maxHighlights)
    {
        var hand = player.PlayerCombatState?.Hand.Cards;
        if (hand == null)
        {
            Clear();
            return;
        }

        var colors = new Dictionary<string, Color>(StringComparer.Ordinal);
        var applied = 0;

        foreach (var suggestion in suggestions)
        {
            if (string.IsNullOrWhiteSpace(suggestion.CardName))
                continue;

            if (suggestion.IsAvoid)
            {
                var avoided = FindCard(hand, suggestion.CardName);
                if (avoided != null)
                    colors[avoided.Id.Entry] = new Color(1f, 0.2f, 0.2f);
                continue;
            }

            if (applied >= Math.Max(0, maxHighlights))
                continue;

            var card = FindCard(hand, suggestion.CardName);
            if (card == null)
                continue;

            colors[card.Id.Entry] = suggestion.GlowColor switch
            {
                "green" => new Color(0f, 1f, 0.3f),
                "red" => new Color(1f, 0.2f, 0.2f),
                _ => new Color(1f, 0.84f, 0f)
            };
            applied++;
        }

        lock (Sync)
        {
            Colors.Clear();
            foreach (var (key, value) in colors)
                Colors[key] = value;
        }

        Refresh();
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Colors.Clear();
        }

        Refresh();
    }

    private static CardModel? FindCard(IReadOnlyList<CardModel> hand, string name)
    {
        return hand.FirstOrDefault(card =>
            (card.Title ?? "").Contains(name, StringComparison.OrdinalIgnoreCase)
            || card.Id.Entry.Contains(name, StringComparison.OrdinalIgnoreCase)
            || name.Contains(card.Title ?? "", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Re-applies the current colors to the live hand. Must run on the main thread.</summary>
    public static void Refresh()
    {
        var hand = NPlayerHand.Instance;
        if (hand == null || !GodotObject.IsInstanceValid(hand))
            return;

        HashSet<string> previous;
        HashSet<string> currentColors;
        lock (Sync)
        {
            currentColors = new HashSet<string>(Colors.Keys, StringComparer.Ordinal);
            previous = new HashSet<string>(Applied, StringComparer.Ordinal);
        }

        var applied = new HashSet<string>(StringComparer.Ordinal);

        foreach (var holder in hand.Holders)
        {
            if (holder == null || !GodotObject.IsInstanceValid(holder))
                continue;

            var model = holder.CardNode?.Model;
            if (model == null)
                continue;

            var id = model.Id.Entry;
            if (currentColors.Contains(id) && ModCardHandOutlineRegistry.TryRefreshOutlineForHolder(holder))
            {
                applied.Add(id);
            }
            else if (previous.Contains(id))
            {
                HideOutline(holder);
            }
        }

        lock (Sync)
        {
            Applied.Clear();
            Applied.UnionWith(applied);
        }
    }

    private static void HideOutline(MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder holder)
    {
        try
        {
            var highlight = holder.CardNode?.CardHighlight;
            if (highlight != null && GodotObject.IsInstanceValid(highlight))
                highlight.AnimHide();
        }
        catch
        {
            // Cosmetic only.
        }
    }
}
