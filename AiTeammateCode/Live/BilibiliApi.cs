using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiTeammate.AiTeammateCode.Settings;

namespace AiTeammate.AiTeammateCode.Live;

/// <summary>
/// Minimal Bilibili Live HTTP client: anonymous device cookies (buvid), WBI request signing,
/// room resolution, danmaku-server lookup, login verification and danmaku sending.
/// 不登录即可读取弹幕；登录后（SESSDATA + bili_jct）才能发送弹幕。
/// </summary>
public static class BilibiliApi
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly Lock Gate = new();

    private static string _buvid3 = "";
    private static string _buvid4 = "";
    private static string _mixinKey = "";
    private static DateTime _wbiFetchedAt = DateTime.MinValue;
    private static long _loginUid;

    // Bilibili's public WBI mixin-key permutation table.
    private static readonly int[] MixinKeyEncTab =
    [
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
        33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40,
        61, 26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11,
        36, 20, 34, 44, 52
    ];

    public sealed class DanmuServerInfo
    {
        public required long RealRoomId { get; init; }
        public required string Token { get; init; }
        public required List<(string Host, int Port)> Hosts { get; init; }
    }

    public sealed class LoginInfo
    {
        public bool IsLogin { get; init; }
        public long Uid { get; init; }
        public string UserName { get; init; } = "";
        public string Face { get; init; } = "";
        public string Error { get; init; } = "";
    }

    public static string Buvid3
    {
        get { lock (Gate) return _buvid3; }
    }

    /// <summary>True when the user has pasted the two cookies needed to send danmaku.</summary>
    public static bool HasLoginCookies
    {
        get
        {
            var settings = AiSettingsStore.Current;
            return !string.IsNullOrWhiteSpace(settings.BilibiliSessData)
                   && !string.IsNullOrWhiteSpace(settings.BilibiliBiliJct);
        }
    }

    /// <summary>Extracts a room id from a bare number or a live-room URL. 从房间号或链接中解析房间号。</summary>
    public static string NormalizeRoomInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "";

        var digits = new StringBuilder();
        var best = new StringBuilder();
        foreach (var ch in input.Trim())
        {
            if (char.IsDigit(ch))
            {
                digits.Append(ch);
            }
            else if (digits.Length > 0)
            {
                if (digits.Length > best.Length)
                    best.Clear().Append(digits);
                digits.Clear();
            }
        }

        if (digits.Length > best.Length)
            best.Clear().Append(digits);

        return best.ToString();
    }

    public static async Task<(long RealRoomId, string Error)> ResolveRoomAsync(string roomInput, CancellationToken ct)
    {
        var id = NormalizeRoomInput(roomInput);
        if (id.Length == 0)
            return (0, "请填写直播间房间号或链接");

        await EnsureBuvidAsync(ct);

        var url = $"https://api.live.bilibili.com/room/v1/Room/room_init?id={Uri.EscapeDataString(id)}";
        var (code, body, error) = await GetJsonAsync(url, $"https://live.bilibili.com/{id}", ct);
        if (code == 0 && body.HasValue
            && body.Value.TryGetProperty("data", out var data)
            && data.TryGetProperty("room_id", out var roomId))
        {
            return (roomId.GetInt64(), "");
        }

        // Some rooms answer room_init with a risk-control error; treating the input as the real
        // room id still works for the vast majority of rooms.
        if (long.TryParse(id, out var fallback) && fallback > 0)
        {
            MainFile.Logger.Warn($"[Bilibili] room_init 失败({code})，直接使用房间号 {fallback}: {error}");
            return (fallback, "");
        }

        return (0, string.IsNullOrWhiteSpace(error) ? "无法解析直播间" : error);
    }

    /// <summary>Fetches the websocket token and host list for a real room id (WBI-signed).</summary>
    public static async Task<(DanmuServerInfo? Info, string Error)> GetDanmuInfoAsync(long realRoomId, CancellationToken ct)
    {
        await EnsureBuvidAsync(ct);
        await EnsureWbiKeysAsync(ct);

        var signed = await RequestDanmuInfoAsync(realRoomId, signed: true, ct);
        if (signed.Info != null)
            return signed;

        // The mixin key rotates: drop the cached keys and retry once with a fresh signature.
        InvalidateWbiKeys();
        await EnsureWbiKeysAsync(ct);
        var refreshed = await RequestDanmuInfoAsync(realRoomId, signed: true, ct);
        if (refreshed.Info != null)
            return refreshed;

        // Legacy endpoint: neither WBI signing nor cookies required, and it still returns a valid
        // token + host list. This is the reliable fallback when risk control rejects getDanmuInfo.
        // 老接口 getConf 不校验 WBI，被风控时用它兜底。
        var conf = await RequestDanmuConfAsync(realRoomId, ct);
        if (conf.Info != null)
            return conf;

        // Signature/key drift or a version change: retry unsigned as a last resort.
        var unsigned = await RequestDanmuInfoAsync(realRoomId, signed: false, ct);
        return unsigned.Info != null ? unsigned : refreshed.Error.Length > 0 ? refreshed : conf;
    }

    private static void InvalidateWbiKeys()
    {
        lock (Gate)
        {
            _mixinKey = "";
            _wbiFetchedAt = DateTime.MinValue;
        }
    }

    private static async Task<(DanmuServerInfo? Info, string Error)> RequestDanmuInfoAsync(
        long realRoomId, bool signed, CancellationToken ct)
    {
        var query = signed
            ? BuildSignedQuery(new Dictionary<string, string>
            {
                ["id"] = realRoomId.ToString(),
                ["type"] = "0",
                ["web_location"] = "444.8"
            })
            : $"id={realRoomId}&type=0&web_location=444.8";

        var url = "https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?" + query;
        var (code, body, error) = await GetJsonAsync(url, $"https://live.bilibili.com/{realRoomId}", ct);
        if (code != 0 || body == null)
            return (null, DescribeCode(code, error));

        try
        {
            var root = body.Value;
            if (!root.TryGetProperty("data", out var data))
                return (null, "接口未返回弹幕服务器信息");

            var token = data.TryGetProperty("token", out var tokenProp) ? tokenProp.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(token))
                return (null, "接口未返回弹幕 token");

            var hosts = new List<(string, int)>();
            if (data.TryGetProperty("host_list", out var hostList) && hostList.ValueKind == JsonValueKind.Array)
            {
                foreach (var host in hostList.EnumerateArray())
                {
                    var name = host.TryGetProperty("host", out var h) ? h.GetString() ?? "" : "";
                    var port = host.TryGetProperty("wss_port", out var p) && p.TryGetInt32(out var parsedPort)
                        ? parsedPort
                        : 443;
                    if (!string.IsNullOrWhiteSpace(name))
                        hosts.Add((name, port));
                }
            }

            if (hosts.Count == 0)
                hosts.Add(("broadcastlv.chat.bilibili.com", 443));

            return (new DanmuServerInfo { RealRoomId = realRoomId, Token = token, Hosts = hosts }, "");
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Legacy token source: <c>room/v1/Danmu/getConf</c> needs no WBI signature and no cookies.
    /// 老接口，无签名无 cookie 要求，作为 getDanmuInfo 被风控时的兜底。
    /// </summary>
    private static async Task<(DanmuServerInfo? Info, string Error)> RequestDanmuConfAsync(
        long realRoomId, CancellationToken ct)
    {
        var url = $"https://api.live.bilibili.com/room/v1/Danmu/getConf?room_id={realRoomId}&platform=pc&player=web";
        var (code, body, error) = await GetJsonAsync(url, $"https://live.bilibili.com/{realRoomId}", ct);
        if (code != 0 || body == null)
            return (null, DescribeCode(code, error));

        try
        {
            var root = body.Value;
            if (!root.TryGetProperty("data", out var data))
                return (null, "接口未返回弹幕服务器信息");

            var token = data.TryGetProperty("token", out var tokenProp) ? tokenProp.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(token))
                return (null, "接口未返回弹幕 token");

            var hosts = new List<(string, int)>();
            if (data.TryGetProperty("host_server_list", out var hostList) && hostList.ValueKind == JsonValueKind.Array)
            {
                foreach (var host in hostList.EnumerateArray())
                {
                    var name = host.TryGetProperty("host", out var h) ? h.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(name) || hosts.Any(entry => entry.Item1 == name))
                        continue;

                    var port = 443;
                    if (host.TryGetProperty("wss_port", out var wssPort) && wssPort.TryGetInt32(out var parsedWss))
                        port = parsedWss;
                    else if (host.TryGetProperty("port", out var plainPort) && plainPort.TryGetInt32(out var parsedPlain))
                        port = parsedPlain;

                    hosts.Add((name, port));
                }
            }

            if (hosts.Count == 0)
                hosts.Add(("broadcastlv.chat.bilibili.com", 443));

            return (new DanmuServerInfo { RealRoomId = realRoomId, Token = token, Hosts = hosts }, "");
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }
    public static async Task<LoginInfo> GetLoginInfoAsync(CancellationToken ct)
    {
        await EnsureBuvidAsync(ct);
        var (code, body, error) = await GetJsonAsync(
            "https://api.bilibili.com/x/web-interface/nav",
            "https://www.bilibili.com/", ct);

        if (body == null)
            return new LoginInfo { Error = DescribeCode(code, error) };

        try
        {
            var root = body.Value;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return new LoginInfo { Error = DescribeCode(code, error) };

            var isLogin = data.TryGetProperty("isLogin", out var loginProp) && loginProp.ValueKind == JsonValueKind.True;
            var uid = data.TryGetProperty("mid", out var mid) && mid.TryGetInt64(out var parsedUid) ? parsedUid : 0;
            lock (Gate)
                _loginUid = isLogin ? uid : 0;

            return new LoginInfo
            {
                IsLogin = isLogin,
                Uid = uid,
                UserName = data.TryGetProperty("uname", out var name) ? name.GetString() ?? "" : "",
                Face = data.TryGetProperty("face", out var face) ? face.GetString() ?? "" : "",
                Error = isLogin ? "" : "未登录（SESSDATA 缺失或已过期）"
            };
        }
        catch (Exception ex)
        {
            return new LoginInfo { Error = ex.Message };
        }
    }

    /// <summary>Sends a danmaku line. Requires SESSDATA and bili_jct cookies. 发送弹幕。</summary>
    public static async Task<(bool Success, string Error)> SendDanmakuAsync(
        long realRoomId, string text, CancellationToken ct)
    {
        var settings = AiSettingsStore.Current;
        if (string.IsNullOrWhiteSpace(settings.BilibiliSessData))
            return (false, "未配置 SESSDATA，无法发送弹幕");
        if (string.IsNullOrWhiteSpace(settings.BilibiliBiliJct))
            return (false, "未配置 bili_jct，无法发送弹幕");

        await EnsureBuvidAsync(ct);

        var csrf = settings.BilibiliBiliJct.Trim();
        var form = new Dictionary<string, string>
        {
            ["bubble"] = "0",
            ["msg"] = text,
            ["color"] = "16777215",
            ["mode"] = "1",
            ["fontsize"] = "25",
            ["rnd"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            ["roomid"] = realRoomId.ToString(),
            ["csrf"] = csrf,
            ["csrf_token"] = csrf
        };

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, "https://api.live.bilibili.com/msg/send")
            {
                Content = new FormUrlEncodedContent(form)
            };
            ApplyHeaders(request, $"https://live.bilibili.com/{realRoomId}");

            using var response = await Http.SendAsync(request, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return (false, $"HTTP {(int)response.StatusCode}");

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var codeProp) && codeProp.TryGetInt32(out var parsed)
                ? parsed
                : -1;
            if (code == 0)
                return (true, "");

            var message = root.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
            return (false, DescribeSendCode(code, message));
        }
        catch (OperationCanceledException)
        {
            return (false, "发送超时");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Downloads an avatar; returns <c>null</c> on failure.</summary>
    public static async Task<byte[]?> FetchImageAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        // Bilibili sometimes returns protocol-relative URLs.
        if (url.StartsWith("//", StringComparison.Ordinal))
            url = "https:" + url;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyHeaders(request, "https://live.bilibili.com/");
            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length == 0 ? null : bytes;
        }
        catch
        {
            return null;
        }
    }

    // ---- internals ----

    private static async Task EnsureBuvidAsync(CancellationToken ct)
    {
        lock (Gate)
        {
            if (!string.IsNullOrEmpty(_buvid3))
                return;
        }

        var (code, body, _) = await GetJsonAsync(
            "https://api.bilibili.com/x/frontend/finger/spi",
            "https://www.bilibili.com/", ct, includeCookies: false);
        if (code != 0 || body == null || !body.Value.TryGetProperty("data", out var data))
            return;

        var b3 = data.TryGetProperty("b_3", out var prop3) ? prop3.GetString() ?? "" : "";
        var b4 = data.TryGetProperty("b_4", out var prop4) ? prop4.GetString() ?? "" : "";
        lock (Gate)
        {
            _buvid3 = b3;
            _buvid4 = b4;
        }
    }

    private static async Task EnsureWbiKeysAsync(CancellationToken ct)
    {
        lock (Gate)
        {
            if (!string.IsNullOrEmpty(_mixinKey) && DateTime.UtcNow - _wbiFetchedAt < TimeSpan.FromMinutes(30))
                return;
        }

        await EnsureBuvidAsync(ct);
        var (_, body, _) = await GetJsonAsync(
            "https://api.bilibili.com/x/web-interface/nav",
            "https://www.bilibili.com/", ct);
        if (body == null || !body.Value.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("wbi_img", out var wbi))
            return;

        var imgUrl = wbi.TryGetProperty("img_url", out var imgProp) ? imgProp.GetString() ?? "" : "";
        var subUrl = wbi.TryGetProperty("sub_url", out var subProp) ? subProp.GetString() ?? "" : "";
        var imgKey = ExtractWbiKey(imgUrl);
        var subKey = ExtractWbiKey(subUrl);
        if (imgKey.Length == 0 || subKey.Length == 0)
            return;

        var raw = imgKey + subKey;
        var permuted = new StringBuilder(raw.Length);
        foreach (var index in MixinKeyEncTab)
        {
            if (index < raw.Length)
                permuted.Append(raw[index]);
        }

        // WBI 的 mixin key 只取置换结果的**前 32 位**。用完整 64 位算出来的 w_rid 会被服务端判为
        // 签名错误，getDanmuInfo 直接返回 -352（风控）——这是之前最主要的报错来源。
        var mixinKey = permuted.Length > 32 ? permuted.ToString(0, 32) : permuted.ToString();

        lock (Gate)
        {
            _mixinKey = mixinKey;
            _wbiFetchedAt = DateTime.UtcNow;
        }
    }

    private static string ExtractWbiKey(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "";
        var name = url[(url.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private static string BuildSignedQuery(Dictionary<string, string> parameters)
    {
        string mixinKey;
        lock (Gate)
            mixinKey = _mixinKey;

        parameters["wts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var sorted = parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal);
        var query = string.Join("&", sorted.Select(pair => $"{Encode(pair.Key)}={Encode(pair.Value)}"));

        if (string.IsNullOrEmpty(mixinKey))
            return query;

        var wRid = Md5Hex(query + mixinKey);
        return query + "&w_rid=" + wRid;
    }

    private static string Encode(string value) =>
        Uri.EscapeDataString(new string(value.Where(c => c is not ('!' or '(' or ')' or '*')).ToArray()));

    private static string Md5Hex(string text)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<(int Code, JsonElement? Body, string Error)> GetJsonAsync(
        string url, string referer, CancellationToken ct, bool includeCookies = true)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyHeaders(request, referer, includeCookies);
            using var response = await Http.SendAsync(request, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return (-1, null, $"HTTP {(int)response.StatusCode}");

            using var doc = JsonDocument.Parse(raw);
            // Clone so the element survives the JsonDocument being disposed.
            var root = doc.RootElement.Clone();
            var code = root.TryGetProperty("code", out var codeProp) && codeProp.TryGetInt32(out var parsed)
                ? parsed
                : 0;
            var error = root.TryGetProperty("message", out var message) ? message.GetString() ?? "" : "";
            return (code, root, error);
        }
        catch (OperationCanceledException)
        {
            return (-1, null, "请求超时");
        }
        catch (Exception ex)
        {
            return (-1, null, ex.Message);
        }
    }

    private static void ApplyHeaders(HttpRequestMessage request, string referer, bool includeCookies = true)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", referer);
        request.Headers.TryAddWithoutValidation("Origin", "https://live.bilibili.com");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");

        if (includeCookies)
        {
            var cookie = BuildCookieHeader();
            if (!string.IsNullOrEmpty(cookie))
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }
    }

    private static string BuildCookieHeader()
    {
        var settings = AiSettingsStore.Current;
        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(settings.BilibiliSessData))
            parts.Add("SESSDATA=" + settings.BilibiliSessData.Trim());
        if (!string.IsNullOrWhiteSpace(settings.BilibiliBiliJct))
            parts.Add("bili_jct=" + settings.BilibiliBiliJct.Trim());

        string b3, b4;
        long loginUid;
        lock (Gate)
        {
            b3 = _buvid3;
            b4 = _buvid4;
            loginUid = _loginUid;
        }

        if (!string.IsNullOrEmpty(b3))
            parts.Add("buvid3=" + b3);
        if (!string.IsNullOrEmpty(b4))
            parts.Add("buvid4=" + b4);
        if (loginUid > 0)
            parts.Add("DedeUserID=" + loginUid);

        return string.Join("; ", parts);
    }

    private static string DescribeCode(int code, string message) => code switch
    {
        -101 => "未登录或登录已过期",
        -352 => "触发 B 站风控校验，请稍后重试",
        -412 => "请求被 B 站风控拦截",
        60004 => "直播间不存在",
        19002000 => "直播间不存在或已下播",
        _ => string.IsNullOrWhiteSpace(message) ? $"接口返回错误（code {code}）" : $"{message}（code {code}）"
    };

    private static string DescribeSendCode(int code, string message) => code switch
    {
        0 => "",
        -101 => "未登录或登录已过期",
        -111 => "需要手机验证",
        -400 => "弹幕参数错误",
        -352 => "触发 B 站风控校验，请稍后重试",
        10030 => "发送太快，被 B 站限流",
        1003212 => "弹幕内容被拒绝或过长",
        10031 => "账号等级不足，无法发送弹幕",
        1003216 => "本直播间禁言或需要粉丝勋章",
        1003217 => "弹幕发送失败",
        _ => string.IsNullOrWhiteSpace(message) ? $"发送失败（code {code}）" : $"{message}（code {code}）"
    };
}
