using AiTeammate.AiTeammateCode.Settings;
using AiTeammate.AiTeammateCode.Localization;

namespace AiTeammate.AiTeammateCode.Backend;

/// <summary>
/// Creates backend instances from the live settings. Shared by the manager (normal operation)
/// and the settings page ("test connection" before the manager exists).
/// </summary>
public static class BackendFactory
{
    public static IAiBackend? Create(string name, AiSettings settings)
    {
        return name switch
        {
            "DeepSeek" => new DeepSeekBackend(
                settings.DeepSeekApiKey, settings.DeepSeekModel, settings.DeepSeekBaseUrl, settings.EnableModelThinking),
            "OpenAI" => new OpenAiBackend(
                settings.OpenAiApiKey, settings.OpenAiModel, settings.OpenAiBaseUrl, settings.EnableModelThinking),
            "Ollama" => new OllamaBackend(
                "", settings.OllamaModel, settings.OllamaBaseUrl, settings.EnableModelThinking),
            _ => null
        };
    }

    public static async Task<(bool Success, string Message)> TestConnectionAsync(
        string name, CancellationToken ct)
    {
        var backend = Create(name, AiSettingsStore.Current);
        if (backend == null)
            return (false, AiLoc.T("backend.unknown", "Unknown backend: {Name}", ("Name", name)));

        if (!backend.IsAvailable)
            return (false, AiLoc.T("backend.not_configured", "{Name} has no API key configured", ("Name", name)));

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            var ok = await backend.ValidateConnectionAsync(linked.Token);
            return ok
                ? (true, AiLoc.T("backend.connected", "{Name} connected successfully", ("Name", name)))
                : (false, AiLoc.T("backend.failed", "{Name} connection failed", ("Name", name)));
        }
        catch (Exception ex)
        {
            return (false, AiLoc.T("backend.failed_detail", "{Name} connection failed: {Message}",
                ("Name", name), ("Message", ex.Message)));
        }
    }
}
