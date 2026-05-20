namespace AiTeammate.AiTeammateCode.Cache;

public class ContentIndex
{
    public string GameVersion { get; set; } = "";
    public Dictionary<string, CardIndexEntry> Cards { get; set; } = new();
    public Dictionary<string, RelicIndexEntry> Relics { get; set; } = new();
    public Dictionary<string, PotionIndexEntry> Potions { get; set; } = new();
    public Dictionary<string, EnchantIndexEntry> Enchants { get; set; } = new();
}

public class CardIndexEntry
{
    public string Name { get; set; } = "";
    public int Cost { get; set; }
    public string CostText { get; set; } = "";
    public string Type { get; set; } = "";
    public string Rarity { get; set; } = "";
    public string OneLiner { get; set; } = "";
    public string Character { get; set; } = "";
    public string CharacterId { get; set; } = "";
    public string Pool { get; set; } = "";
    public string PoolId { get; set; } = "";
    public List<string> Keywords { get; set; } = new();
    public List<string> Tags { get; set; } = new();
}

public class RelicIndexEntry
{
    public string Name { get; set; } = "";
    public string Tier { get; set; } = "";
    public string OneLiner { get; set; } = "";
    public string Pool { get; set; } = "";
    public string PoolId { get; set; } = "";
}

public class PotionIndexEntry
{
    public string Name { get; set; } = "";
    public string Rarity { get; set; } = "";
    public string OneLiner { get; set; } = "";
    public string Pool { get; set; } = "";
    public string PoolId { get; set; } = "";
    public string Usage { get; set; } = "";
    public string TargetType { get; set; } = "";
}

public class ContentFull
{
    public string GameVersion { get; set; } = "";
    public Dictionary<string, CardFullEntry> Cards { get; set; } = new();
    public Dictionary<string, RelicFullEntry> Relics { get; set; } = new();
    public Dictionary<string, PotionFullEntry> Potions { get; set; } = new();
    public Dictionary<string, EnchantFullEntry> Enchants { get; set; } = new();
}

public class CardFullEntry
{
    public string Name { get; set; } = "";
    public string UpgradedName { get; set; } = "";
    public int Cost { get; set; }
    public string CostText { get; set; } = "";
    public string UpgradeCostText { get; set; } = "";
    public string Type { get; set; } = "";
    public string Rarity { get; set; } = "";
    public string Description { get; set; } = "";
    public string UpgradedDescription { get; set; } = "";
    public List<string> Keywords { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public string TargetType { get; set; } = "";
    public string Character { get; set; } = "";
    public string CharacterId { get; set; } = "";
    public string Pool { get; set; } = "";
    public string PoolId { get; set; } = "";
    public string MultiplayerConstraint { get; set; } = "";
    public int StarCost { get; set; } = -1;
    public bool HasXCost { get; set; }
    public bool HasXStarCost { get; set; }
    public bool IsUpgradable { get; set; }
}

public class RelicFullEntry
{
    public string Name { get; set; } = "";
    public string Tier { get; set; } = "";
    public string Description { get; set; } = "";
    public string FlavorText { get; set; } = "";
    public string Pool { get; set; } = "";
    public string PoolId { get; set; } = "";
    public int MerchantCost { get; set; }
}

public class PotionFullEntry
{
    public string Name { get; set; } = "";
    public string Rarity { get; set; } = "";
    public string Description { get; set; } = "";
    public string Pool { get; set; } = "";
    public string PoolId { get; set; } = "";
    public string Usage { get; set; } = "";
    public string TargetType { get; set; } = "";
}

public class EnchantIndexEntry
{
    public string Name { get; set; } = "";
    public string OneLiner { get; set; } = "";
}

public class EnchantFullEntry
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}
