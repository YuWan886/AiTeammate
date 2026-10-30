using System.Text;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using AiTeammate.AiTeammateCode.Localization;
using AiTeammate.AiTeammateCode.Personality;
using AiTeammate.AiTeammateCode.Settings;

namespace AiTeammate.AiTeammateCode.Compat;

/// <summary>Outcome of a nickname change, with the text tool callers can return to the model.</summary>
public readonly record struct NicknameChange(bool Ok, string Message, string DisplayName, bool Changed);

/// <summary>
/// One source of truth for the AI teammate's display name: the nickname stored in
/// <see cref="AiSettings.AiNickname"/>, falling back to the active personality's name.
/// The nameplate patch, chat senders, prompts and the dsh/card tools all resolve through here so a
/// rename takes effect everywhere at once.
/// AI 队友显示名的唯一入口：自定义昵称 → 人格名。
/// </summary>
public static class AiNames
{
    public const int MaxNicknameLength = 24;

    /// <summary>Raised on the main thread after the effective display name changed. 昵称变化事件。</summary>
    public static event Action? Changed;

    /// <summary>Raw configured nickname (normalized), or an empty string when none is set.</summary>
    public static string ConfiguredNickname
    {
        get
        {
            try { return Normalize(AiSettingsStore.Current.AiNickname); }
            catch { return ""; }
        }
    }

    public static bool HasCustomNickname => ConfiguredNickname.Length > 0;

    /// <summary>Personality name used when no nickname is set. 未设置昵称时的默认显示名。</summary>
    public static string DefaultName
    {
        get
        {
            try { return PersonalityStore.Active?.Name ?? FallbackName; }
            catch { return FallbackName; }
        }
    }

    private static string FallbackName => AiLoc.T("chat.ai_default_name", "AI Teammate");

    /// <summary>What players see: the nickname when set, otherwise the personality name.</summary>
    public static string DisplayName
    {
        get
        {
            var nickname = ConfiguredNickname;
            return nickname.Length > 0 ? nickname : DefaultName;
        }
    }

    /// <summary>
    /// Removes control characters/newlines, collapses whitespace and caps the length. Returns an
    /// empty string for "no nickname" so the personality name is used again.
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var sb = new StringBuilder(Math.Min(raw.Length, MaxNicknameLength));
        var pendingSpace = false;
        foreach (var ch in raw)
        {
            if (char.IsControl(ch) || ch == (char)0x200B || ch == (char)0xFEFF)
                continue;

            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(ch);
            if (sb.Length >= MaxNicknameLength)
                break;
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Changes the nickname from the AI itself (chat/tool) or dsh. Empty clears it. Persists the
    /// value, refreshes live UI and tells the other multiplayer clients.
    /// </summary>
    public static NicknameChange SetNickname(string? raw, string source)
    {
        return MainThread.Invoke(() =>
        {
            var name = Normalize(raw);
            AiSettings settings;
            string previous;
            try
            {
                settings = AiSettingsStore.Current;
                previous = Normalize(settings.AiNickname);
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"[Nickname] 无法读取设置: {ex.Message}");
                return new NicknameChange(false, "The nickname could not be changed (settings are unavailable).", "", false);
            }

            var changed = !string.Equals(previous, name, StringComparison.Ordinal);
            if (changed)
            {
                settings.AiNickname = name;
                try
                {
                    AiSettingsStore.Save();
                }
                catch (Exception ex)
                {
                    MainFile.Logger.Warn($"[Nickname] 保存昵称失败: {ex.Message}");
                }

                ApplyLocalChange();
            }

            AiNicknameSync.Broadcast(name);

            var display = name.Length > 0 ? name : DefaultName;
            MainFile.Logger.Info($"[Nickname] {source} 将昵称设为「{name}」(display={display})");
            var message = name.Length > 0
                ? AiLoc.Pick($"你的昵称已改为「{display}」，玩家立刻能看到。",
                    $"Your nickname is now “{display}”; players see it immediately.")
                : AiLoc.Pick($"昵称已清空，恢复为「{display}」。",
                    $"Nickname cleared; you are “{display}” again.");

            return new NicknameChange(true, message, display, changed);
        });
    }

    /// <summary>
    /// The settings page wrote <see cref="AiSettings.AiNickname"/> itself (the binding has no
    /// completion callback); refresh live UI and peers afterwards.
    /// </summary>
    public static void NotifySettingsChanged()
    {
        try
        {
            ApplyLocalChange();
            AiNicknameSync.Broadcast(ConfiguredNickname);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Nickname] 设置变更同步失败: {ex.Message}");
        }
    }

    /// <summary>Applies a nickname received from another client (no re-broadcast). 接收联机昵称。</summary>
    public static void ApplyRemote(string? raw, ulong senderId)
    {
        var name = Normalize(raw);
        try
        {
            var settings = AiSettingsStore.Current;
            if (!string.Equals(Normalize(settings.AiNickname), name, StringComparison.Ordinal))
            {
                settings.AiNickname = name;
                AiSettingsStore.Save();
            }

            MainFile.Logger.Info($"[Nickname] 收到玩家 {senderId} 同步的昵称「{name}」");
            ApplyLocalChange();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Nickname] 应用同步昵称失败: {ex.Message}");
        }
    }

    /// <summary>Refreshes every already-built place that cached the name. 刷新已建好的显示。</summary>
    private static void ApplyLocalChange()
    {
        RefreshNameplates();
        Changed?.Invoke();
    }

    private static void RefreshNameplates()
    {
        try
        {
            var game = NGame.Instance;
            if (game == null || !GodotObject.IsInstanceValid(game))
                return;

            var root = game.GetTree()?.Root;
            if (root == null)
                return;

            var name = DisplayName;
            foreach (var node in Walk(root))
            {
                if (node is not NMultiplayerPlayerState state
                    || state.Player == null
                    || !AiIdentity.IsAi(state.Player.NetId))
                    continue;

                if (state.GetNodeOrNull<MegaLabel>("%NameplateLabel") is { } label)
                    label.SetTextAutoSize(name);
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Nickname] 刷新名牌失败: {ex.Message}");
        }
    }

    private static IEnumerable<Node> Walk(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            yield return child;
            foreach (var descendant in Walk(child))
                yield return descendant;
        }
    }
}
