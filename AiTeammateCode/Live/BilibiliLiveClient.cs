using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Godot;

namespace AiTeammate.AiTeammateCode.Live;

/// <summary>
/// Bilibili Live danmaku websocket client built on Godot's <see cref="WebSocketPeer"/>.
/// Must be polled from the main thread (see <see cref="Poll"/>); packets are zlib/brotli
/// compressed and carry their own 16-byte big-endian headers.
/// 基于 Godot WebSocketPeer 的 B 站直播弹幕客户端，须在主线程轮询。
/// </summary>
public sealed class BilibiliLiveClient : IDisposable
{
    private const int OperationHeartbeat = 2;
    private const int OperationHeartbeatReply = 3;
    private const int OperationMessage = 5;
    private const int OperationAuth = 7;
    private const int OperationAuthReply = 8;

    private const double HeartbeatIntervalSeconds = 30;

    // 服务端每 30s 回一次心跳；45s 内没有任何心跳回复说明连接已经半死（NAT/代理掉线），
    // 主动断开重连，否则会一直挂在“已进入直播间”但收不到任何弹幕。
    private const double HeartbeatTimeoutSeconds = 45;

    private WebSocketPeer? _socket;
    private byte[]? _authPacket;
    private bool _authSent;
    private bool _closing;
    private double _heartbeatTimer;
    private double _livenessTimer;
    private long _ownUid;

    public string Status { get; private set; } = "未连接";

    public bool IsOpen => _socket?.GetReadyState() == WebSocketPeer.State.Open;

    public event Action<DanmakuMessage>? MessageReceived;
    public event Action<string>? StatusChanged;
    public event Action<string>? Disconnected;

