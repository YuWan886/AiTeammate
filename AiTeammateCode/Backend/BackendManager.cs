using AiTeammate.AiTeammateCode.Settings;

namespace AiTeammate.AiTeammateCode.Backend;

/// <summary>
/// Holds the configured backends and rebuilds them whenever the relevant settings change, so
/// editing an API key or model in the settings page takes effect without restarting the game.
/// </summary>
public class BackendManager
{
    private static readonly string[] Names = ["DeepSeek", "OpenAI", "Ollama"];

    private readonly Dictionary<string, IAiBackend> _backends = new(StringComparer.Ordinal);
    private string _fingerprint = "";

    public IAiBackend? Active { get; private set; }

    /// <summary>Rebuilds backend instances if any connection-relevant setting changed. 同步设置。</summary>
    public void SyncFromSettings()
    {
        var settings = AiSettingsStore.Current;
        var fingerprint = string.Join('|',
            settings.ActiveBackend,
            settings.DeepSeekApiKey, settings.DeepSeekModel, settings.DeepSeekBaseUrl,
            settings.OpenAiApiKey, settings.OpenAiModel, settings.OpenAiBaseUrl,
            settings.OllamaModel, settings.OllamaBaseUrl,
            settings.EnableModelThinking);

        if (fingerprint == _fingerprint)
            return;

        _fingerprint = fingerprint;

        var oldBackends = _backends.Values.ToList();
        _backends.Clear();

        foreach (var name in Names)
        {
            var backend = BackendFactory.Create(name, settings);
            if (backend != null)
                _backends[name] = backend;
        }

        Active = _backends.TryGetValue(settings.ActiveBackend, out var active) ? active : null;

        // Drop pooled HTTP clients from the replaced instances.
        foreach (var old in oldBackends.OfType<IDisposable>())
            old.Dispose();
    }

    public async Task<AiResponse> SendAsync(AiRequest request, CancellationToken ct)
    {
        SyncFromSettings();

        var active = Active;
        if (active == null)
            return new AiResponse { IsSuccess = false, ErrorMessage = "No active backend" };

        // Fast-fail: missing API key should not wait for an HTTP timeout.
        if (!active.IsAvailable)
            return new AiResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Backend '{active.BackendName}' is not configured (missing API key)"
            };

        AiTrafficLogger.Instance.LogRequest(
            request.Type.ToString(),
            request.SystemPrompt,
            request.UserPrompt,
            request.ConversationHistory,
            request.MaxTokens,
            request.Temperature);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await active.SendAsync(request, ct);
        sw.Stop();

        if (response.IsSuccess)
        {
            AiTrafficLogger.Instance.LogResponse(
                request.Type.ToString(), true, response.Content, response.TokensUsed, sw.ElapsedMilliseconds);
        }
        else
        {
            AiTrafficLogger.Instance.LogError(request.Type.ToString(), response.ErrorMessage);
        }

        return response;
    }
}
