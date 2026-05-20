using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using AiTeammate.AiTeammateCode;

namespace AiTeammate.AiTeammateCode.Decision;

public class OutOfCombatExecutor
{
    private readonly DecisionService _decision;

    public OutOfCombatExecutor(DecisionService decision)
    {
        _decision = decision;
    }

    public async Task AutoPickCardReward(Player aiPlayer, CancellationToken ct)
    {
        await Task.Delay(400, ct);
        var log = AiTeammateManager.Instance.AiLog;

        NRewardsScreen? rewardsScreen = null;
        NCardRewardSelectionScreen? cardScreen = null;

        try { rewardsScreen = AutoSlayer.GetCurrentScreen<NRewardsScreen>(); } catch { }
        try { cardScreen = AutoSlayer.GetCurrentScreen<NCardRewardSelectionScreen>(); } catch { }

        log.Log("REWARD", $"屏幕检测: NRewardsScreen={(rewardsScreen != null)} NCardRewardSelectionScreen={(cardScreen != null)}");

        if (rewardsScreen != null && GodotObject.IsInstanceValid(rewardsScreen))
        {
            log.Log("REWARD", "进入 HandleRewardsScreen");
            await HandleRewardsScreen(rewardsScreen, aiPlayer, ct);
            return;
        }

        if (cardScreen != null && GodotObject.IsInstanceValid(cardScreen))
        {
            log.Log("REWARD", "进入 HandleCardSelectionScreen");
            await HandleCardSelectionScreen(cardScreen, aiPlayer, ct);
            return;
        }

        log.Log("REWARD", "未找到任何奖励界面");
        MainFile.Logger.Warn("No reward screen found for AI to interact with");
    }

    private async Task HandleRewardsScreen(NRewardsScreen screen, Player aiPlayer, CancellationToken ct)
    {
        var log = AiTeammateManager.Instance.AiLog;
        var aiNetId = aiPlayer.NetId;

        var buttons = UiHelper.FindAll<NRewardButton>(screen);
        log.Log("REWARD", $"NRewardsScreen: {buttons.Count} 个奖励按钮, AI NetId={aiNetId}");

        foreach (var b in buttons)
        {
            var reward = b.Reward;
            if (reward != null)
                log.Log("REWARD", $"  奖励: {reward.GetType().Name} PlayerNetId={reward.Player.NetId} IsEnabled={b.IsEnabled}");
            else
                log.Log("REWARD", $"  奖励: null");
        }

        // Claim all non-card rewards belonging to the AI (gold, relic, potion)
        var nonCardAiButtons = buttons
            .Where(b => b.IsEnabled && b.Reward != null
                && b.Reward.Player.NetId == aiNetId
                && b.Reward is not CardReward)
            .ToList();

        log.Log("REWARD", $"非卡牌奖励(AI): {nonCardAiButtons.Count} 个");

        foreach (var btn in nonCardAiButtons)
        {
            if (!GodotObject.IsInstanceValid(btn) || !btn.IsEnabled) continue;
            log.Log("REWARD", $"领取奖励: {btn.Reward!.GetType().Name}");
            await UiHelper.Click(btn, 500);
            await Task.Delay(300, ct);
        }

        // Now handle card rewards belonging to the AI
        var refreshedButtons = UiHelper.FindAll<NRewardButton>(screen);
        var cardButtons = refreshedButtons
            .Where(b => b.IsEnabled && b.Reward is CardReward && b.Reward.Player.NetId == aiNetId)
            .ToList();
        log.Log("REWARD", $"卡牌奖励(AI): {cardButtons.Count} 个");

        if (cardButtons.Count > 0)
        {
            var btn = cardButtons[0];
            log.Log("REWARD", "点击卡牌奖励");
            await UiHelper.Click(btn, 300);
            await Task.Delay(500, ct);

            var cardScreen = await WaitForScreen<NCardRewardSelectionScreen>(ct, 5);
            if (cardScreen != null)
            {
                log.Log("REWARD", "卡牌选择界面已打开");
                await HandleCardSelectionScreen(cardScreen, aiPlayer, ct);
            }
            else
            {
                log.Log("REWARD", "等待卡牌选择界面超时");
            }
        }

        // Click Proceed to close the rewards screen
        await ClickProceed(screen, ct);
    }

