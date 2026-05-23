using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace AiTeammate.AiTeammateCode.Decision;

/// <summary>
/// Handles AI decision-making for HextechRunes rune selection.
/// Uses soft dependency (reflection) — all HextechRunes types are accessed
/// without a compile-time reference.
/// </summary>
public static class HextechRunesDecisionHandler
{
    private static Type? _selectionScreenType;
    private static Type? _aiTeammateCompatType;
    private static MethodInfo? _pickRandomRuneIndexMethod;
    private static bool _availabilityChecked;
    private static bool _isAvailable;

    /// <summary>Whether HextechRunes mod is detected and available.</summary>
    public static bool IsAvailable
    {
        get
        {
            if (!_availabilityChecked)
            {
                _availabilityChecked = true;
                _isAvailable = DetectHextechRunes();
            }
            return _isAvailable;
        }
    }

    private static bool DetectHextechRunes()
    {
        try
        {
            _selectionScreenType = FindHextechType("HextechRunes.HextechRuneSelectionScreen");
            _aiTeammateCompatType = FindHextechType("HextechRunes.HextechAiTeammateCompat");
            if (_selectionScreenType == null || _aiTeammateCompatType == null)
                return false;

            _pickRandomRuneIndexMethod = _aiTeammateCompatType.GetMethod(
                "PickRandomRuneIndex",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return _pickRandomRuneIndexMethod != null;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[HextechRunes] 检测失败: {ex.Message}");
            return false;
        }
    }

    private static Type? FindHextechType(string typeName)
    {
        var type = Type.GetType(typeName + ", HextechRunes", throwOnError: false);
        if (type != null) return type;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = asm.GetType(typeName, throwOnError: false);
            if (type != null) return type;
        }
        return null;
    }

