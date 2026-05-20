namespace AiTeammate.AiTeammateCode.Backend;

public class BackendManager
{
    private readonly Dictionary<string, IAiBackend> _backends = new();
    private IAiBackend? _active;

    public IAiBackend? Active => _active;
    public IReadOnlyDictionary<string, IAiBackend> All => _backends;

    public void Register(IAiBackend backend)
    {
        _backends[backend.BackendName] = backend;
    }

    public void SwitchTo(string name)
    {
        if (_backends.TryGetValue(name, out var backend))
            _active = backend;
    }

    public async Task<bool> TestConnectionAsync(string name, CancellationToken ct)
    {
        if (!_backends.TryGetValue(name, out var backend))
            return false;
        return await backend.ValidateConnectionAsync(ct);
    }

    public async Task<AiResponse> SendAsync(AiRequest request, CancellationToken ct)
    {
        if (_active == null)
            return new AiResponse { IsSuccess = false, ErrorMessage = "No active backend" };

        // Fast-fail: if backend is not available (no API key configured, etc.),
        // don't waste time on an HTTP call that will fail.
        if (!_active.IsAvailable)
            return new AiResponse { IsSuccess = false, ErrorMessage = $"Backend '{_active.BackendName}' is not configured (missing API key)" };

        // Log outgoing request
        AiTrafficLogger.Instance.LogRequest(
            request.Type.ToString(),
            request.SystemPrompt,
            request.UserPrompt,
            request.ConversationHistory,
            request.MaxTokens,
            request.Temperature);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await _active.SendAsync(request, ct);
        sw.Stop();

        // Log incoming response
        if (response.IsSuccess)
        {
            AiTrafficLogger.Instance.LogResponse(
                request.Type.ToString(),
                true,
                response.Content,
                response.TokensUsed,
                sw.ElapsedMilliseconds);
        }
        else
        {
            AiTrafficLogger.Instance.LogError(
                request.Type.ToString(),
                response.ErrorMessage);
        }

        return response;
    }

    public IAsyncEnumerable<string> SendStreamingAsync(AiRequest request, CancellationToken ct)
    {
        if (_active == null)
            throw new InvalidOperationException("No active backend configured. Please set up an AI backend in settings.");
        if (!_active.IsAvailable)
            throw new InvalidOperationException($"Backend '{_active.BackendName}' is not configured (missing API key). Please set up an AI backend in settings.");
        return _active.SendStreamingAsync(request, ct);
    }
}
