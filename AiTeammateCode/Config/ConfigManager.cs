using System.Text.Encodings.Web;
using System.Text.Json;
using Godot;

namespace AiTeammate.AiTeammateCode.Config;

public class ConfigManager
{
    private const string BaseDir = "user://ai_teammate";
    private const string ConfigPath = "user://ai_teammate/config.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public ModConfig Load()
    {
        if (!Godot.FileAccess.FileExists(ConfigPath))
            return CreateAndSaveDefault();

        using var file = Godot.FileAccess.Open(ConfigPath, Godot.FileAccess.ModeFlags.Read);
        var json = file.GetAsText();
        return JsonSerializer.Deserialize<ModConfig>(json, JsonOptions) ?? CreateAndSaveDefault();
    }

    public void Save(ModConfig config)
    {
        if (!DirAccess.DirExistsAbsolute(BaseDir))
            DirAccess.MakeDirAbsolute(BaseDir);

        using var file = Godot.FileAccess.Open(ConfigPath, Godot.FileAccess.ModeFlags.Write);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        file.StoreString(json);
    }

    private ModConfig CreateAndSaveDefault()
    {
        var config = new ModConfig();
        Save(config);
        return config;
    }
}
