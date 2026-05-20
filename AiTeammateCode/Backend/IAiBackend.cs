namespace AiTeammate.AiTeammateCode.Backend;

public interface IAiBackend
{
    string BackendName { get; }
    string BackendVersion { get; }
    bool IsAvailable { get; }

    Task<AiResponse> SendAsync(AiRequest request, CancellationToken ct);
    IAsyncEnumerable<string> SendStreamingAsync(AiRequest request, CancellationToken ct);
    Task<bool> ValidateConnectionAsync(CancellationToken ct);
}
