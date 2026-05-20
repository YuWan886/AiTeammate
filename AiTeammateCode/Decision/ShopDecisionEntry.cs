using MegaCrit.Sts2.Core.Models;

namespace AiTeammate.AiTeammateCode.Decision;

public sealed class ShopDecisionEntry
{
    public string DisplayText { get; init; } = "";
    public CardModel? Card { get; init; }
    public RelicModel? Relic { get; init; }
    public PotionModel? Potion { get; init; }
    public IReadOnlyList<RelicModel> ModifyingRelics { get; init; } = Array.Empty<RelicModel>();
    public bool IsCardRemoval { get; init; }
}