    private async Task HandleCardSelectionScreen(NCardRewardSelectionScreen screen, Player aiPlayer, CancellationToken ct)
    {
        var log = AiTeammateManager.Instance.AiLog;

        var holders = UiHelper.FindAll<NCardHolder>(screen);
        log.Log("REWARD", $"CardSelectionScreen: {holders.Count} 张可选卡牌");

        if (holders.Count == 0)
        {
            log.Log("REWARD", "无可选卡牌, 点击Skip");
            ClickSkipButton(screen);
            return;
        }

        var firstCard = holders[0].CardModel;
        if (firstCard?.Owner?.NetId != aiPlayer.NetId)
        {
            log.Log("REWARD", $"卡片属于玩家 {firstCard?.Owner?.NetId}, 非AI ({aiPlayer.NetId}), 跳过");
            return;
        }

        var choices = holders
            .Select(h => h.CardModel)
            .Where(m => m != null)
            .Cast<CardModel>()
            .ToList();
        log.Log("REWARD", $"卡牌选项: {string.Join(", ", choices.Select(c => c.Id))}");

        var (pick, reason) = await _decision.DecideCardRewardAsync(aiPlayer, choices, ct);
        if (!string.IsNullOrWhiteSpace(reason))
            log.Log("AI_THINK", $"[选牌] {reason}");
        log.Log("AI_ACTION", $"选择卡牌 index={pick} | {reason}");
        log.Log("REWARD", $"AI选择: index={pick}");

        if (pick >= 0 && pick < holders.Count)
        {
            holders[pick].EmitSignal(NCardHolder.SignalName.Pressed, holders[pick]);
            log.Log("REWARD", $"已选择卡牌 index={pick}");
        }
        else
        {
            ClickSkipButton(screen);
            log.Log("REWARD", "跳过卡牌选择");
        }

        await WaitHelper.Until(
            () => !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree(),
            ct, TimeSpan.FromSeconds(10), "Card reward screen did not close");
    }

    private void ClickSkipButton(NCardRewardSelectionScreen screen)
    {
        var proceedBtn = UiHelper.FindFirst<NProceedButton>(screen);
        if (proceedBtn != null)
        {
            proceedBtn.ForceClick();
            return;
        }
        var buttons = UiHelper.FindAll<Button>(screen);
        var skipBtn = buttons.FirstOrDefault(b =>
            (b.Text ?? "").Contains("Skip", StringComparison.OrdinalIgnoreCase)
            || (b.Text ?? "").Contains("跳过", StringComparison.OrdinalIgnoreCase));
        skipBtn?.EmitSignal(Button.SignalName.Pressed);
    }

    private async Task ClickProceed(NRewardsScreen screen, CancellationToken ct)
    {
        var log = AiTeammateManager.Instance.AiLog;
        var proceedBtn = UiHelper.FindFirst<NProceedButton>(screen);
        if (proceedBtn != null && proceedBtn.IsEnabled)
        {
            log.Log("REWARD", "点击Proceed");
            await UiHelper.Click(proceedBtn, 300);
        }
        else
        {
            log.Log("REWARD", "未找到Proceed按钮或按钮未启用");
        }
    }

    private async Task<T?> WaitForScreen<T>(CancellationToken ct, int seconds) where T : Control
    {
        for (int i = 0; i < seconds * 10; i++)
        {
            await Task.Delay(100, ct);
            var screen = AutoSlayer.GetCurrentScreen<T>();
            if (screen != null && GodotObject.IsInstanceValid(screen)) return screen;
        }
        return default;
    }

