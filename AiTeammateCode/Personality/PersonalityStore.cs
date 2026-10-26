using STS2RitsuLib;
using STS2RitsuLib.Data;
using STS2RitsuLib.Utils.Persistence;
using AiTeammate.AiTeammateCode.Localization;

namespace AiTeammate.AiTeammateCode.Personality;

/// <summary>Persisted personality profile collection (RitsuLib ModDataStore, Global scope).</summary>
public sealed class PersonalityData
{
    /// <summary>
    /// Shipped built-in definitions this file was last synced to. 0 means "never synced"
    /// (fresh install), in which case existing profiles are left alone.
    /// </summary>
    public int BuiltInsVersion { get; set; }

    /// <summary>
    /// Language the built-in profiles were last seeded in. When the game language changes,
    /// built-in text is re-seeded in the new language (custom profiles are untouched).
    /// </summary>
    public string BuiltInsLanguage { get; set; } = "";

    public List<PersonalityProfile> Profiles { get; set; } = new();
}

/// <summary>
/// Owns personality profiles. Profiles are content data (not settings), but they are persisted
/// through RitsuLib so the settings page can bind to them like any other value.
/// </summary>
public static class PersonalityStore
{
    public const string DataKey = "personalities";
    public const string FileName = "personalities.json";

    private sealed record BuiltInText(string Name, string Backstory, string Philosophy, string Style, string Principles);

    private static readonly string[] BuiltInIds = ["tactician", "aggressive", "cautious", "chaos-seeker"];

    private static BuiltInText LoadBuiltIn(string id) => new(
        AiLoc.T($"personality.{id}.name", id),
        AiLoc.T($"personality.{id}.backstory", ""),
        AiLoc.T($"personality.{id}.philosophy", ""),
        AiLoc.T($"personality.{id}.style", ""),
        AiLoc.T($"personality.{id}.principles", ""));

    public static PersonalityData Data =>
        RitsuLibFramework.GetDataStore(MainFile.ModId).Get<PersonalityData>(DataKey);

    public static IReadOnlyList<PersonalityProfile> All => Data.Profiles;

    public static PersonalityProfile? Active
    {
        get
        {
            var data = Data;
            var activeId = Settings.AiSettingsStore.Current.ActivePersonalityId;
            return data.Profiles.FirstOrDefault(p => p.Id == activeId)
                   ?? data.Profiles.FirstOrDefault();
        }
    }

    public static void RegisterData()
    {
        ModDataStore.For(MainFile.ModId).Register(
            key: DataKey,
            fileName: FileName,
            scope: SaveScope.Global,
            defaultFactory: static () => new PersonalityData(),
            autoCreateIfMissing: true);
    }

    /// <summary>
    /// Bumped whenever the shipped built-in text changes. Existing files are refreshed only when
    /// they were already synced to an older schema, so player edits to built-ins are not lost on
    /// every launch.
    /// </summary>
    private const int CurrentBuiltInsVersion = 1;

    /// <summary>
    /// Restores missing built-in profiles and (on a definition-schema bump) refreshes built-in
    /// text. Custom profiles and player edits are left untouched. Idempotent; runs on every startup.
    /// </summary>
    public static void EnsureSeeded()
    {
        // Mod initialization happens before LocManager: the OS locale would be wrong and would
        // clobber localized built-ins. Seeding runs later (game ready / settings page build).
        if (!AiLoc.GameLocaleReady)
            return;

        var data = Data;
        var language = AiLoc.LanguageCode;
        var refreshDefinitions = data.BuiltInsVersion > 0 &&
                                 (data.BuiltInsVersion < CurrentBuiltInsVersion ||
                                  !string.Equals(data.BuiltInsLanguage, language, StringComparison.OrdinalIgnoreCase));

        foreach (var id in BuiltInIds)
        {
            var builtIn = LoadBuiltIn(id);
            var existing = data.Profiles.FirstOrDefault(p => p.Id == id);
            if (existing == null)
            {
                data.Profiles.Add(new PersonalityProfile
                {
                    Id = id,
                    Name = builtIn.Name,
                    IsPreset = true,
                    IsBuiltIn = true,
                    Backstory = builtIn.Backstory,
                    CombatPhilosophy = builtIn.Philosophy,
                    CommunicationStyle = builtIn.Style,
                    DecisionPrinciples = builtIn.Principles
                });
            }
            else if (existing.IsBuiltIn && refreshDefinitions)
            {
                existing.Name = builtIn.Name;
                existing.Backstory = builtIn.Backstory;
                existing.CombatPhilosophy = builtIn.Philosophy;
                existing.CommunicationStyle = builtIn.Style;
                existing.DecisionPrinciples = builtIn.Principles;
                existing.IsPreset = true;
            }
        }

        data.BuiltInsVersion = CurrentBuiltInsVersion;
        data.BuiltInsLanguage = language;

        var activeId = Settings.AiSettingsStore.Current.ActivePersonalityId;
        if (data.Profiles.All(p => p.Id != activeId) && data.Profiles.Count > 0)
            Settings.AiSettingsStore.Current.ActivePersonalityId = data.Profiles[0].Id;

        Save();
    }

    public static PersonalityProfile Create(string name)
    {
        var data = Data;
        var profile = new PersonalityProfile
        {
            Id = $"custom_{Guid.NewGuid().ToString("N")[..6]}",
            Name = name,
            IsPreset = false,
            IsBuiltIn = false,
            EnableGrowth = true
        };
        data.Profiles.Add(profile);
        Settings.AiSettingsStore.Current.ActivePersonalityId = profile.Id;
        Save();
        return profile;
    }

    /// <summary>Deletes a custom profile. Built-in profiles cannot be deleted. 删除自定义人格。</summary>
    public static bool Delete(string id)
    {
        var data = Data;
        var profile = data.Profiles.FirstOrDefault(p => p.Id == id);
        if (profile == null || profile.IsBuiltIn)
            return false;

        data.Profiles.Remove(profile);
        if (Settings.AiSettingsStore.Current.ActivePersonalityId == id && data.Profiles.Count > 0)
            Settings.AiSettingsStore.Current.ActivePersonalityId = data.Profiles[0].Id;
        Save();
        return true;
    }

    /// <summary>Restores a built-in profile's text fields to the shipped definition. 恢复内置人格。</summary>
    public static bool RestoreBuiltIn(string id)
    {
        if (!BuiltInIds.Contains(id, StringComparer.Ordinal))
            return false;

        var builtIn = LoadBuiltIn(id);

        var profile = Data.Profiles.FirstOrDefault(p => p.Id == id);
        if (profile == null || !profile.IsBuiltIn)
            return false;

        profile.Name = builtIn.Name;
        profile.Backstory = builtIn.Backstory;
        profile.CombatPhilosophy = builtIn.Philosophy;
        profile.CommunicationStyle = builtIn.Style;
        profile.DecisionPrinciples = builtIn.Principles;
        Save();
        return true;
    }

    public static void Save() =>
        RitsuLibFramework.GetDataStore(MainFile.ModId).Save(DataKey);
}
