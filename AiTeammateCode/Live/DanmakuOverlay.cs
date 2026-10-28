using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Settings;
using Godot;

namespace AiTeammate.AiTeammateCode.Live;

/// <summary>
/// Full-screen, input-transparent overlay that scrolls live-room danmaku across the game screen.
/// Each line is an avatar + text ("video danmaku" style). Lanes are allocated so lines never
/// overlap; text is trimmed to <see cref="AiSettings.DanmakuMaxChars"/>.
/// 全屏弹幕层：头像 + 文字，按泳道滚动，不拦截鼠标。
/// </summary>
public partial class DanmakuOverlay : Control
{
    private const int MaxQueued = 200;
    private const int MaxActive = 240;
    private const int MaxCachedAvatars = 256;

    /// <summary>Space above the first lane, as a fraction of the screen height. 顶部留白。</summary>
    private const float TopMarginRatio = 0.03f;

    private sealed class ActiveItem
    {
        public Control Root = null!;
        public float Speed;
        public float Width;
        public int Lane;
    }

    private readonly Queue<DanmakuMessage> _pending = new();
    private readonly List<ActiveItem> _active = new();
    private readonly Dictionary<int, ActiveItem> _laneTail = new();
    private readonly Dictionary<string, Texture2D> _avatars = new();
    private readonly HashSet<string> _avatarsLoading = new(StringComparer.Ordinal);

    private int _nextLane;
    private float _lastWidth = -1;
    private float _lastHeight = -1;

