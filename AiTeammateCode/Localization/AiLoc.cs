using MegaCrit.Sts2.Core.Localization;
using STS2RitsuLib;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils;

namespace AiTeammate.AiTeammateCode.Localization;

/// <summary>
/// Mod localization. Strings live in <c>AiTeammate/localization/eng.json</c> and <c>zhs.json</c>
/// inside the mod's <c>.pck</c>; RitsuLib's <see cref="I18N" /> loads the language that matches the
/// game locale and falls back to English for unsupported languages.
/// </summary>
public static class AiLoc
{
    private const string PckFolder = "res://" + MainFile.ModId + "/localization";

    private static readonly Lazy<I18N> Loc = new(Create);

    /// <summary>Normalized game language code (for example <c>eng</c> or <c>zhs</c>).</summary>
    public static string LanguageCode => I18N.ResolveCurrentLanguageCode();

    public static bool IsChinese => LanguageCode is "zhs" or "zht";

    /// <summary>
    /// True once the game's <see cref="LocManager" /> exists. Before that point the only locale
    /// signal is the OS locale, so language-dependent data (built-in personalities) must wait.
    /// </summary>
    public static bool GameLocaleReady
    {
        get
        {
            try { return LocManager.Instance != null; }
            catch { return false; }
        }
    }

    /// <summary>Looks up a localized string; returns <paramref name="fallback"/> when missing.</summary>
    public static string T(string key, string fallback) => Loc.Value.Get(key, fallback);

    /// <summary>
    /// Looks up a localized string and replaces <c>{Name}</c>-style placeholders.
    /// </summary>
    public static string T(string key, string fallback, params (string Name, string Value)[] args)
    {
        return Format(Loc.Value.Get(key, fallback), args);
    }

    /// <summary>RitsuLib settings text that follows the game locale.</summary>
    public static ModSettingsText Settings(string key, string fallback) =>
        ModSettingsText.I18N(Loc.Value, key, fallback);

    /// <summary>Picks a Chinese or English fallback for a non-localized language.</summary>
    public static string Pick(string chinese, string english) => IsChinese ? chinese : english;

    public static string Format(string template, params (string Name, string Value)[] args)
    {
        if (string.IsNullOrEmpty(template) || args.Length == 0)
            return template;

        foreach (var (name, value) in args)
            template = template.Replace("{" + name + "}", value, StringComparison.Ordinal);

        return template;
    }

    private static I18N Create() =>
        RitsuLibFramework.CreateLocalization(
            instanceName: MainFile.ModId + ".I18N",
            fileSystemFolders: Array.Empty<string>(),
            resourceFolders: Array.Empty<string>(),
            pckFolders: new[] { PckFolder },
            resourceAssembly: typeof(AiLoc).Assembly);
}
