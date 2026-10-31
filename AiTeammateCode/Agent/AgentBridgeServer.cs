using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace AiTeammate.AiTeammateCode.Agent;

/// <summary>
/// Minimal line-delimited JSON server on 127.0.0.1. One request per line:
/// <c>{"id":1,"call":"sts2_state","args":{}}</c>; one response per line.
/// 游戏侧 dsh 桥接的 TCP 服务。
/// </summary>
internal sealed class AgentBridgeServer : IDisposable
{
    private const int MaxLineLength = 1024 * 1024;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly int _port;
    private readonly string _token;
    private readonly bool _allowActions;
    private Task? _acceptLoop;
    private int _connectedClients;

    public AgentBridgeServer(int port, string token, bool allowActions)
    {
        _port = port;
        _token = token ?? "";
        _allowActions = allowActions;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    /// <summary>Number of dsh clients currently attached. 当前已连接的 dsh 客户端数。</summary>
    public int ConnectedClients => Volatile.Read(ref _connectedClients);

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
        MainFile.Logger.Info($"[Agent] bridge listening on 127.0.0.1:{_port}");
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _cts.Dispose(); } catch { }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (_cts.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"[Agent] accept failed: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var pending = new HashSet<Task>();
        var count = Interlocked.Increment(ref _connectedClients);
        MainFile.Logger.Info($"[Agent] client connected ({count} total)");
        try
        {
            using var _ = client;
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            using var writeGate = new SemaphoreSlim(1, 1);

            while (!connectionCts.IsCancellationRequested)
            {
                string? line;
                try { line = await reader.ReadLineAsync(connectionCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (IOException) { break; }

                if (line == null)
                    break; // client closed
                if (line.Length == 0)
                    continue;

                if (line.Length > MaxLineLength)
                {
                    pending.Add(WriteLineAsync(writer, writeGate,
                        ErrorResponse(null, "Request line too long."), connectionCts.Token));
                    continue;
                }

                pending.RemoveWhere(task => task.IsCompleted);
                pending.Add(Task.Run(() => HandleLineAsync(line, writer, writeGate, connectionCts.Token)));
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Agent] client loop failed: {ex.Message}");
        }
        finally
        {
            try { connectionCts.Cancel(); } catch { }
            try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { }
            count = Interlocked.Decrement(ref _connectedClients);
            MainFile.Logger.Info($"[Agent] client disconnected ({count} total)");
        }
    }

    private async Task HandleLineAsync(string line, StreamWriter writer, SemaphoreSlim writeGate,
        CancellationToken ct)
    {
        JsonNode? parsed = null;
        try { parsed = JsonNode.Parse(line); } catch { /* handled below */ }

        if (parsed is not JsonObject request)
        {
            await WriteLineAsync(writer, writeGate, ErrorResponse(null, "Invalid JSON request."), ct);
            return;
        }

        var id = request["id"]?.DeepClone();
        if (_token.Length > 0)
        {
            var provided = StringField(request, "token");
            if (!string.Equals(provided, _token, StringComparison.Ordinal))
            {
                await WriteLineAsync(writer, writeGate, ErrorResponse(id, "Unauthorized."), ct);
                return;
            }
        }

        var call = StringField(request, "call");
        if (string.IsNullOrWhiteSpace(call))
        {
            await WriteLineAsync(writer, writeGate, ErrorResponse(id, "Missing 'call'."), ct);
            return;
        }

        var args = request["args"] as JsonObject ?? new JsonObject();

        // Poll-style calls must not count as "dsh is alive": the plugin asks for a take-over
        // invitation every few seconds even when no session is driving, and it polls for player
        // chat lines every second. Stamping either would keep the silence fallback from ever firing.
        var passive = call is "sts2_takeover" or "sts2_chat_poll" or "sts2_chat_send";

        AgentToolResult result;
        if (!passive)
            AgentBridgeService.BeginClientRequest();
        try
        {
            result = await AgentToolExecutor.ExecuteAsync(call, args, _allowActions, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            result = AgentToolResult.Error(ex.Message);
        }
        finally
        {
            if (!passive)
                AgentBridgeService.EndClientRequest();
        }

        var response = new JsonObject
        {
            ["id"] = id,
            ["ok"] = !result.IsError
        };

        if (result.IsError)
        {
            response["error"] = result.Text;
        }
        else
        {
            var payload = new JsonObject { ["text"] = result.Text };
            if (!string.IsNullOrEmpty(result.ImageBase64))
            {
                payload["image"] = result.ImageBase64;
                payload["mimeType"] = result.ImageMimeType ?? "image/jpeg";
            }
            response["result"] = payload;
        }

        await WriteLineAsync(writer, writeGate, response, ct);
    }

    private static async Task WriteLineAsync(StreamWriter writer, SemaphoreSlim gate, JsonObject response,
        CancellationToken ct)
    {
        var text = response.ToJsonString(AgentJson.Options);
        try
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { await writer.WriteLineAsync(text.AsMemory(), ct).ConfigureAwait(false); }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static JsonObject ErrorResponse(JsonNode? id, string error) => new()
    {
        ["id"] = id,
        ["ok"] = false,
        ["error"] = error
    };

    private static string StringField(JsonObject obj, string name)
    {
        try { return obj[name]?.GetValue<string>() ?? ""; }
        catch { return ""; }
    }
}
