using MegaCrit.Sts2.Core.Models;
using AiTeammate.AiTeammateCode.Localization;

namespace AiTeammate.AiTeammateCode.Cache;

internal static class ContentCacheMetadata
{
    public static string FormatCardCost(CardModel card)
    {
        var energyCost = card.EnergyCost.CostsX
            ? AiLoc.Pick("X费", "X Energy")
            : $"{card.EnergyCost.Canonical}{AiLoc.Pick("费", " Energy")}";

        if (card.HasStarCostX)
            return $"{energyCost}/X{AiLoc.Pick("辉星", " Star")}";

        if (card.CanonicalStarCost >= 0)
            return $"{energyCost}/{card.CanonicalStarCost}{AiLoc.Pick("辉星", " Star")}";

        return energyCost;
    }
}