    /// <summary>Builds the op=7 auth packet for an anonymous (<paramref name="uid"/> 0) or logged-in session.</summary>
    public static byte[] BuildAuthPacket(long roomId, long uid, string token, string buvid)
    {
        var payload = new Dictionary<string, object>
        {
            ["uid"] = uid,
            ["roomid"] = roomId,
            ["protover"] = 3,
            ["platform"] = "web",
            ["type"] = 2,
            ["key"] = token
        };
        if (!string.IsNullOrEmpty(buvid))
            payload["buvid"] = buvid;

        return BuildPacket(OperationAuth, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
    }

    public void Connect(string host, int port, byte[] authPacket, long ownUid)
    {
        DisposeSocket();
        _authPacket = authPacket;
        _authSent = false;
        _closing = false;
        _ownUid = ownUid;
        _heartbeatTimer = 0;
        _livenessTimer = 0;

        var socket = new WebSocketPeer();
        var error = socket.ConnectToUrl($"wss://{host}:{port}/sub");
        if (error != Error.Ok)
        {
            SetStatus($"无法连接弹幕服务器（{error}）");
            socket.Dispose();
            Disconnected?.Invoke(Status);
            return;
        }

        _socket = socket;
        SetStatus($"正在连接 {host}…");
    }

    /// <summary>Advances the websocket; call once per frame from the main thread.</summary>
    public void Poll(double delta)
    {
        var socket = _socket;
        if (socket == null)
            return;

        socket.Poll();
        switch (socket.GetReadyState())
        {
            case WebSocketPeer.State.Open:
                if (!_authSent)
                {
                    if (_authPacket != null)
                        socket.Send(_authPacket);
                    _authSent = true;
                    _livenessTimer = 0;
                    SetStatus("已连接，正在进入直播间…");
                }

                while (socket.GetAvailablePacketCount() > 0)
                {
                    var packet = socket.GetPacket();
                    if (packet is { Length: > 0 })
                        ParsePackets(packet);
                }

                // 收不到心跳回复就认为连接已失效（半开连接 / 代理静默丢弃），强制重连。
                _livenessTimer += delta;
                if (_livenessTimer >= HeartbeatTimeoutSeconds)
                {
                    const string timeout = "弹幕服务器心跳超时，正在重连…";
                    DisposeSocket();
                    SetStatus(timeout);
                    if (!_closing)
                        Disconnected?.Invoke(timeout);
                    return;
                }

                _heartbeatTimer += delta;
                if (_heartbeatTimer >= HeartbeatIntervalSeconds)
                    SendHeartbeat();
                break;

            case WebSocketPeer.State.Closed:
            {
                var code = socket.GetCloseCode();
                var reason = socket.GetCloseReason();
                var detail = string.IsNullOrWhiteSpace(reason) ? $"连接已关闭（{code}）" : $"连接已关闭（{code}: {reason}）";
                DisposeSocket();
                SetStatus(detail);
                if (!_closing)
                    Disconnected?.Invoke(detail);
                break;
            }
        }
    }

    public void Close()
    {
        _closing = true;
        try { _socket?.Close(1000, "bye"); } catch { /* already closed */ }
        DisposeSocket();
    }

    public void Dispose()
    {
        _closing = true;
        DisposeSocket();
    }

    private void DisposeSocket()
    {
        var socket = _socket;
        _socket = null;
        _authPacket = null;
        _authSent = false;
        if (socket == null)
            return;

        try { socket.Close(1000, "bye"); } catch { /* ignore */ }
        socket.Dispose();
    }

    private void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    // ---- protocol ----

    private void SendHeartbeat()
    {
        var socket = _socket;
        if (socket == null || socket.GetReadyState() != WebSocketPeer.State.Open)
            return;

        _heartbeatTimer = 0;
        socket.Send(BuildPacket(OperationHeartbeat, Array.Empty<byte>()));
    }

    private static byte[] BuildPacket(int operation, byte[] body)
    {
        var packet = new byte[16 + body.Length];
        WriteInt32BE(packet, 0, packet.Length);
        WriteInt16BE(packet, 4, 16);
        WriteInt16BE(packet, 6, 1);
        WriteInt32BE(packet, 8, operation);
        WriteInt32BE(packet, 12, 1);
        body.CopyTo(packet, 16);
        return packet;
    }

    private void ParsePackets(byte[] data)
    {
        var offset = 0;
        while (offset + 16 <= data.Length)
        {
            var totalLength = ReadInt32BE(data, offset);
            if (totalLength < 16 || offset + totalLength > data.Length)
                return;

            var headerLength = ReadInt16BE(data, offset + 4);
            var version = ReadInt16BE(data, offset + 6);
            var operation = ReadInt32BE(data, offset + 8);
            if (headerLength < 16 || headerLength > totalLength)
                return;

            var bodyLength = totalLength - headerLength;
            var body = new byte[bodyLength];
            if (bodyLength > 0)
                Buffer.BlockCopy(data, offset + headerLength, body, 0, bodyLength);
            offset += totalLength;

            switch (operation)
            {
                case OperationMessage:
                    if (version == 2)
                        ParsePackets(Decompress(body, brotli: false));
                    else if (version == 3)
                        ParsePackets(Decompress(body, brotli: true));
                    else
                        HandleJson(body);
                    break;

                case OperationAuthReply:
                {
                    var reply = body.Length == 0 ? "" : Encoding.UTF8.GetString(body);
                    if (reply.Contains("\"code\":0", StringComparison.Ordinal))
                    {
                        SetStatus("已进入直播间");
                        // 进入直播间后立刻发一次心跳，并以此作为存活计时起点。
                        _livenessTimer = 0;
                        SendHeartbeat();
                    }
                    else
                    {
                        SetStatus($"进入直播间失败：{reply}");
                    }
                    break;
                }

                case OperationHeartbeatReply:
                    _livenessTimer = 0;
                    break;
            }
        }
    }

    private static byte[] Decompress(byte[] data, bool brotli)
    {
        try
        {
            using var input = new MemoryStream(data);
            using Stream stream = brotli
                ? new BrotliStream(input, CompressionMode.Decompress)
                : new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            stream.CopyTo(output);
            return output.ToArray();
        }
        catch
        {
            // Some servers emit raw deflate without the zlib wrapper.
            try
            {
                using var input = new MemoryStream(data);
                using Stream stream = new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                stream.CopyTo(output);
                return output.ToArray();
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }
    }

    private void HandleJson(byte[] body)
    {
        if (body.Length == 0)
            return;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("cmd", out var cmdProp))
                return;

            var cmd = cmdProp.GetString() ?? "";
            var kind = cmd.Split(':')[0];
            switch (kind)
            {
                case "DANMU_MSG":
                    ParseDanmaku(root);
                    break;
                case "SUPER_CHAT_MESSAGE":
                    ParseSuperChat(root);
                    break;
                // Bilibili emits a Japanese duplicate for the same Super Chat.
                case "SUPER_CHAT_MESSAGE_JPN":
                    break;
                case "SEND_GIFT":
                    ParseGift(root);
                    break;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed frames.
        }
    }

    private void ParseDanmaku(JsonElement root)
    {
        if (!root.TryGetProperty("info", out var info)
            || info.ValueKind != JsonValueKind.Array
            || info.GetArrayLength() < 3)
            return;

        var text = info[1].ValueKind == JsonValueKind.String ? info[1].GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(text))
            return;

        long uid = 0;
        var name = "";
        var user = info[2];
        if (user.ValueKind == JsonValueKind.Array && user.GetArrayLength() >= 2)
        {
            if (user[0].ValueKind == JsonValueKind.Number && user[0].TryGetInt64(out var parsedUid))
                uid = parsedUid;
            name = user[1].ValueKind == JsonValueKind.String ? user[1].GetString() ?? "" : "";
        }

        var color = 0;
        var face = "";
        var meta = info[0];
        if (meta.ValueKind == JsonValueKind.Array)
        {
            if (meta.GetArrayLength() > 3 && meta[3].ValueKind == JsonValueKind.Number
                && meta[3].TryGetInt32(out var parsedColor))
                color = parsedColor;

            face = TryGetFace(meta);
        }

        Emit(new DanmakuMessage
        {
            Uid = uid,
            Name = name,
            Text = text,
            FaceUrl = face,
            Color = color,
            Kind = DanmakuKind.Chat,
            IsSelf = _ownUid > 0 && uid == _ownUid
        });
    }

