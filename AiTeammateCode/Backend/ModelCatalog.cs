using AiTeammate.AiTeammateCode.Settings;

namespace AiTeammate.AiTeammateCode.Backend;

/// <summary>
/// Remembers the model list fetched from each configured endpoint for the current game session.
/// The settings page exposes it as a drop-down so picking a model no longer means typing an id,
/// while the plain text field stays available for custom deployments.
/// 当前会话内缓存的端点模型列表，供设置页下拉选择。
/// </summary>
public static class ModelCatalog
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, List<string>> Models = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, DateTime> FetchedAt = new(StringComparer.Ordinal);

    /// <summary>Model ids fetched for <paramref name="backend"/>; empty when never fetched.</summary>
    public static IReadOnlyList<string> Get(string backend)
    {
        lock (Gate)
            return Models.TryGetValue(backend, out var models) ? models.ToList() : Array.Empty<string>();
    }

    public static DateTime? GetFetchedAt(string backend)
    {
        lock (Gate)
            return FetchedAt.TryGetValue(backend, out var at) ? at : null;
    }

    public static void Set(string backend, IEnumerable<string> models)
    {
        var list = models
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (Gate)
        {
            Models[backend] = list;
            FetchedAt[backend] = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Asks the endpoint for its model list and caches the result. Never throws; network errors are
    /// returned in <see cref="ModelListResult.ErrorMessage"/>.
    /// </summary>
    public static async Task<ModelListResult> RefreshAsync(string backend, CancellationToken ct)
    {
        var settings = AiSettingsStore.Current;
        using var backendInstance = BackendFactory.Create(backend, settings);
        if (backendInstance == null)
            return ModelListResult.Fail($"Unknown backend: {backend}");

        var result = await backendInstance.ListModelsAsync(ct);
        if (result.IsSuccess)
            Set(backend, result.Models);

        return result;
    }

    /// <summary>
    /// Heuristic used only to warn the user: known vision-capable families. It never blocks the
    /// screenshot tool — a local/proxy model may support images despite an unfamiliar name.
    /// 仅用于界面提示的视觉模型猜测，不会阻止截图工具。
    /// </summary>
    public static bool LooksVisionCapable(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return false;

        var name = model.ToLowerInvariant();
        string[] markers =
        [
            "gpt-4o", "gpt-4.1", "gpt-5", "o3", "o4", "claude", "gemini",
            "qwen-vl", "qwen2-vl", "qwen2.5-vl", "qwen3-vl", "glm-4v", "glm-4.1v",
            "internvl", "llava", "minicpm-v", "pixtral", "llama-3.2-11b-vision",
            "llama-3.2-90b-vision", "gemma3", "phi-4-multimodal", "yi-vl", "moondream",
            "step-1v", "ernie-4.5-turbo-vl", "grok-4", "vision"
        ];
        return markers.Any(name.Contains);
    }
}
