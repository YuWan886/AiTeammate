using Godot;
using HarmonyLib;
using AiTeammate.AiTeammateCode.Chat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Nodes.Vfx.Utilities;
using MegaCrit.Sts2.Core.Runs;
using AiTeammate.AiTeammateCode.Solo;
using AiTeammate.AiTeammateCode.Localization;
using AiTeammate.AiTeammateCode.Personality;

namespace AiTeammate.AiTeammateCode.Patches;

[HarmonyPatch(typeof(NGame), "_Ready")]
public static class NGamePatch
{
    private sealed class DragState
    {
        public bool Dragging;
        public bool Moved;
        public bool SuppressNextClick;
        public Vector2 Offset;
    }

    private static Panel? _chatRoot;
    private static VBoxContainer? _messageContainer;
    private static LineEdit? _inputField;
    private static ScrollContainer? _scrollContainer;
    private static Button? _minimizedButton;
    private static Button? _pauseButton;
    private static bool _chatMinimized;
    private static Action<ChatMessage>? _currentHandler;
    private static readonly DragState _panelDragState = new();
    private static readonly DragState _buttonDragState = new();

    private const float ChatWidthRatio = 0.22f;
    private const float ChatHeightRatio = 0.35f;
    private const float ChatLeftRatio = 0.01f;
    private const float ChatTopRatio = 0.45f;
    private const double BubbleSecondsPerCharacter = 0.12;
    private const double BubbleMinSeconds = 1.5;

    public static void RefreshChatUi()
    {
        var game = NGame.Instance;
        if (game == null || !GodotObject.IsInstanceValid(game))
            return;

        AfterGameReady(game);
    }

