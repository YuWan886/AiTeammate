using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace AiTeammate.AiTeammateCode.Cache;

public class RelicCacheGenerator
{
    public Dictionary<string, RelicIndexEntry> GenerateIndex()
    {
        var entries = new Dictionary<string, RelicIndexEntry>();
        var allRelics = ModelDb.AllRelics;
        foreach (var relic in allRelics)
        {
            var id = relic.Id.Entry;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var name = PromptTextHelper.ResolveLocString(relic.Title, id);
            var description = PromptTextHelper.ResolveLocString(relic.DynamicDescription, "");
            entries[id] = new RelicIndexEntry
            {
                Name = name,
                Tier = relic.Rarity.ToString(),
                OneLiner = ExtractOneLiner(description),
                Pool = relic.Pool.Id.Entry,
                PoolId = relic.Pool.Id.Entry
            };
        }
        return entries;
    }

    public Dictionary<string, RelicFullEntry> GenerateFull()
    {
        var entries = new Dictionary<string, RelicFullEntry>();
        var allRelics = ModelDb.AllRelics;
        foreach (var relic in allRelics)
        {
            var id = relic.Id.Entry;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var name = PromptTextHelper.ResolveLocString(relic.Title, id);
            entries[id] = new RelicFullEntry
            {
                Name = name,
                Tier = relic.Rarity.ToString(),
                Description = PromptTextHelper.ResolveLocString(relic.DynamicDescription, ""),
                FlavorText = PromptTextHelper.ResolveLocString(relic.Flavor, ""),
                Pool = relic.Pool.Id.Entry,
                PoolId = relic.Pool.Id.Entry,
                MerchantCost = relic.MerchantCost
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
