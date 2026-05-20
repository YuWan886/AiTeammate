using System.Text.Encodings.Web;
using System.Text.Json;
using Godot;

namespace AiTeammate.AiTeammateCode.Cache;

public class ContentCacheService
{
    private const int CacheSchemaVersion = 3;
    private const string IndexPath = "user://ai_teammate/cache/content_index.json";
    private const string FullPath = "user://ai_teammate/cache/content_full.json";
    private const string CacheDir = "user://ai_teammate/cache";

    public ContentIndex? Index { get; private set; }
    public ContentFull? Full { get; private set; }
    public bool IsLoaded { get; private set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public void Initialize()
    {
        if (!DirAccess.DirExistsAbsolute(CacheDir))
        {
            var err = DirAccess.MakeDirAbsolute(CacheDir);
            if (err != Error.Ok)
            {
                MainFile.Logger.Warn($"Failed to create cache directory: {CacheDir} (Error: {err})");
                return;
            }
        }

        var currentVersion = GetCacheVersion();

        if (Godot.FileAccess.FileExists(IndexPath) && Godot.FileAccess.FileExists(FullPath))
        {
            try
            {
                using var indexFile = Godot.FileAccess.Open(IndexPath, Godot.FileAccess.ModeFlags.Read);
                Index = JsonSerializer.Deserialize<ContentIndex>(indexFile.GetAsText(), JsonOptions);

                using var fullFile = Godot.FileAccess.Open(FullPath, Godot.FileAccess.ModeFlags.Read);
                Full = JsonSerializer.Deserialize<ContentFull>(fullFile.GetAsText(), JsonOptions);

                if (Index?.GameVersion == currentVersion && Full?.GameVersion == currentVersion)
                {
                    IsLoaded = true;
                    MainFile.Logger.Info($"Content cache loaded (version: {currentVersion})");
                    return;
                }
            }
            catch (JsonException ex)
            {
                MainFile.Logger.Warn($"Cache files corrupted, regenerating: {ex.Message}");
            }
        }

        RegenerateCache(currentVersion);
    }

    public void RegenerateCache(string? gameVersion = null)
    {
        var version = gameVersion ?? GetCacheVersion();
        MainFile.Logger.Info($"Regenerating content cache (version: {version})...");

        var cardGen = new CardCacheGenerator();
        var relicGen = new RelicCacheGenerator();
        var potionGen = new PotionCacheGenerator();
        var enchantGen = new EnchantmentCacheGenerator();

        Index = new ContentIndex
        {
            GameVersion = version,
            Cards = cardGen.GenerateIndex(),
            Relics = relicGen.GenerateIndex(),
            Potions = potionGen.GenerateIndex(),
            Enchants = enchantGen.GenerateIndex()
        };

        Full = new ContentFull
        {
            GameVersion = version,
            Cards = cardGen.GenerateFull(),
            Relics = relicGen.GenerateFull(),
            Potions = potionGen.GenerateFull(),
            Enchants = enchantGen.GenerateFull()
        };

        SaveToDisk();
        IsLoaded = true;
        MainFile.Logger.Info($"Content cache regenerated: {Index.Cards.Count} cards, {Index.Relics.Count} relics, {Index.Potions.Count} potions, {Index.Enchants.Count} enchants");
    }

    public void SaveToDisk()
    {
        if (!DirAccess.DirExistsAbsolute(CacheDir))
            DirAccess.MakeDirAbsolute(CacheDir);

        if (Index != null)
        {
            using var file = Godot.FileAccess.Open(IndexPath, Godot.FileAccess.ModeFlags.Write);
            file.StoreString(JsonSerializer.Serialize(Index, JsonOptions));
        }
        if (Full != null)
        {
            using var file = Godot.FileAccess.Open(FullPath, Godot.FileAccess.ModeFlags.Write);
            file.StoreString(JsonSerializer.Serialize(Full, JsonOptions));
        }
    }

    private static string GetCacheVersion()
    {
        var gameVersion = System.Reflection.Assembly.GetAssembly(typeof(MegaCrit.Sts2.Core.Nodes.NGame))?
            .GetName().Version?.ToString() ?? "unknown";
        return $"{gameVersion}-cache{CacheSchemaVersion}";
    }
}