    public DanmakuOverlay()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // Draw above room screens and their overlays without stealing input.
        ZIndex = 100;
        SetProcess(true);
    }

    /// <summary>Queues a line for scrolling; ignored while the overlay is hidden. 添加一条弹幕。</summary>
    public void Enqueue(DanmakuMessage message)
    {
        if (message == null || string.IsNullOrWhiteSpace(message.Text) || !Visible)
            return;

        if (_pending.Count >= MaxQueued)
            _pending.Dequeue();
        _pending.Enqueue(message);
    }

    public void Clear()
    {
        _pending.Clear();
        foreach (var item in _active)
        {
            if (GodotObject.IsInstanceValid(item.Root))
                item.Root.QueueFree();
        }
        _active.Clear();
        _laneTail.Clear();
        _nextLane = 0;
    }

    public override void _Process(double delta)
    {
        var settings = AiSettingsStore.Current;
        if (!settings.EnableBilibiliDanmaku || !Visible)
            return;

        var viewport = GetViewportRect().Size;
        if (viewport.X <= 1 || viewport.Y <= 1)
            return;

        if (Math.Abs(viewport.X - _lastWidth) > 1 || Math.Abs(viewport.Y - _lastHeight) > 1)
        {
            _lastWidth = viewport.X;
            _lastHeight = viewport.Y;
            // Keep in-flight lines, but let future lines pick lanes for the new size.
            _laneTail.Clear();
        }

        var dt = (float)delta;
        MoveItems(viewport, settings, dt);
        TrySpawn(viewport, settings);
    }

    private void MoveItems(Vector2 viewport, AiSettings settings, float delta)
    {
        var speed = Math.Max(30f, settings.DanmakuSpeed * (viewport.Y / 1080f));
        var leftToRight = settings.DanmakuMoveDirection == DanmakuDirection.LeftToRight;

        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var item = _active[i];
            if (!GodotObject.IsInstanceValid(item.Root))
            {
                _active.RemoveAt(i);
                continue;
            }

            var movement = item.Speed * delta * (leftToRight ? 1f : -1f);
            item.Root.Position += new Vector2(movement, 0);

            var x = item.Root.Position.X;
            var finished = leftToRight ? x > viewport.X : x + item.Width < 0;
            if (!finished)
                continue;

            if (_laneTail.TryGetValue(item.Lane, out var tail) && ReferenceEquals(tail, item))
                _laneTail.Remove(item.Lane);

            item.Root.QueueFree();
            _active.RemoveAt(i);
        }
    }

    private void TrySpawn(Vector2 viewport, AiSettings settings)
    {
        if (_pending.Count == 0)
            return;

        var lanes = GetLaneCount(viewport, settings);
        if (lanes <= 0)
            return;

        // Probe each lane once per frame; a busy frame simply leaves the line queued.
        for (var probe = 0; probe < lanes && _pending.Count > 0; probe++)
        {
            var lane = _nextLane % lanes;
            _nextLane = (lane + 1) % lanes;
            if (!IsLaneFree(lane, viewport, settings))
                continue;

            Spawn(_pending.Dequeue(), lane, viewport, settings);
            if (_active.Count >= MaxActive)
                break;
        }
    }

    private int GetLaneCount(Vector2 viewport, AiSettings settings)
    {
        var laneHeight = LaneHeight(settings, viewport);
        var area = Math.Clamp(settings.DanmakuAreaPercent, 10, 100) / 100f;
        var usable = Math.Max(laneHeight, viewport.Y * area - viewport.Y * TopMarginRatio);
        return Math.Max(1, (int)(usable / laneHeight));
    }

    /// <summary>Viewport scale so the configured sizes read the same at 1080p and 4K.</summary>
    private static float ViewScale(Vector2 viewport) =>
        Math.Clamp(viewport.Y / 1080f, 0.5f, 4f);

    private static float FontSize(AiSettings settings, Vector2 viewport) =>
        Math.Clamp(settings.DanmakuFontSize * ViewScale(viewport), 10f, 96f);

    private static float LaneHeight(AiSettings settings, Vector2 viewport)
    {
        var avatar = AvatarSize(settings, viewport);
        return Math.Max(FontSize(settings, viewport) + 10f, avatar + 10f);
    }

    private static float AvatarSize(AiSettings settings, Vector2 viewport) =>
        Math.Max(18f, FontSize(settings, viewport) * 1.15f);

    private bool IsLaneFree(int lane, Vector2 viewport, AiSettings settings)
    {
        if (!_laneTail.TryGetValue(lane, out var tail) || tail == null
            || !GodotObject.IsInstanceValid(tail.Root))
            return true;

        var gap = FontSize(settings, viewport) + 44f * ViewScale(viewport);
        if (settings.DanmakuMoveDirection == DanmakuDirection.RightToLeft)
            return tail.Root.Position.X + tail.Width + gap <= viewport.X;

        return tail.Root.Position.X - gap >= 0;
    }

    private void Spawn(DanmakuMessage message, int lane, Vector2 viewport, AiSettings settings)
    {
        var fontSize = FontSize(settings, viewport);
        var avatarSize = AvatarSize(settings, viewport);
        var alpha = Math.Clamp(settings.DanmakuOpacity, 0, 100) / 100f;
        var text = FormatText(message.Text, settings.DanmakuMaxChars);

        var row = new HBoxContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            // Setting a size on a container outside a layout still lays its children out.
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin
        };
        row.AddThemeConstantOverride("separation", 6);

        TextureRect? avatar = null;
        if (settings.DanmakuShowAvatar)
        {
            avatar = new TextureRect
            {
                CustomMinimumSize = new Vector2(avatarSize, avatarSize),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = MouseFilterEnum.Ignore,
                Modulate = new Color(1, 1, 1, alpha)
            };
            row.AddChild(avatar);
        }

        var label = new Label
        {
            Text = text,
            MouseFilter = MouseFilterEnum.Ignore,
            Modulate = new Color(1, 1, 1, alpha)
        };
        var fontSizeInt = (int)MathF.Round(fontSize);
        label.AddThemeFontSizeOverride("font_size", fontSizeInt);
        label.AddThemeColorOverride("font_color", ResolveColor(message));
        label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, Math.Min(1f, alpha + 0.2f)));
        label.AddThemeConstantOverride("outline_size", Math.Max(3, fontSizeInt / 5));
        row.AddChild(label);

        AddChild(row);

        var size = row.GetCombinedMinimumSize();
        row.Size = size;

        var laneHeight = LaneHeight(settings, viewport);
        var top = viewport.Y * TopMarginRatio;
        var y = top + lane * laneHeight;
        var x = settings.DanmakuMoveDirection == DanmakuDirection.LeftToRight
            ? -size.X - 4f
            : viewport.X + 4f;
        row.Position = new Vector2(x, y);

        if (avatar != null && !string.IsNullOrWhiteSpace(message.FaceUrl))
            LoadAvatar(message.FaceUrl, avatar);

        var speed = Math.Max(30f, settings.DanmakuSpeed * (viewport.Y / 1080f));
        var item = new ActiveItem { Root = row, Speed = speed, Width = size.X, Lane = lane };
        _active.Add(item);
        _laneTail[lane] = item;
    }

    private static string FormatText(string raw, int maxChars)
    {
        var builder = new System.Text.StringBuilder(raw.Length);
        var lastWasSpace = false;
        foreach (var ch in raw)
        {
            if (char.IsControl(ch))
                continue;

            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace && builder.Length > 0)
                    builder.Append(' ');
                lastWasSpace = true;
                continue;
            }

            builder.Append(ch);
            lastWasSpace = false;
        }

        var text = builder.ToString().Trim();
        var limit = Math.Clamp(maxChars, 1, 500);
        if (text.Length > limit)
        {
            var cut = limit - 1;
            if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
                cut--;
            text = text[..cut] + "…";
        }
        return text;
    }

    private static Color ResolveColor(DanmakuMessage message)
    {
        if (message.Kind == DanmakuKind.SuperChat)
            return new Color(1f, 0.84f, 0.2f);
        if (message.Kind == DanmakuKind.Gift)
            return new Color(1f, 0.55f, 0.75f);
        if (message.IsSelf)
            return new Color(0.55f, 1f, 0.65f);

        var rgb = message.Color & 0xFFFFFF;
        if (rgb == 0)
            return Colors.White;

        var color = Color.Color8((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        // Near-black text is unreadable over the game; lift it to light grey.
        if (color.R + color.G + color.B < 0.3f)
            return new Color(0.85f, 0.85f, 0.85f);
        return color;
    }

    private void LoadAvatar(string url, TextureRect target)
    {
        if (_avatars.TryGetValue(url, out var cached))
        {
            if (GodotObject.IsInstanceValid(target))
                target.Texture = cached;
            return;
        }

        if (!_avatarsLoading.Add(url))
            return;

        var requestedSize = target.CustomMinimumSize.X;
        _ = Task.Run(async () =>
        {
            var bytes = await BilibiliApi.FetchImageAsync(url, CancellationToken.None);
            MainThread.Post(() =>
            {
                _avatarsLoading.Remove(url);
                if (bytes == null || bytes.Length == 0)
                    return;

                if (_avatars.TryGetValue(url, out var existing))
                {
                    if (GodotObject.IsInstanceValid(target))
                        target.Texture = existing;
                    return;
                }

                var image = new Image();
                if (!TryLoadImage(image, bytes))
                    return;

                var side = (int)Math.Max(16f, requestedSize * 2f);
                image.Resize(side, side, Image.Interpolation.Bilinear);
                var texture = ImageTexture.CreateFromImage(image);

                if (_avatars.Count >= MaxCachedAvatars)
                    _avatars.Clear();
                _avatars[url] = texture;

                if (GodotObject.IsInstanceValid(target))
                    target.Texture = texture;
            });
        });
    }

    /// <summary>
    /// Decodes avatar bytes by sniffing the container first. Bilibili's avatar CDN sometimes answers
    /// with an HTML/JSON body (hotlink protection), and every blind decode attempt makes Godot print
    /// an engine-level <c>ERROR: Condition "err" is true</c> block per danmaku, which floods the log.
    /// </summary>
    private static bool TryLoadImage(Image image, byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return image.LoadJpgFromBuffer(bytes) == Error.Ok;

        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            return image.LoadPngFromBuffer(bytes) == Error.Ok;

        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 // RIFF
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) // WEBP
            return image.LoadWebpFromBuffer(bytes) == Error.Ok;

        return false;
    }
}
