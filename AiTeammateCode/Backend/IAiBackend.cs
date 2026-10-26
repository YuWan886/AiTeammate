namespace AiTeammate.AiTeammateCode.Backend;

public interface IAiBackend : IDisposable
{
    string BackendName { get; }
    string BackendVersion { get; }
    bool IsAvailable { get; }

    Task<AiResponse> SendAsync(AiRequest request, CancellationToken ct);
    Task<bool> ValidateConnectionAsync(CancellationToken ct);
}
