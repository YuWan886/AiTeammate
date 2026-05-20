using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace AiTeammate.AiTeammateCode.Suggestion;

public class CardHighlighter
{
    public void ApplyHighlights(List<CardSuggestion> suggestions, Player player)
    {
        ClearAllHighlights();

        var combat = player.PlayerCombatState;
        if (combat == null) return;

        foreach (var suggestion in suggestions)
        {
            var card = FindCardNode(combat, suggestion.CardName);
            if (card == null) continue;

            ApplyGlow(card, suggestion.GlowColor, suggestion.Priority);
        }
    }

    private void ApplyGlow(Node cardNode, string color, int priority)
    {
        var shader = ResourceLoader.Load<Shader>("res://mods/AiTeammate/glow.gdshader");
        if (shader == null) return;

        var material = new ShaderMaterial();
        material.Shader = shader;
        material.SetShaderParameter("glow_color", ColorFromName(color));
        material.SetShaderParameter("intensity", priority == 1 ? 1.0f : 0.5f);
        material.SetShaderParameter("pulse", priority == 1);

        if (cardNode is CanvasItem canvasItem)
            canvasItem.Material = material;
    }

    public void ClearAllHighlights()
    {
        var combatRoom = NCombatRoom.Instance;
        if (combatRoom == null) return;

        var handContainer = combatRoom.GetNodeOrNull("HandContainer");
        if (handContainer == null) return;

        foreach (var child in handContainer.GetChildren())
        {
            if (child is CanvasItem canvasItem)
                canvasItem.Material = null;
        }
    }

    private Node? FindCardNode(PlayerCombatState combat, string cardName)
    {
        var combatRoom = NCombatRoom.Instance;
        if (combatRoom == null) return null;

        var handContainer = combatRoom.GetNodeOrNull("HandContainer");
        if (handContainer == null) return null;

        foreach (var child in handContainer.GetChildren())
        {
            if (child.Name.ToString().Contains(cardName, System.StringComparison.OrdinalIgnoreCase))
                return child;
        }
        return null;
    }

    private Color ColorFromName(string name)
    {
        return name switch
        {
            "gold" => new Color(1.0f, 0.84f, 0.0f),
            "green" => new Color(0.0f, 1.0f, 0.3f),
            "red" => new Color(1.0f, 0.2f, 0.2f),
            _ => new Color(0.5f, 0.5f, 0.5f)
        };
    }
}
