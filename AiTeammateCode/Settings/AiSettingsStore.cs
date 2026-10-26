using STS2RitsuLib;
using STS2RitsuLib.Data;
using STS2RitsuLib.Utils.Persistence;

namespace AiTeammate.AiTeammateCode.Settings;

/// <summary>
/// Registration and live access for <see cref="AiSettings"/> through RitsuLib's ModDataStore.
/// All services read <see cref="Current"/> at use time so settings take effect immediately.
/// </summary>
public static class AiSettingsStore
{
    public const string DataKey = "settings";
    public const string FileName = "settings.json";

    /// <summary>The live settings instance (mutated in place by the settings page). 当前设置实例。</summary>
    public static AiSettings Current =>
        RitsuLibFramework.GetDataStore(MainFile.ModId).Get<AiSettings>(DataKey);

    /// <summary>Registers the data slot. Call inside <c>RitsuLibFramework.BeginModDataRegistration</c>.</summary>
    public static void RegisterData()
    {
        ModDataStore.For(MainFile.ModId).Register(
            key: DataKey,
            fileName: FileName,
            scope: SaveScope.Global,
            defaultFactory: static () => new AiSettings(),
            autoCreateIfMissing: true);
    }

    /// <summary>Persists the live settings. 持久化当前设置。</summary>
    public static void Save()
    {
        RitsuLibFramework.GetDataStore(MainFile.ModId).Save(DataKey);
    }
}
