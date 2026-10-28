using System.Text.Json.Nodes;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Decision;
using AiTeammate.AiTeammateCode.Solo;

namespace AiTeammate.AiTeammateCode.Agent;

internal readonly record struct AgentActionResult(bool Ok, string Detail, int ObserveMs = 900);

/// <summary>Last action, mirrored into the next snapshot so the model sees what happened.</summary>
internal static class AgentActionState
{
    private static readonly Lock Sync = new();
    private static string _tool = "";
    private static bool _ok;
    private static string _detail = "";

    public static void Record(string tool, bool ok, string detail)
    {
        lock (Sync)
        {
            _tool = tool;
            _ok = ok;
            _detail = detail;
        }
    }

    public static JsonObject? ToJson()
    {
        lock (Sync)
        {
            if (string.IsNullOrEmpty(_tool))
                return null;
            return new JsonObject
            {
                ["tool"] = _tool,
                ["ok"] = _ok,
                ["detail"] = _detail
            };
        }
    }
}

/// <summary>
/// Implements the game-side action tools. Every method runs on the Godot main thread, validates the
/// current snapshot, starts the action, and returns immediately — the bridge observes the resulting
/// state after a short delay instead of blocking on animations.
/// </summary>
internal static class AgentActions
{
    public static AgentActionResult Start(string tool, JsonObject args)
    {
        try
        {
            // Control tools change who drives the game; they do not need an actionable board.
            switch (tool)
            {
                case "sts2_decide":
                    return Decide(args);
                case "sts2_start_run":
                    return StartRun(args);
                case "sts2_continue_run":
                    return ContinueRun();
            }

            var snap = AgentSnapshotBuilder.Capture();

            // The AI player is dsh's to steer: its actions are decisions, not UI clicks.
            if (snap.AiControlled)
            {
                return new AgentActionResult(false,
                    "This run is playing as the AI player. Answer the pending decision with sts2_decide, "
                    + "or call sts2_wait for the next one.", 0);
            }

            if (!snap.Actionable)
            {
                return new AgentActionResult(false,
                    $"No action is possible right now (phase={snap.Phase}, blocked_by={snap.BlockedBy}). "
                    + string.Join(" ", snap.Json["notes"] is JsonArray notes
                        ? notes.Select(n => n?.GetValue<string>() ?? "")
                        : Array.Empty<string>()));
            }

            return tool switch
            {
                "sts2_play_card" => PlayCard(args, snap),
                "sts2_use_potion" => UsePotion(args, snap),
                "sts2_discard_potion" => DiscardPotion(args, snap),
                "sts2_end_turn" => EndTurn(snap),
                "sts2_choose" => Choose(args, snap),
                "sts2_select_cards" => SelectCards(args, snap),
                "sts2_proceed" => Proceed(snap),
                _ => new AgentActionResult(false, $"Unknown action tool: {tool}", 0)
            };
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Agent] {tool} failed: {ex}");
            return new AgentActionResult(false, $"Action failed: {ex.Message}", 0);
        }
    }

    // ---- Control ---------------------------------------------------------------------------

    /// <summary>Answers the parked AI decision with the JSON the plugin's model produced.</summary>
    private static AgentActionResult Decide(JsonObject args)
    {
        string decision;
        try
        {
            decision = args["decision"]?.GetValue<string>() ?? "";
        }
        catch
        {
            // The client may pass the JSON as an object instead of a string.
            decision = args["decision"]?.ToJsonString() ?? "";
        }

        var (ok, message) = AgentDecisionBroker.Resolve(decision);
        return new AgentActionResult(ok, message, ok ? 400 : 0);
    }

    /// <summary>Starts a fresh singleplayer run (character / ascension / seed). 开局。</summary>
    private static AgentActionResult StartRun(JsonObject args)
    {
        if (RunManager.Instance?.NetService?.Type is { } startNetType && startNetType != NetGameType.Singleplayer)
            return Fail("A new run can only be started from a singleplayer session.");

        var character = StringArg(args, "character");
        var ascension = IntArg(args, "ascension") ?? 0;
        var seed = StringArg(args, "seed");

        var (ok, message) = SoloRunController.StartAgentRun(character, ascension,
            string.IsNullOrWhiteSpace(seed) ? null : seed);
        return new AgentActionResult(ok, message, ok ? 3000 : 0);
    }

    /// <summary>Resumes the saved singleplayer run. 续档。</summary>
    private static AgentActionResult ContinueRun()
    {
        if (RunManager.Instance?.DebugOnlyGetState() != null)
            return Fail("A run is already in progress.");

        if (RunManager.Instance?.NetService?.Type is { } continueNetType && continueNetType != NetGameType.Singleplayer)
            return Fail("A saved singleplayer run can only be loaded from a singleplayer session.");

        try
        {
            var saves = SaveManager.Instance;
            if (saves == null || !saves.HasRunSave)
                return Fail("There is no saved run to continue.");

            var read = saves.LoadRunSave();
            if (read?.SaveData is not { } save)
                return Fail("The saved run could not be read: " + (read?.ErrorMessage ?? "unknown error"));

            var state = RunState.FromSerializable(save);
            _ = ContinueRunAsync(state, save);
            return new AgentActionResult(true, "Loading the saved run", 3000);
        }
        catch (Exception ex)
        {
            return Fail("Could not continue the run: " + ex.Message);
        }
    }

    private static async Task ContinueRunAsync(RunState state, SerializableRun save)
    {
        try
        {
            await RunManager.Instance.SetUpSavedSingleplayer(state, save);
            NGame.Instance?.ReactionContainer?.InitializeNetworking(new NetSingleplayerGameService());
            await NGame.Instance!.LoadRun(state, save.PreFinishedRoom);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Agent] continue run failed: {ex.Message}");
            try { AiTeammateManager.Instance.AiLog.LogError("AGENT", $"续档失败: {ex.Message}"); } catch { }
        }
    }

    // ---- Combat -------------------------------------------------------------------------

    private static AgentActionResult PlayCard(JsonObject args, AgentSnapshot snap)
    {
        if (snap.Phase != "combat")
            return Fail($"sts2_play_card only works in combat (current phase: {snap.Phase}).");

        if (!TryLocalPlayer(out var player, out _, out var error))
            return Fail(error);

        var combat = player.PlayerCombatState;
        if (combat == null)
            return Fail("No combat state for the local player.");

        var query = StringArg(args, "card");
        if (string.IsNullOrWhiteSpace(query))
            return Fail("Missing 'card' (hand index or card name).");

        var card = ResolveCard(combat.Hand.Cards, query);
        if (card == null)
        {
            var available = string.Join(", ",
                combat.Hand.Cards.Select((c, i) => $"{i}:{c.Title ?? c.Id.Entry}"));
            return Fail($"Card '{query}' is not in hand. Available: {available}");
        }

        if (!combat.HasEnoughResourcesFor(card, out var reason))
            return Fail($"Not enough resources for [{card.Title ?? card.Id.Entry}]: {reason}");

        var target = ResolveTarget(CombatManager.Instance.DebugOnlyGetState(), player,
            StringArg(args, "target"), card);

        // CardModel.IsValidTarget only accepts a creature for AnyEnemy / AnyAlly; everything else
        // (Self, AllEnemies, ...) must be played with target: null, which is also what the hand UI does.
        if (!CardTargetsACreature(card))
            target = null;

        // Play it the way the hand UI does — CardModel.TryManualPlay enqueues a PlayCardAction,
        // which spends energy/stars and raises the normal "when you play a card" hooks.
        // CardCmd.AutoPlay is documented as "play a card for free": it pays no cost at all, which
        // is exactly why a dsh-driven play used to leave the energy untouched.
        if (!card.TryManualPlay(target))
        {
            card.CanPlay(out var unplayable, out var preventer);
            var blocked = preventer != null ? $" (blocked by {preventer.Id.Entry})" : "";
            return Fail($"Cannot play [{card.Title ?? card.Id.Entry}] right now: {unplayable}{blocked}.");
        }

        var targetText = target != null ? $" → {target.Name}" : "";
        return new AgentActionResult(true, $"Playing [{card.Title ?? card.Id.Entry}]{targetText}", 700);
    }

    private static bool CardTargetsACreature(CardModel card) =>
        card.TargetType is TargetType.AnyEnemy or TargetType.AnyAlly;

    private static AgentActionResult UsePotion(JsonObject args, AgentSnapshot snap)
    {
        if (!TryLocalPlayer(out var player, out _, out var error))
            return Fail(error);

        var query = StringArg(args, "potion");
        if (string.IsNullOrWhiteSpace(query))
            return Fail("Missing 'potion' (slot index or potion name).");

        var potions = player.Potions.ToList();
        var potion = ResolvePotion(potions, query);
        if (potion == null)
        {
            var available = string.Join(", ",
                potions.Select((p, i) => $"{i}:{PotionName(p)}"));
            return Fail($"Potion '{query}' not found. Available: {available}");
        }

        // AnyTime potions (healing, gold, ...) work outside combat too; CombatOnly ones do not.
        var inCombat = snap.Phase == "combat" && CombatManager.Instance.IsInProgress;
        if (!inCombat && potion.Usage != PotionUsage.AnyTime)
            return Fail($"[{PotionName(potion)}] can only be used in combat.");

        var target = ResolveTarget(inCombat ? CombatManager.Instance.DebugOnlyGetState() : null, player,
            StringArg(args, "target"), null);
        if (target == null && potion.TargetType is TargetType.Self or TargetType.AnyPlayer)
            target = player.Creature;

        var action = new UsePotionAction(potion, target, inCombat);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);
        _ = AwaitPotionAsync(action, potion);

        var targetText = target != null ? $" → {target.Name}" : "";
        return new AgentActionResult(true, $"Using potion [{PotionName(potion)}]{targetText}", 700);
    }

    /// <summary>Throwing a potion away is a normal player action too. 丢弃药水。</summary>
    private static AgentActionResult DiscardPotion(JsonObject args, AgentSnapshot snap)
    {
        if (!TryLocalPlayer(out var player, out _, out var error))
            return Fail(error);

        var query = StringArg(args, "potion");
        if (string.IsNullOrWhiteSpace(query))
            return Fail("Missing 'potion' (slot index or potion name).");

        var potions = player.Potions.ToList();
        var potion = ResolvePotion(potions, query);
        if (potion == null)
        {
            var available = string.Join(", ", potions.Select((p, i) => $"{i}:{PotionName(p)}"));
            return Fail($"Potion '{query}' not found. Available: {available}");
        }

        potion.Discard();
        return new AgentActionResult(true, $"Discarded potion [{PotionName(potion)}]", 400);
    }

    private static async Task AwaitPotionAsync(UsePotionAction action, PotionModel potion)
    {
        try
        {
            await action.CompletionTask.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Agent] potion [{potion.Id.Entry}] failed: {ex.Message}");
            try { AiTeammateManager.Instance.AiLog.LogError("AGENT", $"药水失败: {ex.Message}"); } catch { }
        }
    }

    private static AgentActionResult EndTurn(AgentSnapshot snap)
    {
        if (snap.Phase != "combat")
            return Fail($"sts2_end_turn only works in combat (current phase: {snap.Phase}).");

        if (!TryLocalPlayer(out var player, out _, out var error))
            return Fail(error);

        var state = CombatManager.Instance.DebugOnlyGetState();
        if (state?.CurrentSide != CombatSide.Player)
            return Fail("It is not the player's turn.");

        CombatManager.Instance.SetReadyToEndTurn(player, canBackOut: false);
        return new AgentActionResult(true, "End turn signalled", 600);
    }

    // ---- Screens ------------------------------------------------------------------------

    private static AgentActionResult Choose(JsonObject args, AgentSnapshot snap)
    {
        var index = IntArg(args, "index");
        var skip = BoolArg(args, "skip");

        return snap.Phase switch
        {
            "map" => ChooseMap(index),
            "rewards" => ChooseReward(index),
            "card_reward" => ChooseCardReward(index, skip),
            "card_choice" => ChooseCardChoice(index, skip),
            "relic_select" => ChooseRelic(index, skip),
            "rest" => ChooseRest(index),
            "event" => ChooseEvent(index),
            "shop" => ChooseShop(index),
            "bundle_select" => ChooseBundle(index),
            "crystal_sphere" => ChooseCrystalSphere(index),
            "card_select" => index is >= 0
                ? SelectCardsIndices(new[] { index.Value })
                : Fail("Missing 'index' for this card selection screen."),
            _ => Fail($"sts2_choose does not apply to phase '{snap.Phase}'.")
        };
    }

    private static AgentActionResult ChooseMap(int? index)
    {
        var screen = NMapScreen.Instance;
        if (screen == null || !GodotObject.IsInstanceValid(screen) || !screen.IsOpen)
            return Fail("The map is not open anymore.");

        var points = AgentSnapshotBuilder.GetMapPoints(screen);
        if (index is not { } i || i < 0 || i >= points.Count)
            return Fail($"Map option index out of range (0..{points.Count - 1}).");

        var point = points[i];
        screen.OnMapPointSelectedLocally(point);
        return new AgentActionResult(true, $"Travel to {ContextBuilder.DescribeMapPoint(point.Point)}", 1200);
    }

    private static AgentActionResult ChooseReward(int? index)
    {
        var screen = NOverlayStack.Instance?.Peek() as NRewardsScreen;
        if (screen == null || !GodotObject.IsInstanceValid(screen))
            return Fail("The rewards screen is not open.");

        var buttons = AgentSnapshotBuilder.GetRewardButtons(screen);
        if (index is not { } i || i < 0 || i >= buttons.Count)
            return Fail($"Reward index out of range (0..{buttons.Count - 1}).");

        var button = buttons[i];
        if (!button.IsEnabled)
            return Fail("That reward is not claimable right now.");

        button.ForceClick();
        return new AgentActionResult(true, $"Claiming reward {i}", 700);
    }

    private static AgentActionResult ChooseCardReward(int? index, bool skip)
    {
        var screen = NOverlayStack.Instance?.Peek() as NCardRewardSelectionScreen;
        if (screen == null || !GodotObject.IsInstanceValid(screen))
            return Fail("The card reward screen is not open.");

        var holders = AgentSnapshotBuilder.GetCardRewardHolders(screen);
        var alternatives = AgentSnapshotBuilder.GetCardRewardAlternatives(screen);

        if (skip)
        {
            if (alternatives.Count == 0)
                return Fail("There is no skip/alternative button on this card reward screen.");
            alternatives[0].ForceClick();
            return new AgentActionResult(true, "Skipping card reward", 500);
        }

        if (index is not { } i || i < 0 || i >= holders.Count + alternatives.Count)
            return Fail($"Card reward index out of range (0..{holders.Count + alternatives.Count - 1}).");

        if (i < holders.Count)
        {
            var holder = holders[i];
            holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
            return new AgentActionResult(true, $"Selected card {i}", 600);
        }

        alternatives[i - holders.Count].ForceClick();
        return new AgentActionResult(true, $"Selected card reward alternative {i - holders.Count}", 500);
    }

    private static AgentActionResult ChooseCardChoice(int? index, bool skip)
    {
        var screen = NOverlayStack.Instance?.Peek() as NChooseACardSelectionScreen;
        if (screen == null || !GodotObject.IsInstanceValid(screen))
            return Fail("The card choice screen is not open.");

        if (skip)
        {
            var skipButton = AgentSnapshotBuilder.FindSkipButton(screen);
            if (skipButton == null)
                return Fail("There is no skip button on this screen.");
            skipButton.ForceClick();
            return new AgentActionResult(true, "Skipped card choice", 500);
        }

        var holders = AgentSnapshotBuilder.GetCardChoiceHolders(screen);
        if (index is not { } i || i < 0 || i >= holders.Count)
            return Fail($"Card choice index out of range (0..{holders.Count - 1}).");

        var holder = holders[i];
        holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
        return new AgentActionResult(true, $"Selected card {i}", 600);
    }

    private static AgentActionResult ChooseRelic(int? index, bool skip)
    {
        var screen = NOverlayStack.Instance?.Peek() as NChooseARelicSelection;
        if (screen == null || !GodotObject.IsInstanceValid(screen))
            return Fail("The relic selection screen is not open.");

        if (skip)
        {
            var skipButton = AgentSnapshotBuilder.FindSkipButton(screen);
            if (skipButton == null)
                return Fail("There is no skip button on this screen.");
            skipButton.ForceClick();
            return new AgentActionResult(true, "Skipped relic", 500);
        }

        var holders = AgentSnapshotBuilder.GetRelicHolders(screen);
        if (index is not { } i || i < 0 || i >= holders.Count)
            return Fail($"Relic index out of range (0..{holders.Count - 1}).");

        holders[i].ForceClick();
        return new AgentActionResult(true, $"Selected relic {i}", 600);
    }

    private static AgentActionResult ChooseRest(int? index)
    {
        if (!TryLocalPlayer(out var player, out _, out var error))
            return Fail(error);

        var options = AgentSnapshotBuilder.GetRestOptions(player);
        if (index is not { } i || i < 0 || i >= options.Count)
            return Fail($"Rest option index out of range (0..{options.Count - 1}).");

        var option = options[i];
        var room = NRestSiteRoom.Instance;
        var button = room != null && GodotObject.IsInstanceValid(room)
            ? room.GetButtonForOption(option)
            : null;

        if (button != null && GodotObject.IsInstanceValid(button))
        {
            button.ForceClick();
        }
        else
        {
            var sync = RunManager.Instance?.RestSiteSynchronizer;
            if (sync == null)
                return Fail("Rest site synchronizer is not available.");
            _ = ChooseRestFallbackAsync(sync, room, i, option);
        }

        return new AgentActionResult(true, $"Rest option: {option.OptionId}", 900);
    }

    private static async Task ChooseRestFallbackAsync(RestSiteSynchronizer sync, NRestSiteRoom? room,
        int index, RestSiteOption option)
    {
        try
        {
            var ok = await sync.ChooseLocalOption(index);
            if (ok && room != null && GodotObject.IsInstanceValid(room))
                room.AfterSelectingOption(option);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Agent] rest choice failed: {ex.Message}");
        }
    }

    private static AgentActionResult ChooseEvent(int? index)
    {
        // Click the option the player would click: it carries the layout's own bookkeeping
        // (dialogue advance, page changes) that the synchronizer call would skip.
        var buttons = AgentSnapshotBuilder.GetEventOptionButtons();
        if (buttons.Count > 0)
        {
            if (index is not { } bi || bi < 0 || bi >= buttons.Count)
                return Fail($"Event option index out of range (0..{buttons.Count - 1}).");

            buttons[bi].ForceClick();
            return new AgentActionResult(true, $"Event option {bi}", 900);
        }

        var dialogue = AgentSnapshotBuilder.FindEventDialogueButton();
        if (dialogue != null)
        {
            dialogue.EmitSignal(NClickableControl.SignalName.Released, dialogue);
            return new AgentActionResult(true, "Advanced the event dialogue", 600);
        }

        return Fail("No event option is clickable right now; call sts2_state again.");
    }

    /// <summary>Bundle screens: pick a bundle, then confirm (or go back). 选包。</summary>
    private static AgentActionResult ChooseBundle(int? index)
    {
        var screen = NOverlayStack.Instance?.Peek() as NChooseABundleSelectionScreen;
        if (screen == null || !GodotObject.IsInstanceValid(screen))
            return Fail("The bundle screen is not open.");

        var preview = screen.GetNodeOrNull<Control>("%BundlePreviewContainer");
        if (preview is { Visible: true })
        {
            var actions = new List<(string Kind, Action Click)>();
            var confirm = screen.GetNodeOrNull<NConfirmButton>("%Confirm");
            if (confirm != null && GodotObject.IsInstanceValid(confirm) && confirm.IsEnabled)
                actions.Add(("confirm", () => confirm.ForceClick()));
            var cancel = screen.GetNodeOrNull<NBackButton>("%Cancel");
            if (cancel != null && GodotObject.IsInstanceValid(cancel) && cancel.IsEnabled)
                actions.Add(("back", () => cancel.ForceClick()));

            if (index is not { } pi || pi < 0 || pi >= actions.Count)
                return Fail($"Bundle preview index out of range (0..{actions.Count - 1}).");

            actions[pi].Click();
            return new AgentActionResult(true, $"Bundle preview: {actions[pi].Kind}", 700);
        }

        var bundles = UiHelper.FindAll<NCardBundle>(screen)
            .Where(b => GodotObject.IsInstanceValid(b) && b.IsVisibleInTree() && b.Bundle != null)
            .ToList();
        if (index is not { } i || i < 0 || i >= bundles.Count)
            return Fail($"Bundle index out of range (0..{bundles.Count - 1}).");

        bundles[i].Hitbox.ForceClick();
        return new AgentActionResult(true, $"Selected bundle {i}", 700);
    }

    /// <summary>Crystal sphere minigame: divine a hidden cell or switch the tool. 水晶球。</summary>
    private static AgentActionResult ChooseCrystalSphere(int? index)
    {
        var screen = NOverlayStack.Instance?.Peek() as NCrystalSphereScreen;
        if (screen == null || !GodotObject.IsInstanceValid(screen))
            return Fail("The crystal sphere screen is not open.");

        var entity = AccessTools.Field(typeof(NCrystalSphereScreen), "_entity")?.GetValue(screen)
            as CrystalSphereMinigame;
        var finished = entity?.IsFinished ?? false;
        var hasTools = !finished && (entity?.DivinationCount ?? 0) > 0;
        var cells = AgentSnapshotBuilder.GetCrystalCells(screen);
        var total = cells.Count + (hasTools ? 2 : 0);

        if (index is not { } i || i < 0 || i >= total)
            return Fail($"Crystal sphere index out of range (0..{total - 1}).");

        if (i < cells.Count)
        {
            var cell = cells[i];
            cell.EmitSignal(NClickableControl.SignalName.Released, cell);
            return new AgentActionResult(true, $"Divined cell ({cell.Entity.X},{cell.Entity.Y})", 700);
        }

        var small = i == cells.Count;
        var button = screen.GetNodeOrNull<NButton>(small ? "%SmallDivinationButton" : "%BigDivinationButton");
        if (button == null || !GodotObject.IsInstanceValid(button))
            return Fail("That divination tool is not available.");

        button.ForceClick();
        return new AgentActionResult(true, $"Switched to {(small ? "small" : "big")} divination", 300);
    }

    private static AgentActionResult ChooseShop(int? index)
    {
        if (!TryLocalPlayer(out _, out _, out var error))
            return Fail(error);

        var room = NMerchantRoom.Instance;
        if (room == null || !GodotObject.IsInstanceValid(room))
            return Fail("The merchant room is not open.");

        var inventory = room.Inventory?.Inventory;
        if (inventory == null)
            return Fail("The merchant inventory is not ready.");

        var entries = AgentSnapshotBuilder.GetShopEntries(room);
        if (index is not { } i || i < 0 || i >= entries.Count)
            return Fail($"Shop index out of range (0..{entries.Count - 1}).");

        var entry = entries[i];
        if (!entry.IsStocked)
            return Fail("That item is out of stock.");
        if (!entry.EnoughGold)
            return Fail($"Not enough gold ({entry.Cost} required).");

        room.OpenInventory();
        _ = BuyAsync(entry, inventory);
        return new AgentActionResult(true, $"Buying {AgentSnapshotBuilder.ShopLabel(entry)}", 800);
    }

    private static async Task BuyAsync(MerchantEntry entry, MerchantInventory inventory)
    {
        try
        {
            await entry.OnTryPurchaseWrapper(inventory, false);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Agent] purchase failed: {ex.Message}");
            try { AiTeammateManager.Instance.AiLog.LogError("AGENT", $"购买失败: {ex.Message}"); } catch { }
        }
    }

    // ---- Card grid selection ------------------------------------------------------------

    private static AgentActionResult SelectCards(JsonObject args, AgentSnapshot snap)
    {
        if (snap.Phase != "card_select")
            return Fail($"sts2_select_cards only works on a card selection screen (current phase: {snap.Phase}).");

        var indices = new List<int>();
        if (args["indices"] is JsonArray array)
        {
            foreach (var node in array)
            {
                if (node == null) continue;
                try { indices.Add(node.GetValue<int>()); } catch { /* ignore */ }
            }
        }

        if (indices.Count == 0)
            return Fail("Missing 'indices' (array of option indices).");

        return SelectCardsIndices(indices);
    }

    private static AgentActionResult SelectCardsIndices(IReadOnlyList<int> indices)
    {
        var screen = NOverlayStack.Instance?.Peek() as NCardGridSelectionScreen;
        if (screen == null || !GodotObject.IsInstanceValid(screen))
            return Fail("The card selection screen is not open.");

        var holders = AgentSnapshotBuilder.GetGridHolders(screen);
        var (min, max) = AgentSnapshotBuilder.GetGridSelectRange(screen);
        var distinct = indices.Distinct().ToList();
        if (distinct.Any(i => i < 0 || i >= holders.Count))
            return Fail($"Card index out of range (0..{holders.Count - 1}).");
        if (distinct.Count < min || distinct.Count > max)
            return Fail($"This screen needs {min}..{max} card(s); got {distinct.Count}.");

        var picked = distinct.Select(i => holders[i].CardModel).Where(c => c != null).Cast<CardModel>().ToList();
        if (picked.Count != distinct.Count)
            return Fail("One of the selected cards is no longer available.");

        var tcs = AccessTools.Field(typeof(NCardGridSelectionScreen), "_completionSource")
            ?.GetValue(screen) as TaskCompletionSource<IEnumerable<CardModel>>;
        if (tcs == null || tcs.Task.IsCompleted)
            return Fail("This card selection is no longer accepting choices.");

        var names = string.Join(", ", picked.Select(c => c.Title ?? c.Id.Entry));
        screen.Visible = false;
        tcs.TrySetResult(picked);
        Callable.From(() =>
        {
            try
            {
                if (GodotObject.IsInstanceValid(screen))
                    NOverlayStack.Instance?.Remove(screen);
            }
            catch { }
        }).CallDeferred();

        return new AgentActionResult(true, $"Selected [{names}]", 600);
    }

    // ---- Proceed ------------------------------------------------------------------------

    private static AgentActionResult Proceed(AgentSnapshot snap)
    {
        return snap.Phase switch
        {
            "map" => Fail("Use sts2_choose to travel instead of sts2_proceed."),
            "combat" => Fail("Use sts2_end_turn to finish the turn."),
            "rewards" => ProceedRewards(),
            "card_reward" => ChooseCardReward(null, skip: true),
            "card_choice" => ChooseCardChoice(null, skip: true),
            "relic_select" => ChooseRelic(null, skip: true),
            "event" => ProceedEvent(),
            "shop" => ProceedShop(),
            "bundle_select" => ProceedBundle(),
            "crystal_sphere" => ProceedCrystalSphere(),
            "treasure" => ProceedTreasure(),
            "game_over" => ProceedGameOver(),
            "room_proceed" => ProceedRoom(),
            _ => ProceedRoom()
        };
    }

    private static AgentActionResult ProceedRewards()
    {
        var screen = NOverlayStack.Instance?.Peek() as NRewardsScreen;
        if (screen == null || !GodotObject.IsInstanceValid(screen))
            return Fail("The rewards screen is not open.");

        var proceed = AgentSnapshotBuilder.FindProceed(screen);
        if (proceed == null)
            return Fail("The proceed button is not available yet.");
        proceed.ForceClick();
        return new AgentActionResult(true, "Leaving rewards", 900);
    }

    private static AgentActionResult ProceedEvent()
    {
        var dialogue = AgentSnapshotBuilder.FindEventDialogueButton();
        if (dialogue != null)
        {
            dialogue.EmitSignal(NClickableControl.SignalName.Released, dialogue);
            return new AgentActionResult(true, "Advanced the event dialogue", 600);
        }

        if (!TryLocalPlayer(out var player, out var state, out var error))
            return Fail(error);

        var runNode = NRun.Instance;
        if (runNode != null && GodotObject.IsInstanceValid(runNode))
        {
            var proceedOption = UiHelper.FindAll<NEventOptionButton>(runNode)
                .FirstOrDefault(b => GodotObject.IsInstanceValid(b)
                                     && b.IsVisibleInTree()
                                     && b.Option != null
                                     && !b.Option.IsLocked
                                     && b.Option.IsProceed);
            if (proceedOption != null)
            {
                proceedOption.ForceClick();
                return new AgentActionResult(true, "Event proceed", 900);
            }
        }

        // Custom layouts (e.g. the fake merchant) only expose the standard proceed button.
        var eventProceed = AgentSnapshotBuilder.FindEnabledProceed();
        if (eventProceed != null)
        {
            eventProceed.ForceClick();
            return new AgentActionResult(true, "Left the event", 900);
        }

        var sync = RunManager.Instance?.EventSynchronizer;
        if (sync == null)
            return Fail("Event synchronizer is not available.");

        try
        {
            var evt = sync.GetEventForPlayer(player);
            var options = evt.CurrentOptions;
            if (options is { Count: 1 } && !options[0].IsLocked)
            {
                sync.ChooseLocalOption(0);
                return new AgentActionResult(true, "Event single-option continue", 900);
            }
        }
        catch (Exception ex)
        {
            return Fail("Could not read the event: " + ex.Message);
        }

        return Fail("Choose an event option with sts2_choose first.");
    }

    /// <summary>Confirm the bundle preview; picking a bundle itself goes through sts2_choose.</summary>
    private static AgentActionResult ProceedBundle()
    {
        var screen = NOverlayStack.Instance?.Peek() as NChooseABundleSelectionScreen;
        if (screen == null || !GodotObject.IsInstanceValid(screen))
            return Fail("The bundle screen is not open.");

        var preview = screen.GetNodeOrNull<Control>("%BundlePreviewContainer");
        if (preview is not { Visible: true })
            return Fail("Pick a bundle with sts2_choose index=<n> first.");

        var confirm = screen.GetNodeOrNull<NConfirmButton>("%Confirm");
        if (confirm == null || !GodotObject.IsInstanceValid(confirm) || !confirm.IsEnabled)
            return Fail("The confirm button is not available yet.");

        confirm.ForceClick();
        return new AgentActionResult(true, "Confirmed the bundle", 800);
    }

    private static AgentActionResult ProceedCrystalSphere()
    {
        var screen = NOverlayStack.Instance?.Peek() as NCrystalSphereScreen;
        if (screen == null || !GodotObject.IsInstanceValid(screen))
            return Fail("The crystal sphere screen is not open.");

        var proceed = screen.GetNodeOrNull<NProceedButton>("%ProceedButton");
        if (proceed == null || !GodotObject.IsInstanceValid(proceed) || !proceed.IsEnabled)
            return Fail("Divinations are still available; divine a cell with sts2_choose first.");

        proceed.ForceClick();
        return new AgentActionResult(true, "Closed the crystal sphere", 900);
    }

    private static AgentActionResult ProceedShop()
    {
        var room = NMerchantRoom.Instance;
        if (room == null || !GodotObject.IsInstanceValid(room))
            return Fail("The merchant room is not open.");

        if (room.Inventory != null && room.Inventory.IsOpen)
        {
            var back = UiHelper.FindFirst<NBackButton>(room);
            if (back == null)
                return Fail("Could not find the shop back button.");
            back.ForceClick();
            return new AgentActionResult(true, "Closed shop inventory", 700);
        }

        var proceed = AgentSnapshotBuilder.FindEnabledProceed();
        if (proceed == null)
            return Fail("The proceed button is not available yet.");
        proceed.ForceClick();
        return new AgentActionResult(true, "Leaving shop", 900);
    }

    private static AgentActionResult ProceedTreasure()
    {
        var chest = AgentSnapshotBuilder.FindTreasureChest(NRun.Instance);
        if (chest == null)
            return Fail("The treasure chest is not openable right now.");
        chest.ForceClick();
        return new AgentActionResult(true, "Opening treasure chest", 900);
    }

    private static AgentActionResult ProceedGameOver()
    {
        var screen = NOverlayStack.Instance?.Peek() as NGameOverScreen;
        if (screen == null || !GodotObject.IsInstanceValid(screen))
            return Fail("The game-over screen is not open.");

        var continueButton = UiHelper.FindFirst<NGameOverContinueButton>(screen);
        if (continueButton is { IsEnabled: true })
        {
            continueButton.ForceClick();
            return new AgentActionResult(true, "Game over: continue", 900);
        }

        var menuButton = UiHelper.FindFirst<NReturnToMainMenuButton>(screen);
        if (menuButton is { Visible: true, IsEnabled: true })
        {
            menuButton.ForceClick();
            return new AgentActionResult(true, "Game over: return to main menu", 900);
        }

        return Fail("The game-over screen is not ready yet.");
    }

    private static AgentActionResult ProceedRoom()
    {
        var proceed = AgentSnapshotBuilder.FindEnabledProceed();
        if (proceed == null)
            return Fail("There is no enabled proceed button right now.");
        proceed.ForceClick();
        return new AgentActionResult(true, "Clicked proceed", 900);
    }

    // ---- Resolution helpers -------------------------------------------------------------

    private static bool TryLocalPlayer(out Player player, out RunState state, out string error)
    {
        player = null!;
        state = null!;
        error = "";

        var run = RunManager.Instance;
        state = run?.DebugOnlyGetState()!;
        if (state == null)
        {
            error = "No run is in progress.";
            return false;
        }

        if (LocalContext.NetId is not ulong netId || state.GetPlayer(netId) is not { } local)
        {
            error = "The local player is not in the run state yet.";
            return false;
        }

        player = local;
        return true;
    }

    private static CardModel? ResolveCard(IReadOnlyList<CardModel> hand, string query)
    {
        query = query.Trim();
        if (int.TryParse(query, out var index))
            return index >= 0 && index < hand.Count ? hand[index] : null;

        return hand.FirstOrDefault(c =>
            string.Equals(c.Title, query, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.Id.Entry, query, StringComparison.OrdinalIgnoreCase))
            ?? hand.FirstOrDefault(c =>
                (c.Title ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)
                || c.Id.Entry.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static PotionModel? ResolvePotion(IReadOnlyList<PotionModel> potions, string query)
    {
        query = query.Trim();
        if (int.TryParse(query, out var index))
            return index >= 0 && index < potions.Count ? potions[index] : null;

        return potions.FirstOrDefault(p =>
            string.Equals(PotionName(p), query, StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.Id.Entry, query, StringComparison.OrdinalIgnoreCase))
            ?? potions.FirstOrDefault(p =>
                PotionName(p).Contains(query, StringComparison.OrdinalIgnoreCase)
                || p.Id.Entry.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static string PotionName(PotionModel potion) =>
        PromptTextHelper.ResolveLocString(potion.Title, potion.Id.Entry);

    private static Creature? ResolveTarget(CombatState? state, Player player, string query, CardModel? card)
    {
        var enemies = state?.Enemies ?? Array.Empty<Creature>();

        if (string.IsNullOrWhiteSpace(query))
        {
            if (card?.TargetType == TargetType.AnyEnemy)
                return enemies.FirstOrDefault(e => e.IsAlive);
            if (card is { TargetType: TargetType.Self or TargetType.AnyPlayer or TargetType.AllAllies })
                return player.Creature;
            return null;
        }

        query = query.Trim();
        if (query is "self" or "me" or "我" or "自己" or "你")
            return player.Creature;

        if (int.TryParse(query, out var index))
            return index >= 0 && index < enemies.Count ? enemies[index] : null;

        return enemies.FirstOrDefault(e =>
            string.Equals(e.Name, query, StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.Monster?.Id.Entry, query, StringComparison.OrdinalIgnoreCase))
            ?? enemies.FirstOrDefault(e =>
                (e.Name ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)
                || (e.Monster?.Id.Entry ?? "").Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static string StringArg(JsonObject args, string name)
    {
        try { return args[name]?.GetValue<string>() ?? ""; }
        catch { return ""; }
    }

    private static int? IntArg(JsonObject args, string name)
    {
        try
        {
            if (args[name] is not { } node) return null;
            return node.GetValue<int>();
        }
        catch
        {
            try { return int.TryParse(args[name]?.GetValue<string>(), out var value) ? value : null; }
            catch { return null; }
        }
    }

    private static bool BoolArg(JsonObject args, string name)
    {
        try { return args[name]?.GetValue<bool>() ?? false; }
        catch { return false; }
    }

    private static AgentActionResult Fail(string message) => new(false, message, 0);
}
