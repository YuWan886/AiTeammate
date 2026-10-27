namespace AiTeammate.AiTeammateCode.Backend;

public interface IAiBackend : IDisposable
{
    string BackendName { get; }
    string BackendVersion { get; }
    bool IsAvailable { get; }

    Task<AiResponse> SendAsync(AiRequest request, CancellationToken ct);
    Task<bool> ValidateConnectionAsync(CancellationToken ct);

    /// <summary>Asks the endpoint which models it currently exposes. 获取端点可用模型列表。</summary>
    Task<ModelListResult> ListModelsAsync(CancellationToken ct);
}
