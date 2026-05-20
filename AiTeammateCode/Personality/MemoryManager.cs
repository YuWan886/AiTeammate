using System.Text.Encodings.Web;
using System.Text.Json;
using Godot;

namespace AiTeammate.AiTeammateCode.Personality;

public class MemoryManager
{
    private const string MemoryDir = "user://ai_teammate/memories";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public PersonalityMemory Load(string profileId)
    {
        var path = GetPath(profileId);
        if (!Godot.FileAccess.FileExists(path))
            return new PersonalityMemory { ProfileId = profileId };

        try
        {
            using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
            var json = file.GetAsText();
            return JsonSerializer.Deserialize<PersonalityMemory>(json, JsonOptions)
                   ?? new PersonalityMemory { ProfileId = profileId };
        }
        catch (JsonException)
        {
            return new PersonalityMemory { ProfileId = profileId };
        }
    }

    public void Save(PersonalityMemory memory)
    {
        if (!DirAccess.DirExistsAbsolute(MemoryDir))
            DirAccess.MakeDirAbsolute(MemoryDir);

        var path = GetPath(memory.ProfileId);
        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write);
        file.StoreString(JsonSerializer.Serialize(memory, JsonOptions));
    }

    public void AddEntry(string profileId, MemoryEntry entry)
    {
        var memory = Load(profileId);
        memory.Entries.Add(entry);
        Save(memory);
    }

    public List<MemoryEntry> GetRelevantMemories(string profileId, int maxCount = 5)
    {
        var memory = Load(profileId);
        return memory.Entries
            .OrderByDescending(m => m.Importance)
            .ThenByDescending(m => m.Timestamp)
            .Take(maxCount)
            .ToList();
    }

    public PersonalityMemory MergeMemories(string profileId, string selfReflection)
    {
        var memory = Load(profileId);
        memory.SelfReflection = selfReflection;
        memory.Entries = memory.Entries
            .OrderByDescending(e => e.Timestamp)
            .Take(50)
            .ToList();
        Save(memory);
        return memory;
    }

    private string GetPath(string profileId) => $"{MemoryDir}/{profileId}.json";
}