    public async Task AutoNavigateMap(Player aiPlayer, CancellationToken ct)
    {
        await Task.Delay(500, ct);
        var log = AiTeammateManager.Instance.AiLog;
        var mapScreen = MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.Instance;
        if (mapScreen == null || !GodotObject.IsInstanceValid(mapScreen)) return;

        var clickables = UiHelper.FindAll<Button>(mapScreen)
            .Where(c => c.Visible).ToList();

        if (clickables.Count == 0) return;

        var (pick, reason) = await _decision.DecideMapNavigationAsync(aiPlayer, ct);
        if (pick < 0 || pick >= clickables.Count) pick = 0;

        if (!string.IsNullOrWhiteSpace(reason))
            log.Log("AI_THINK", $"[地图] {reason}");
        log.Log("AI_ACTION", $"选择路线 index={pick} | {reason}");

        clickables[pick].EmitSignal(Button.SignalName.Pressed);
        await Task.Delay(300, ct);
    }

    public async Task AutoRestSite(Player aiPlayer, CancellationToken ct)
    {
        var log = AiTeammateManager.Instance.AiLog;

        try
        {
            var sync = RunManager.Instance.RestSiteSynchronizer;
            if (sync == null)
            {
                log.Log("REST", "RestSiteSynchronizer is null");
                return;
            }

            var options = sync.GetOptionsForPlayer(aiPlayer);
            if (options == null || options.Count == 0)
            {
                log.Log("REST", "No rest site options for AI player");
                return;
            }

            log.Log("REST", $"AI rest options: {string.Join(", ", options.Select(o => o.OptionId))}");

            var creature = aiPlayer.Creature;
            var decision = await _decision.DecideRestSiteAsync(aiPlayer, creature.CurrentHp, creature.MaxHp, options, ct);
            var pick = decision.Choice;
            var reason = decision.Reason;
            if (!string.IsNullOrWhiteSpace(reason))
                log.Log("AI_THINK", $"[休息站] {reason}");
            log.Log("REST", $"AI chose option index={pick}");

            if (pick < 0 || pick >= options.Count)
            {
                log.Log("REST", "Invalid choice, defaulting to 0");
                pick = 0;
            }

            var chosen = options[pick];
            if (!chosen.IsEnabled)
            {
                log.Log("REST", "Chosen option disabled, finding first enabled");
                int firstIdx = -1;
                for (int i = 0; i < options.Count; i++)
                    if (options[i].IsEnabled) { firstIdx = i; break; }
                if (firstIdx < 0) return;
                pick = firstIdx;
                chosen = options[pick];
            }

            log.Log("REST", $"Executing option: {chosen.OptionId}");
            log.Log("AI_ACTION", $"休息站选择: {chosen.OptionId} | {reason}");

            // Always call ChooseOption to properly register with the synchronizer.
            // For SMITH, the game will open a card selection screen which
            // ChooseACardSelectionPatch handles with auto-pick.
            try
            {
                await Traverse.Create(sync).Method("ChooseOption", aiPlayer, pick).GetValue<Task<bool>>();
            }
            catch (Exception ex)
            {
                log.Log("REST", $"ChooseOption failed: {ex.Message}, calling OnSelect directly");
                await chosen.OnSelect();
            }

            log.Log("REST", "Rest site decision complete");
        }
        catch (Exception ex)
        {
            log.LogError("REST", $"Rest site error: {ex.Message}\n{ex.StackTrace}");
        }
    }

