using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Localization.DynamicVars;

namespace AiTeammate.AiTeammateCode;

internal static class PromptTextHelper
{
    private static readonly Regex BbCodeRegex = new(@"\[.*?\]", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex ResourcePathRegex = new(@"res://\S+", RegexOptions.Compiled);
    private static readonly Regex PlaceholderRegex = new(@"\{[^{}\r\n]+\}", RegexOptions.Compiled);
    private static readonly Regex PlaceholderRootRegex = new(@"\{([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
    private static readonly Regex PlaceholderTokenRegex = new(@"\{([A-Za-z_][A-Za-z0-9_-]*)[^{}\r\n]*\}", RegexOptions.Compiled);
    private static readonly string[] SuspiciousSuffixes =
    {
        "获得", "失去", "造成", "给予", "抽", "生成", "召唤", "保留", "打出", "获得在下个回合获得"
    };

    public static string ResolveLocString(LocString? loc, string fallback = "?")
    {
        if (loc == null) return fallback;

        var raw = TryGet(() => loc.GetRawText());
        if (!ShouldAttemptFormatting(loc, raw))
            return PickBestText(raw, null, fallback: fallback);

        var formatted = TryGet(() => loc.GetFormattedText());
        return PickBestText(formatted, raw, fallback);
    }

    public static string ResolveCardDescription(CardModel? card, PileType pile = PileType.Hand, string fallback = "")
    {
        if (card == null) return fallback;

        var pileDesc = TryGet(() => card.GetDescriptionForPile(pile));
        var offlineFormatted = TryGet(() => FormatCardDescriptionOffline(card, card.IsUpgraded));
        var raw = TryGet(() => card.Description?.GetRawText());
        var rawWithVars = TryGet(() => ApplyVariableFallbacks(card, card.IsUpgraded));
        return PickBestText(pileDesc, offlineFormatted, rawWithVars, raw, fallback);
    }

    public static string CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var cleaned = BbCodeRegex.Replace(text, "");
        cleaned = ResourcePathRegex.Replace(cleaned, "");
        cleaned = cleaned.Replace("\r", "");
        cleaned = WhitespaceRegex.Replace(cleaned, " ").Trim();
        return cleaned;
    }

    public static string Truncate(string text, int maxLen)
    {
        if (maxLen <= 0 || text.Length <= maxLen) return text;
        return text[..maxLen] + "...";
    }

    private static string PickBestText(string? primary, string? secondary, string? tertiary = null, string? quaternary = null, string fallback = "?")
    {
        var candidates = new[] { primary, secondary, tertiary, quaternary }
            .Select(CleanText)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        if (candidates.Count == 0) return fallback;

        var resolved = candidates.FirstOrDefault(s => !PlaceholderRegex.IsMatch(s) && !LooksMalformedCardText(s));
        if (!string.IsNullOrWhiteSpace(resolved))
            return resolved;

        resolved = candidates.FirstOrDefault(s => !PlaceholderRegex.IsMatch(s));
        return resolved ?? candidates[0];
    }

    private static bool LooksMalformedCardText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        if (PlaceholderRegex.IsMatch(text))
            return true;

        return SuspiciousSuffixes.Any(suffix => text.EndsWith(suffix, StringComparison.Ordinal));
    }

    private static bool ShouldAttemptFormatting(LocString loc, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || loc.Variables.Count == 0)
            return false;

        var placeholderRoots = PlaceholderRootRegex.Matches(raw)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (placeholderRoots.Count == 0)
            return false;

        return placeholderRoots.All(name => loc.Variables.ContainsKey(name));
    }

    private static string? TryGet(Func<string?> getter)
    {
        try { return getter(); }
        catch { return null; }
    }

    private static string FormatCardDescriptionOffline(CardModel card, bool upgraded)
    {
        var description = new LocString("cards", card.Id.Entry + ".description");
        card.DynamicVars.AddTo(description);
        description.Add(new IfUpgradedVar(upgraded ? UpgradeDisplay.Upgraded : UpgradeDisplay.Normal));
        description.Add("OnTable", true);
        description.Add("InCombat", false);
        description.Add("IsTargeting", false);
        description.Add("TargetType", card.TargetType.ToString());
        description.Add("energyPrefix", "");
        description.Add("singleStarIcon", "辉星");

        foreach (var variable in description.Variables)
        {
            if (variable.Value is EnergyVar energyVar)
                energyVar.ColorPrefix = "";
        }

        return description.GetFormattedText();
    }

    private static string ApplyVariableFallbacks(CardModel card, bool upgraded)
    {
        var description = new LocString("cards", card.Id.Entry + ".description");
        card.DynamicVars.AddTo(description);
        description.Add(new IfUpgradedVar(upgraded ? UpgradeDisplay.Upgraded : UpgradeDisplay.Normal));
        description.Add("OnTable", true);
        description.Add("InCombat", false);
        description.Add("IsTargeting", false);
        description.Add("TargetType", card.TargetType.ToString());
        description.Add("energyPrefix", "");
        description.Add("singleStarIcon", "辉星");

        var raw = description.GetRawText();
        var result = PlaceholderTokenRegex.Replace(raw, match =>
        {
            var key = match.Groups[1].Value;
            if (!description.Variables.TryGetValue(key, out var value))
                return match.Value;

            return value switch
            {
                LocString locString => locString.GetFormattedText(),
                IConvertible convertible => convertible.ToString(null) ?? match.Value,
                _ => value.ToString() ?? match.Value
            };
        });

        return result;
    }
}
