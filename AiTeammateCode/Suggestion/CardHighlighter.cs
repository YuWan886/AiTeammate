using MegaCrit.Sts2.Core.Entities.Players;
using AiTeammate.AiTeammateCode.Settings;

namespace AiTeammate.AiTeammateCode.Suggestion;

/// <summary>
/// Applies AI card suggestions to the hand using RitsuLib's hand-outline registry (no custom
/// shader asset required).
/// </summary>
public class CardHighlighter
{
    public void ApplyHighlights(List<CardSuggestion> suggestions, Player player)
    {
        if (!AiSettingsStore.Current.EnableSuggestions)
        {
            ClearAllHighlights();
            return;
        }

        SuggestionHighlightState.SetFromSuggestions(
            suggestions, player, AiSettingsStore.Current.MaxSuggestionsPerTurn);
    }

    public void ClearAllHighlights()
    {
        SuggestionHighlightState.Clear();
    }
}
