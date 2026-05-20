using MegaCrit.Sts2.Core.Models;

namespace AiTeammate.AiTeammateCode.Cache;

internal static class ContentCacheMetadata
{
    public static string ResolveCardCharacterName(CardModel card)
    {
        return ModelDb.AllCharacters
            .FirstOrDefault(character => character.CardPool.Id == card.Pool.Id)?
            .Id.Entry ?? "";
    }

    public static string ResolveCardCharacterId(CardModel card)
    {
        return ModelDb.AllCharacters
            .FirstOrDefault(character => character.CardPool.Id == card.Pool.Id)?
            .Id.Entry ?? "";
    }

    public static string ResolvePoolName(CardPoolModel pool)
    {
        return pool.Title;
    }

    public static string ResolvePoolName(RelicModel relic)
    {
        return relic.Pool.Id.Entry;
    }

    public static string ResolvePoolName(PotionModel potion)
    {
        return potion.Pool.Id.Entry;
    }

    public static string FormatCardCost(CardModel card)
    {
        var energyCost = card.EnergyCost.CostsX ? "X费" : $"{card.EnergyCost.Canonical}费";

        if (card.HasStarCostX)
            return $"{energyCost}/X辉星";

        if (card.CanonicalStarCost >= 0)
            return $"{energyCost}/{card.CanonicalStarCost}辉星";

        return energyCost;
    }
}