    /// <summary>聊天面板仅在多人模式有 AI 队友、或单人爬塔由 AI 接管时显示。</summary>
    private static bool ShouldShowChatPanel()
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized)
            return false;

        // Solo climb: the local player is AI-controlled, so the chat panel is the only way to talk.
        SoloRunController.TryBind(manager);
        if (manager.IsSoloControlled)
            return true;

        var netType = RunManager.Instance?.NetService?.Type;
        if (netType == null || netType == NetGameType.Singleplayer)
            return false;

        if (manager.AiPlayer == null)
        {
            manager.DetectAndBindAiPlayer();
            if (manager.AiPlayer == null)
                return false;
        }

        return true;
    }

    [HarmonyPostfix]
    public static void AfterGameReady(NGame __instance)
    {
        Compat.MainThread.Capture();

        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized)
            manager.Initialize();

        // The content cache needs ModelDb, which only exists once the game is ready (an early
        // Initialize from the settings page cannot build it).
        manager.CacheService?.EnsureLoaded();

        // Locale is finalized by now; refresh built-in personalities when the language changed.
        PersonalityStore.EnsureSeeded();

        if (!ShouldShowChatPanel())
        {
            if (_chatRoot != null && GodotObject.IsInstanceValid(_chatRoot))
                _chatRoot.Visible = false;
            if (_minimizedButton != null && GodotObject.IsInstanceValid(_minimizedButton))
                _minimizedButton.Visible = false;
            return;
        }

        if (_chatRoot == null || !GodotObject.IsInstanceValid(_chatRoot) || !_chatRoot.IsInsideTree())
        {
            try
            {
                BuildChatUi(__instance);
                MainFile.Logger.Info("聊天面板已创建并挂载");
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"创建聊天面板失败: {ex.Message}");
            }
        }
        else if (_chatMinimized)
        {
            _chatRoot.Visible = false;
            if (_minimizedButton != null && GodotObject.IsInstanceValid(_minimizedButton))
                _minimizedButton.Visible = true;
        }
        else
        {
            _chatRoot.Visible = true;
            if (_minimizedButton != null && GodotObject.IsInstanceValid(_minimizedButton))
                _minimizedButton.Visible = false;
        }
    }

    private static void BuildChatUi(NGame gameRoot)
    {
        if (_currentHandler != null)
            AiTeammateManager.Instance.ChatManager.OnMessageReceived -= _currentHandler;

        var viewportSize = GetViewportSize();
        var panelSize = new Vector2(viewportSize.X * ChatWidthRatio, viewportSize.Y * ChatHeightRatio);
        var initialPosition = new Vector2(viewportSize.X * ChatLeftRatio, viewportSize.Y * ChatTopRatio);

        _chatRoot = new Panel();
        _chatRoot.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        _chatRoot.Position = initialPosition;
        _chatRoot.Size = panelSize;
        _chatRoot.MouseFilter = Control.MouseFilterEnum.Stop;

        var styleBox = new StyleBoxFlat();
        styleBox.BgColor = new Color(0.05f, 0.05f, 0.1f, 0.85f);
        styleBox.BorderWidthLeft = 1;
        styleBox.BorderWidthRight = 1;
        styleBox.BorderWidthTop = 1;
        styleBox.BorderWidthBottom = 1;
        styleBox.BorderColor = new Color(0.3f, 0.5f, 0.3f, 0.8f);
        styleBox.CornerRadiusTopLeft = 6;
        styleBox.CornerRadiusTopRight = 6;
        styleBox.CornerRadiusBottomLeft = 6;
        styleBox.CornerRadiusBottomRight = 6;
        _chatRoot.AddThemeStyleboxOverride("panel", styleBox);

        var outerMargin = new MarginContainer();
        outerMargin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        outerMargin.AddThemeConstantOverride("margin_left", 4);
        outerMargin.AddThemeConstantOverride("margin_right", 4);
        outerMargin.AddThemeConstantOverride("margin_top", 2);
        outerMargin.AddThemeConstantOverride("margin_bottom", 4);
        _chatRoot.AddChild(outerMargin);

        var mainVbox = new VBoxContainer();
        mainVbox.AddThemeConstantOverride("separation", 2);
        outerMargin.AddChild(mainVbox);

        var titleBar = new HBoxContainer();
        titleBar.AddThemeConstantOverride("separation", 4);

        var dragArea = new Panel();
        dragArea.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        dragArea.MouseFilter = Control.MouseFilterEnum.Stop;
        var dragStyleBox = new StyleBoxFlat();
        dragStyleBox.BgColor = new Color(0.15f, 0.25f, 0.15f, 0.9f);
        dragStyleBox.CornerRadiusTopLeft = 4;
        dragStyleBox.CornerRadiusTopRight = 4;
        dragArea.AddThemeStyleboxOverride("panel", dragStyleBox);

        var titleLabel = new Label();
        titleLabel.Text = AiLoc.T("chat.title", "AI Teammate");
        titleLabel.HorizontalAlignment = HorizontalAlignment.Left;
        titleLabel.AddThemeColorOverride("font_color", new Color(0.7f, 1f, 0.7f));
        dragArea.AddChild(titleLabel);
        WireDrag(dragArea, _chatRoot, _panelDragState);

        titleBar.AddChild(dragArea);

        var minBtn = new Button();
        minBtn.Text = "─";
        minBtn.CustomMinimumSize = new Vector2(24, 22);
        minBtn.TooltipText = AiLoc.T("chat.minimize", "Minimize");
        minBtn.Pressed += MinimizeChatPanel;
        titleBar.AddChild(minBtn);

        var pauseBtn = new Button();
        pauseBtn.Text = "⏸";
        pauseBtn.CustomMinimumSize = new Vector2(24, 22);
        pauseBtn.TooltipText = AiLoc.T("chat.pause", "Pause AI");
        pauseBtn.Visible = false;
        pauseBtn.Pressed += () => AiFlowControl.ToggleManualPause();
        titleBar.AddChild(pauseBtn);
        _pauseButton = pauseBtn;

        mainVbox.AddChild(titleBar);

        _scrollContainer = new ScrollContainer();
        _scrollContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _scrollContainer.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        mainVbox.AddChild(_scrollContainer);

        _messageContainer = new VBoxContainer();
        _messageContainer.AddThemeConstantOverride("separation", 2);
        _messageContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _scrollContainer.AddChild(_messageContainer);
        _messageContainer.ChildEnteredTree += _ => AutoScrollToBottom();
        _messageContainer.Resized += AutoScrollToBottom;

        var inputHbox = new HBoxContainer();
        inputHbox.AddThemeConstantOverride("separation", 4);
        mainVbox.AddChild(inputHbox);

        _inputField = new LineEdit();
        _inputField.PlaceholderText = AiLoc.T("chat.placeholder", "Type a message...");
        _inputField.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _inputField.CustomMinimumSize = new Vector2(0, 22);
        inputHbox.AddChild(_inputField);

        var sendButton = new Button();
        sendButton.Text = AiLoc.T("chat.send", "Send");
        sendButton.CustomMinimumSize = new Vector2(48, 22);
        inputHbox.AddChild(sendButton);

        _minimizedButton = new Button();
        _minimizedButton.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        _minimizedButton.Position = initialPosition;
        _minimizedButton.Size = new Vector2(120, 30);
        _minimizedButton.Text = AiLoc.T("chat.title", "AI Teammate");
        _minimizedButton.TooltipText = AiLoc.T("chat.drag_hint", "Drag to move / click to expand");
        _minimizedButton.Visible = false;
        _minimizedButton.MouseFilter = Control.MouseFilterEnum.Stop;
        WireDrag(_minimizedButton, _minimizedButton, _buttonDragState);
        _minimizedButton.Pressed += RestoreChatPanel;

        gameRoot.AddChild(_chatRoot);
        gameRoot.AddChild(_minimizedButton);

        var manager = AiTeammateManager.Instance;
        _currentHandler = msg => Callable.From(() => RouteChatMessage(msg)).CallDeferred();
        manager.ChatManager.OnMessageReceived += _currentHandler;

        void OnSend(string text)
        {
            var trimmed = text.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                return;

            _inputField?.Clear();
            _ = manager.ChatManager.HandlePlayerMessage(AiLoc.T("chat.player_sender", "Player"), trimmed, System.Threading.CancellationToken.None);
        }

        sendButton.Pressed += () => OnSend(_inputField?.Text ?? "");
        _inputField.TextSubmitted += OnSend;

        UpdateSoloChatControls();
    }

    /// <summary>Hides the chat panel (solo mode released / run over). 隐藏聊天面板。</summary>
    public static void HideChatPanel()
    {
        if (_chatRoot != null && GodotObject.IsInstanceValid(_chatRoot))
            _chatRoot.Visible = false;
        if (_minimizedButton != null && GodotObject.IsInstanceValid(_minimizedButton))
            _minimizedButton.Visible = false;
    }

    /// <summary>Shows/hides the solo-climb pause button and updates its state. 更新暂停按钮。</summary>
    public static void UpdateSoloChatControls()
    {
        var manager = AiTeammateManager.Instance;
        var solo = manager.IsInitialized && manager.IsSoloControlled;

        if (_pauseButton != null && GodotObject.IsInstanceValid(_pauseButton))
        {
            var paused = AiFlowControl.IsPaused;
            _pauseButton.Visible = solo;
            _pauseButton.Text = paused ? "▶" : "⏸";
            _pauseButton.TooltipText = paused
                ? AiLoc.T("chat.resume", "Resume AI")
                : AiLoc.T("chat.pause", "Pause AI");
            _pauseButton.AddThemeColorOverride("font_color",
                paused ? new Color(1f, 0.75f, 0.3f) : new Color(0.7f, 1f, 0.7f));
        }

        if (_inputField != null && GodotObject.IsInstanceValid(_inputField))
            _inputField.PlaceholderText = solo
                ? AiLoc.T("chat.placeholder.solo", "Talk to the AI (this interrupts it)...")
                : AiLoc.T("chat.placeholder", "Type a message...");
    }

    private static void WireDrag(Control handle, Control target, DragState state)
    {
        handle.GuiInput += evt =>
        {
            if (evt is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    state.Dragging = true;
                    state.Moved = false;
                    state.Offset = target.GlobalPosition - mb.GlobalPosition;
                }
                else
                {
                    state.Dragging = false;
                    state.SuppressNextClick = state.Moved;
                }
            }

            if (evt is InputEventMouseMotion motion && state.Dragging)
            {
                if (!state.Moved && motion.Relative.Length() > 0.5f)
                    state.Moved = true;

                target.GlobalPosition = ClampToViewport(target, motion.GlobalPosition + state.Offset);
            }
        };
    }

    private static Vector2 GetViewportSize()
    {
        var game = NGame.Instance;
        return game == null ? new Vector2(1920f, 1080f) : game.GetViewportRect().Size;
    }

    private static Vector2 ClampToViewport(Control target, Vector2 nextPos)
    {
        var viewportSize = GetViewportSize();
        var maxX = Math.Max(0f, viewportSize.X - target.Size.X);
        var maxY = Math.Max(0f, viewportSize.Y - target.Size.Y);
        return new Vector2(Math.Clamp(nextPos.X, 0f, maxX), Math.Clamp(nextPos.Y, 0f, maxY));
    }

    private static void MinimizeChatPanel()
    {
        if (_chatRoot == null || !GodotObject.IsInstanceValid(_chatRoot) ||
            _minimizedButton == null || !GodotObject.IsInstanceValid(_minimizedButton))
            return;

        _chatMinimized = true;
        _minimizedButton.GlobalPosition = ClampToViewport(_minimizedButton, _chatRoot.GlobalPosition);
        _chatRoot.Visible = false;
        _minimizedButton.Visible = true;
    }

    private static void RestoreChatPanel()
    {
        if (_buttonDragState.SuppressNextClick)
        {
            _buttonDragState.SuppressNextClick = false;
            return;
        }

        if (_chatRoot == null || !GodotObject.IsInstanceValid(_chatRoot) ||
            _minimizedButton == null || !GodotObject.IsInstanceValid(_minimizedButton))
            return;

        _chatMinimized = false;
        _chatRoot.GlobalPosition = ClampToViewport(_chatRoot, _minimizedButton.GlobalPosition);
        _chatRoot.Visible = true;
        _minimizedButton.Visible = false;
    }

    private static void RouteChatMessage(ChatMessage message)
    {
        if (message.Type == ChatMessageType.Normal)
        {
            ShowBubbleChat(message);
            return;
        }

        AddMessageToPanel(message);
    }

    private static void AddMessageToPanel(ChatMessage message)
    {
        if (_messageContainer == null || !GodotObject.IsInstanceValid(_messageContainer))
            return;

        if (message.Type == ChatMessageType.Suggestion)
        {
            var toggleBtn = new Button();
            toggleBtn.Text = $"▶ {message.SenderName}";
            toggleBtn.Flat = true;
            toggleBtn.Alignment = HorizontalAlignment.Left;
            toggleBtn.AddThemeFontSizeOverride("font_size", 11);
            toggleBtn.AddThemeColorOverride("font_color", new Color(0.4f, 0.9f, 0.4f));
            toggleBtn.AddThemeColorOverride("font_hover_color", new Color(0.5f, 1f, 0.5f));
            toggleBtn.AddThemeColorOverride("font_pressed_color", new Color(0.7f, 1f, 0.7f));
            toggleBtn.MouseFilter = Control.MouseFilterEnum.Stop;

            var contentLabel = new Label();
            contentLabel.Text = message.Content;
            contentLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            contentLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            contentLabel.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f));
            contentLabel.AddThemeFontSizeOverride("font_size", 11);
            contentLabel.Visible = false;

            toggleBtn.Pressed += () =>
            {
                contentLabel.Visible = !contentLabel.Visible;
                toggleBtn.Text = $"{(contentLabel.Visible ? "▼" : "▶")} {message.SenderName}";
                AutoScrollToBottom();
            };

            _messageContainer.AddChild(toggleBtn);
            _messageContainer.AddChild(contentLabel);
            AutoScrollToBottom();
            return;
        }

        var prefix = message.Type switch
        {
            ChatMessageType.Warning => "⚠️ ",
            ChatMessageType.SystemInfo => "🔹 ",
            _ => ""
        };

        var nameColor = message.IsAi ? new Color(0.4f, 1f, 0.4f) : new Color(0.4f, 0.6f, 1f);
        var label = new Label();
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.Text = $"[{message.SenderName}] {prefix}{message.Content}";
        label.AddThemeColorOverride("font_color", nameColor);
        label.AddThemeFontSizeOverride("font_size", 12);

        _messageContainer.AddChild(label);
        AutoScrollToBottom();
    }

    private static void ShowBubbleChat(ChatMessage message)
    {
        if (!Settings.AiSettingsStore.Current.EnableSpeechBubble)
            return;

        var speaker = ResolveSpeaker(message);
        if (speaker == null || string.IsNullOrWhiteSpace(message.Content))
            return;

        if (!TryShowCombatBubble(speaker, message.Content))
            TryShowOverlayBubble(speaker, message.Content);

        if (message.IsAi)
            TryShowReaction(speaker, message.Content);
    }

    private static Player? ResolveSpeaker(ChatMessage message)
    {
        if (message.IsAi)
            return AiTeammateManager.Instance.AiPlayer;

        var state = RunManager.Instance?.DebugOnlyGetState();
        return state == null ? null : LocalContext.GetMe(state);
    }

    private static bool TryShowCombatBubble(Player speaker, string text)
    {
        var combatRoom = NCombatRoom.Instance;
        if (combatRoom?.GetCreatureNode(speaker.Creature) == null)
            return false;

        var bubble = NSpeechBubbleVfx.Create(text, speaker.Creature, GetBubbleDuration(text), speaker.Character.SpeechBubbleColor);
        if (bubble == null)
            return false;

        combatRoom.CombatVfxContainer.AddChild(bubble);
        return true;
    }

    private static bool TryShowOverlayBubble(Player speaker, string text)
    {
        var playerState = FindPlayerStateNode(speaker);
        var vfxContainer = NRun.Instance?.GlobalUi?.AboveTopBarVfxContainer;
        if (playerState == null || vfxContainer == null)
            return false;

        var anchor = playerState.GlobalPosition + new Vector2(playerState.Size.X + 16f, playerState.Size.Y * 0.45f);
        var side = anchor.X < GetViewportSize().X * 0.5f ? DialogueSide.Left : DialogueSide.Right;
        var bubble = NSpeechBubbleVfx.Create(text, side, anchor, GetBubbleDuration(text), speaker.Character.SpeechBubbleColor);
        if (bubble == null)
            return false;

        vfxContainer.AddChild(bubble);
        return true;
    }

    private static NMultiplayerPlayerState? FindPlayerStateNode(Player speaker)
    {
        var container = NRun.Instance?.GlobalUi?.MultiplayerPlayerContainer;
        if (container == null)
            return null;

        foreach (var child in container.GetChildren())
        {
            if (child is NMultiplayerPlayerState state && state.Player == speaker)
                return state;
        }

        return null;
    }

    private static void TryShowReaction(Player speaker, string text)
    {
        var reaction = PickReaction(text);
        if (reaction == null || NGame.Instance?.ReactionContainer == null)
            return;

        NGame.Instance.ReactionContainer.DoRemoteReaction(reaction.Value, ResolveReactionPosition(speaker));
    }

    private static Vector2 ResolveReactionPosition(Player speaker)
    {
        if (NCombatRoom.Instance?.GetCreatureNode(speaker.Creature) is { } creatureNode)
            return creatureNode.GlobalPosition + new Vector2(100f, -120f);

        if (FindPlayerStateNode(speaker) is { } playerState)
            return playerState.GlobalPosition + new Vector2(playerState.Size.X + 36f, playerState.Size.Y * 0.5f);

        return _chatRoot?.GlobalPosition + new Vector2(80f, 30f) ?? new Vector2(200f, 200f);
    }

    private static ReactionType? PickReaction(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var lower = text.ToLowerInvariant();

        if (text.Contains('？') || text.Contains('?') || text.Contains("吗") || text.Contains("怎么")
            || lower.Contains("how") || lower.Contains("what") || lower.Contains("why"))
            return ReactionType.QuestionMark;

        if (text.Contains("危险") || text.Contains("小心") || text.Contains("快") || text.Contains("注意")
            || lower.Contains("danger") || lower.Contains("careful") || lower.Contains("watch out"))
            return ReactionType.Exclamation;

        if (text.Contains("糟") || text.Contains("完了") || text.Contains("死") || text.Contains("撑不住")
            || lower.Contains("damn") || lower.Contains("dying") || lower.Contains("can't"))
            return ReactionType.SadSlime;

        if (text.Contains("不行") || text.Contains("别") || text.Contains("算了")
            || lower.Contains("don't") || lower.Contains("stop") || lower.Contains("never mind"))
            return ReactionType.ThumbDown;

        if (text.Contains("好") || text.Contains("稳") || text.Contains("漂亮") || text.Contains("赞")
            || lower.Contains("nice") || lower.Contains("good") || lower.Contains("great")
            || lower.Contains("well played") || lower.Contains("gg"))
            return ReactionType.ThumbUp;

        if (text.Contains("谢谢") || text.Contains("爱") || text.Contains("喜欢")
            || lower.Contains("thanks") || lower.Contains("thank you") || lower.Contains("love"))
            return ReactionType.Heart;

        if (text.Contains("哈哈") || text.Contains("好耶") || text.Contains("赢")
            || lower.Contains("haha") || lower.Contains("lol") || lower.Contains("let's go"))
            return ReactionType.HappyCultist;

        return null;
    }

    private static double GetBubbleDuration(string text)
    {
        var rawLength = string.Concat(text.Where(c => !char.IsWhiteSpace(c))).Length;
        return Math.Max(BubbleMinSeconds, rawLength * BubbleSecondsPerCharacter);
    }

    private static void AutoScrollToBottom()
    {
        if (_scrollContainer == null || !GodotObject.IsInstanceValid(_scrollContainer))
            return;

        Callable.From(ScrollToBottomOnce).CallDeferred();
        Callable.From(ScrollToBottomOnce).CallDeferred();
    }

    private static void ScrollToBottomOnce()
    {
        if (_scrollContainer == null || !GodotObject.IsInstanceValid(_scrollContainer))
            return;

        if (_messageContainer != null && GodotObject.IsInstanceValid(_messageContainer))
        {
            var lastChild = _messageContainer.GetChildCount() > 0
                ? _messageContainer.GetChild(_messageContainer.GetChildCount() - 1) as Control
                : null;
            if (lastChild != null && GodotObject.IsInstanceValid(lastChild))
                _scrollContainer.EnsureControlVisible(lastChild);
        }

        var vScrollBar = _scrollContainer.GetVScrollBar();
        if (vScrollBar != null)
            _scrollContainer.ScrollVertical = (int)vScrollBar.MaxValue;
    }
}