    private static string TryGetFace(JsonElement meta)
    {
        try
        {
            if (meta.GetArrayLength() <= 15)
                return "";

            var userInfo = meta[15];

            // Depending on the protocol revision this slot is either an object or a JSON string.
            if (userInfo.ValueKind == JsonValueKind.String)
            {
                var raw = userInfo.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                    return "";
                using var doc = JsonDocument.Parse(raw);
                return ExtractFaceFromUserInfo(doc.RootElement);
            }

            return ExtractFaceFromUserInfo(userInfo);
        }
        catch
        {
            return "";
        }
    }

    private static string ExtractFaceFromUserInfo(JsonElement userInfo)
    {
        if (userInfo.ValueKind != JsonValueKind.Object
            || !userInfo.TryGetProperty("user", out var user)
            || user.ValueKind != JsonValueKind.Object
            || !user.TryGetProperty("base", out var baseInfo)
            || baseInfo.ValueKind != JsonValueKind.Object
            || !baseInfo.TryGetProperty("face", out var face)
            || face.ValueKind != JsonValueKind.String)
            return "";

        return face.GetString() ?? "";
    }

    private void ParseSuperChat(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return;

        var message = data.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(message))
            return;

        var price = data.TryGetProperty("price", out var priceProp) && priceProp.TryGetInt32(out var parsedPrice)
            ? parsedPrice
            : 0;
        var uid = data.TryGetProperty("uid", out var uidProp) && uidProp.TryGetInt64(out var parsedUid) ? parsedUid : 0;

        var name = "";
        var face = "";
        if (data.TryGetProperty("user_info", out var userInfo) && userInfo.ValueKind == JsonValueKind.Object)
        {
            name = userInfo.TryGetProperty("uname", out var uname) ? uname.GetString() ?? "" : "";
            face = userInfo.TryGetProperty("face", out var faceProp) ? faceProp.GetString() ?? "" : "";
        }

        Emit(new DanmakuMessage
        {
            Uid = uid,
            Name = name,
            Text = $"[SC ¥{price}] {message}",
            FaceUrl = face,
            Color = 0xFFD700,
            Kind = DanmakuKind.SuperChat,
            IsSelf = _ownUid > 0 && uid == _ownUid
        });
    }

    private void ParseGift(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return;

        var name = data.TryGetProperty("uname", out var uname) ? uname.GetString() ?? "" : "";
        var gift = data.TryGetProperty("giftName", out var giftName) ? giftName.GetString() ?? "" : "";
        var count = data.TryGetProperty("num", out var num) && num.TryGetInt32(out var parsedCount) ? parsedCount : 1;
        if (string.IsNullOrWhiteSpace(gift))
            return;

        var uid = data.TryGetProperty("uid", out var uidProp) && uidProp.TryGetInt64(out var parsedUid) ? parsedUid : 0;
        var face = data.TryGetProperty("face", out var faceProp) ? faceProp.GetString() ?? "" : "";

        Emit(new DanmakuMessage
        {
            Uid = uid,
            Name = name,
            Text = $"送出 {gift} ×{count}",
            FaceUrl = face,
            Color = 0xFF7FAF,
            Kind = DanmakuKind.Gift,
            IsSelf = false
        });
    }

    private void Emit(DanmakuMessage message)
    {
        try { MessageReceived?.Invoke(message); }
        catch (Exception ex) { MainFile.Logger.Warn($"[Bilibili] 弹幕处理失败: {ex.Message}"); }
    }

    // ---- big-endian helpers ----

    private static int ReadInt32BE(byte[] data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

    private static int ReadInt16BE(byte[] data, int offset) =>
        (short)((data[offset] << 8) | data[offset + 1]);

    private static void WriteInt32BE(byte[] data, int offset, int value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private static void WriteInt16BE(byte[] data, int offset, int value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }
}
