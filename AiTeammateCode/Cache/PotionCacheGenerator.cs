using MegaCrit.Sts2.Core.Models;

namespace AiTeammate.AiTeammateCode.Cache;

public class PotionCacheGenerator
{
    public Dictionary<string, PotionIndexEntry> GenerateIndex()
    {
        var entries = new Dictionary<string, PotionIndexEntry>();
        var allPotions = ModelDb.AllPotions;
        foreach (var potion in allPotions)
        {
            var id = potion.Id.Entry;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var name = PromptTextHelper.ResolveLocString(potion.Title, id);
            var description = PromptTextHelper.ResolveLocString(potion.DynamicDescription, "");
            entries[id] = new PotionIndexEntry
            {
                Name = name,
                Rarity = potion.Rarity.ToString(),
                OneLiner = ExtractOneLiner(description),
                Pool = potion.Pool.Id.Entry,
                PoolId = potion.Pool.Id.Entry,
                Usage = potion.Usage.ToString(),
                TargetType = potion.TargetType.ToString()
            };
        }
        return entries;
    }

    public Dictionary<string, PotionFullEntry> GenerateFull()
    {
        var entries = new Dictionary<string, PotionFullEntry>();
        var allPotions = ModelDb.AllPotions;
        foreach (var potion in allPotions)
        {
            var id = potion.Id.Entry;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var name = PromptTextHelper.ResolveLocString(potion.Title, id);
            entries[id] = new PotionFullEntry
            {
                Name = name,
                Rarity = potion.Rarity.ToString(),
                Description = PromptTextHelper.ResolveLocString(potion.DynamicDescription, ""),
                Pool = potion.Pool.Id.Entry,
                PoolId = potion.Pool.Id.Entry,
                Usage = potion.Usage.ToString(),
                TargetType = potion.TargetType.ToString()
            };
        }
        return entries;
    }

    private string ExtractOneLiner(string description)
    {
        var periodIdx = description.IndexOf('.');
        if (periodIdx > 0) return description.Substring(0, periodIdx + 1);
        return description.Length > 60 ? description.Substring(0, 60) + "..." : description;
    }
}