    public async Task AutoShop(Player aiPlayer, CancellationToken ct)
    {
        await Task.Delay(500, ct);
        var log = AiTeammateManager.Instance.AiLog;
        log.Log("SHOP", "AI entering shop");

        // Skip shop if AI is dead — purchasing would cause NullReferenceException
        // because the player's card pile state is invalid when dead.
        if (!aiPlayer.Creature.IsAlive)
        {
            log.Log("SHOP", "AI is dead, skipping shop");
            return;
        }

        try
        {
            // Create inventory for AI player (game only creates for local player)
            var inventory = MerchantInventory.CreateForNormalMerchant(aiPlayer);
            var allEntries = inventory.AllEntries.ToList();
            var stockedEntries = allEntries.Where(e => e.IsStocked).ToList();
            log.Log("SHOP", $"AI shop inventory: {allEntries.Count} total, {stockedEntries.Count} stocked | Gold={aiPlayer.Gold}");

            if (stockedEntries.Count == 0)
            {
                log.Log("SHOP", "No stocked items");
                return;
            }

            // Get affordable items
            var affordable = allEntries
                .Where(e => e.IsStocked && e.Cost <= aiPlayer.Gold)
                .ToList();

            if (affordable.Count == 0)
            {
                var stillStocked = allEntries.Count(e => e.IsStocked);
                if (stillStocked == 0)
                    log.Log("SHOP", "Nothing left in stock");
                else
                    log.Log("SHOP", $"Nothing affordable ({stillStocked} items too expensive, Gold={aiPlayer.Gold})");
                return;
            }

            var shopEntries = affordable.Select(CreateShopDecisionEntry).ToList();
            log.Log("SHOP", $"{affordable.Count} affordable items, Gold={aiPlayer.Gold}");

            // Single LLM call: get all items to buy at once
            var (choices, reason) = await _decision.DecideShopAsync(aiPlayer, shopEntries, ct);

            if (!string.IsNullOrWhiteSpace(reason))
                log.Log("AI_THINK", $"[商店] {reason}");

            // Empty choices = leave shop
            if (choices.Count == 0)
            {
                log.Log("AI_ACTION", "离开商店");
                log.Log("SHOP", "AI chose to leave shop (empty selection)");
                log.Log("SHOP", "Shop complete");
                return;
            }

            // Validate and deduplicate choices
            var validChoices = choices
                .Where(c => c >= 0 && c < affordable.Count)
                .Distinct()
                .ToList();

            if (validChoices.Count == 0)
            {
                log.Log("AI_ACTION", "离开商店");
                log.Log("SHOP", "AI chose to leave shop (no valid choices)");
                log.Log("SHOP", "Shop complete");
                return;
            }

            // Execute purchases in order, tracking remaining gold
            int purchased = 0;
            foreach (var idx in validChoices)
            {
                ct.ThrowIfCancellationRequested();

                var chosen = affordable[idx];

                // Check if still affordable (gold may have been spent on previous purchases)
                if (chosen.Cost > aiPlayer.Gold)
                {
                    log.Log("SHOP", $"Skipping {shopEntries[idx].DisplayText} — cost {chosen.Cost} exceeds remaining gold {aiPlayer.Gold}");
                    continue;
                }

                // Check if still stocked
                if (!chosen.IsStocked)
                {
                    log.Log("SHOP", $"Skipping {shopEntries[idx].DisplayText} — no longer in stock");
                    continue;
                }

                log.Log("AI_ACTION", $"购买: {shopEntries[idx].DisplayText}");
                log.Log("SHOP", $"AI buying: {chosen.GetType().Name} for {chosen.Cost} gold");

                bool success = await chosen.OnTryPurchaseWrapper(inventory, false);
                log.Log("SHOP", $"Purchase result: {(success ? "success" : "failed")} | Gold remaining: {aiPlayer.Gold}");

                if (success)
                    purchased++;
                else
                    break; // Stop on purchase failure

                await Task.Delay(200, ct);
            }

            log.Log("SHOP", $"Shop complete — purchased {purchased} item(s)");
        }
        catch (Exception ex)
        {
            log.LogError("SHOP", $"Shop error: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>Format a shop entry into a compact human-readable string for the AI prompt.</summary>
    private static ShopDecisionEntry CreateShopDecisionEntry(MerchantEntry e)
    {
        var price = $"[{e.Cost}金]";
        if (e is MerchantCardEntry cardEntry)
        {
            var card = cardEntry.CreationResult?.Card;
            var cardName = card?.Title ?? "?";
            // Include energy cost (and starlight cost if applicable)
            var costInfo = "";
            try
            {
                if (card != null)
                {
                    var energyCost = card.EnergyCost?.Canonical ?? 0;
                    var starCost = Math.Max(0, card.GetStarCostWithModifiers());
                    costInfo = starCost > 0 ? $"({energyCost}费/{starCost}辉星)" : $"({energyCost}费)";
                }
            }
            catch { }
            // Include card description for informed decisions
            string? cardDesc = null;
            try
            {
                if (card != null)
                {
                    cardDesc = card.GetDescriptionForPile(MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand);
                    cardDesc = CleanDescription(cardDesc);
                    if (cardDesc != null && cardDesc.Length > 80) cardDesc = cardDesc[..80] + "...";
                }
            }
            catch { }
            var descPart = cardDesc != null ? $" — {cardDesc}" : "";
            return new ShopDecisionEntry
            {
                DisplayText = $"{price} 卡牌:{cardName}{costInfo}{descPart}",
                Card = card,
                ModifyingRelics = cardEntry.CreationResult != null
                    ? cardEntry.CreationResult.ModifyingRelics.ToList()
                    : new List<RelicModel>()
            };
        }
        if (e is MerchantRelicEntry relicEntry)
        {
            var model = relicEntry.Model;
            var relicName = SafeLocText(model?.Title);
            var relicId = model?.Id.Entry ?? "?";
            if (relicName == "?" || relicName == relicId) relicName = relicId;
            string relicDesc = "?";
            try { relicDesc = SafeLocText(model?.DynamicDescription); }
            catch { /* DynamicVars may fail for AI, fall back to name only */ }
            relicDesc = CleanDescription(relicDesc);
            var descPart = relicDesc.Length > 0 ? $" — {relicDesc}" : "";
            if (descPart.Length > 80) descPart = descPart[..80] + "...";
            return new ShopDecisionEntry
            {
                DisplayText = $"{price} 遗物:{relicName}{descPart}",
                Relic = model
            };
        }
        if (e is MerchantPotionEntry potionEntry)
        {
            var model = potionEntry.Model;
            var potionName = SafeLocText(model?.Title);
            var potionId = model?.Id.Entry ?? "?";
            if (potionName == "?" || potionName == potionId) potionName = potionId;
            string potionDesc = "?";
            try { potionDesc = SafeLocText(model?.DynamicDescription); }
            catch { /* DynamicVars may fail for AI */ }
            potionDesc = CleanDescription(potionDesc);
            var descPart = potionDesc.Length > 0 ? $" — {potionDesc}" : "";
            if (descPart.Length > 80) descPart = descPart[..80] + "...";
            return new ShopDecisionEntry
            {
                DisplayText = $"{price} 药水:{potionName}{descPart}",
                Potion = model
            };
        }
        if (e is MerchantCardRemovalEntry)
        {
            return new ShopDecisionEntry
            {
                DisplayText = $"{price} 删牌",
                IsCardRemoval = true
            };
        }

        return new ShopDecisionEntry
        {
            DisplayText = $"{price} {e.GetType().Name}"
        };
    }

    /// <summary>Resolve shop text for prompts, preferring formatted text with concrete values.</summary>
    private static string SafeLocText(LocString? loc)
    {
        return PromptTextHelper.ResolveLocString(loc);
    }

    /// <summary>
    /// Clean a description string for AI prompt use:
    /// strips BBCode tags, Godot resource paths, unresolved placeholders, and collapses whitespace.
    /// </summary>
    private static string CleanDescription(string? raw)
    {
        var s = PromptTextHelper.CleanText(raw);
        if (s.Length == 0) return "";
        // Strip unresolved SmartFormat placeholders (e.g. {Amount:cond:...})
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\{[^}]+\}", "");
        return s.Trim();
    }
}