    /// <summary>
    /// Extract rune option names and descriptions from a HextechRuneSelectionScreen.
    /// Uses the public CurrentRelics property.
    /// </summary>
    public static List<RuneOptionInfo> ExtractOptions(object screenInstance)
    {
        var options = new List<RuneOptionInfo>();
        try
        {
            var currentRelicsProp = _selectionScreenType!.GetProperty("CurrentRelics");
            if (currentRelicsProp?.GetValue(screenInstance) is not IReadOnlyList<RelicModel> relics)
                return options;

            for (int i = 0; i < relics.Count; i++)
            {
                var relic = relics[i];
                options.Add(new RuneOptionInfo
                {
                    Index = i,
                    Name = SafeLocText(relic.Title),
                    Description = CleanDescription(SafeLocText(relic.DynamicDescription)),
                    RelicId = (relic.CanonicalInstance?.Id ?? relic.Id).Entry
                });
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[HextechRunes] 提取选项失败: {ex.Message}");
        }
        return options;
    }

    /// <summary>
    /// Extract monster hex info from the screen.
    /// </summary>
    public static MonsterHexInfo? ExtractMonsterHex(object screenInstance)
    {
        try
        {
            var monsterHexProp = _selectionScreenType!.GetProperty("CurrentMonsterHex");
            if (monsterHexProp == null) return null;

            var hexValue = monsterHexProp.GetValue(screenInstance);
            if (hexValue == null) return null;

            var hexKindName = hexValue.ToString();
            if (string.IsNullOrEmpty(hexKindName)) return null;

            var catalogType = FindHextechType("HextechRunes.MonsterHexCatalog");
            var getDescriptionMethod = catalogType?.GetMethod("GetEnemyHexDescriptionFormatted",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            string? description = null;
            if (getDescriptionMethod != null)
            {
                try
                {
                    var desc = getDescriptionMethod.Invoke(null, new[] { hexValue });
                    if (desc is string descStr)
                        description = CleanDescription(descStr);
                }
                catch { }
            }

            var getIconRelicMethod = catalogType?.GetMethod("GetIconRelicForMonsterHex",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            string? title = hexKindName;
            if (getIconRelicMethod != null)
            {
                try
                {
                    if (getIconRelicMethod.Invoke(null, new[] { hexValue }) is RelicModel iconRelic)
                        title = SafeLocText(iconRelic.Title);
                }
                catch { }
            }

            var enemyRemovedProp = _selectionScreenType!.GetProperty("EnemyHexRemoved");
            var isRemoved = enemyRemovedProp?.GetValue(screenInstance) is true;

            return new MonsterHexInfo
            {
                KindName = hexKindName,
                Title = title ?? hexKindName,
                Description = description ?? "",
                IsRemoved = isRemoved
            };
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[HextechRunes] 提取怪物Hex失败: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Check if a HextechRuneSelectionScreen is for an AI player by reading the
    /// _titleOverride private field. In host-controlled mode, only AI players get
    /// a custom title override.
    /// </summary>
    public static bool IsScreenForAi(object screenInstance)
    {
        try
        {
            var titleOverrideField = _selectionScreenType!.GetField("_titleOverride",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var titleOverride = titleOverrideField?.GetValue(screenInstance) as string;
            return !string.IsNullOrEmpty(titleOverride);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Click the card button at the given slot index on the screen.
    /// Traverses the node tree to find the button named "{relicId}_Card".
    /// </summary>
    public static async Task<bool> ClickRuneCard(object screenInstance, int slotIndex, List<RuneOptionInfo> options)
    {
        try
        {
            if (screenInstance is not Node screenNode) return false;
            if (slotIndex < 0 || slotIndex >= options.Count) return false;

            var tree = screenNode.GetTree();
            if (tree != null)
                await screenNode.ToSignal(tree, "process_frame");

            var buttonName = $"{options[slotIndex].RelicId}_Card";
            var button = FindChildByName(screenNode, buttonName);
            if (button == null)
            {
                var slotNode = screenNode.GetNodeOrNull<Node>($"PlayerCardsRow/Slot_{slotIndex}");
                button = slotNode?.GetChild(0) as Button;
            }

            if (button is not BaseButton baseButton || baseButton.Disabled) return false;

            baseButton.EmitSignal("pressed");
            MainFile.Logger.Info($"[HextechRunes] 点击符文卡 slot={slotIndex} name={buttonName}");
            return true;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[HextechRunes] 点击卡牌失败 slot={slotIndex}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Click the reroll button for a specific slot.
    /// </summary>
    public static async Task<bool> ClickRerollButton(object screenInstance, int slotIndex)
    {
        try
        {
            if (screenInstance is not Node screenNode) return false;

            var buttonName = $"RerollButton_{slotIndex}";
            var button = FindChildByName(screenNode, buttonName);
            if (button is not BaseButton baseButton || baseButton.Disabled) return false;

            baseButton.EmitSignal("pressed");
            MainFile.Logger.Info($"[HextechRunes] 点击重投按钮 slot={slotIndex}");

            var tree = screenNode.GetTree();
            if (tree != null)
            {
                await screenNode.ToSignal(tree, "process_frame");
                await screenNode.ToSignal(tree, "process_frame");
            }
            return true;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[HextechRunes] 点击重投失败 slot={slotIndex}: {ex.Message}");
            return false;
        }
    }

    private static Button? FindChildByName(Node root, string name)
    {
        foreach (var child in root.GetChildren(false))
        {
            if (child.Name.ToString() == name && child is Button button)
                return button;
            var found = FindChildByName(child, name);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// LLM-based rune selection with optional reroll support.
    /// Returns the selected index, or -1 if selection failed.
    /// </summary>
    public static async Task<int> DecideRuneWithLlmAsync(
        Player aiPlayer,
        List<RuneOptionInfo> options,
        MonsterHexInfo? monsterHex,
        object? screenInstance = null,
        CancellationToken ct = default)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized) return PickHeuristicFallback(aiPlayer, options);

        try
        {
            var (choice, reroll, reason) = await manager.DecisionService.DecideHextechRuneAsync(
                aiPlayer, options, monsterHex, ct);

            if (reroll is { Count: > 0 } && screenInstance != null)
            {
                foreach (var slotIndex in reroll)
                {
                    if (slotIndex < 0 || slotIndex >= options.Count) continue;
                    var clicked = await ClickRerollButton(screenInstance, slotIndex);
                    if (!clicked) continue;
                }

                var newOptions = ExtractOptions(screenInstance);
                var newMonsterHex = ExtractMonsterHex(screenInstance);

                if (newOptions.Count > 0 && newOptions.Any(o =>
                    o.RelicId != options[o.Index].RelicId))
                {
                    var (choice2, _, reason2) = await manager.DecisionService.DecideHextechRuneAsync(
                        aiPlayer, newOptions, newMonsterHex, ct, allowReroll: false);

                    manager.AiLog.Log("AI_THINK",
                        $"[海克斯符文-重投后] {choice2}: {newOptions[choice2].Name} | {reason2}");
                    return choice2;
                }
            }

            if (choice >= 0 && choice < options.Count)
            {
                manager.AiLog.Log("AI_THINK",
                    $"[海克斯符文] 选择 {choice}: {options[choice].Name} | {reason}");
                return choice;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[HextechRunes] LLM决策失败: {ex.Message}");
        }

        return PickHeuristicFallback(aiPlayer, options);
    }

    /// <summary>
    /// Heuristic fallback when LLM is unavailable.
    /// Priority: character pool match → rarity keywords → random.
    /// </summary>
    public static int PickHeuristicFallback(Player aiPlayer, List<RuneOptionInfo> options)
    {
        if (options.Count == 0) return -1;

        int best = 0;
        int bestScore = -1;

        for (int i = 0; i < options.Count; i++)
        {
            var score = 0;
            var name = options[i].Name.ToLowerInvariant();

            var charName = (aiPlayer.Character?.Id.Entry ?? "").ToLowerInvariant();
            if (!string.IsNullOrEmpty(charName) && name.Contains(charName))
                score += 10;

            var desc = options[i].Description.ToLowerInvariant();
            if (desc.Contains("rare") || desc.Contains("gold") || desc.Contains("legendary"))
                score += 5;

            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        if (bestScore <= 0)
            best = System.Random.Shared.Next(options.Count);

        MainFile.Logger.Info($"[HextechRunes] 启发式选择 index={best} name={options[best].Name} score={bestScore}");
        return best;
    }

    private static string SafeLocText(LocString? loc)
    {
        return PromptTextHelper.ResolveLocString(loc);
    }

    private static string CleanDescription(string desc)
    {
        desc = System.Text.RegularExpressions.Regex.Replace(desc, @"\[.*?\]", "");
        desc = System.Text.RegularExpressions.Regex.Replace(desc, @"res://\S+", "");
        return desc.Trim();
    }
}

public class RuneOptionInfo
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string RelicId { get; set; } = "";
}

public class MonsterHexInfo
{
    public string KindName { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsRemoved { get; set; }
}
