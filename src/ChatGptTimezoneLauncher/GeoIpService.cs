using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ChatGptTimezoneLauncher;

public sealed class GeoIpService
{
    public static readonly TimeSpan DetectionTimeout = TimeSpan.FromSeconds(7);
    private static readonly Uri[] TraceEndpoints =
    [
        new("https://chatgpt.com/cdn-cgi/trace"),
        new("https://api.openai.com/cdn-cgi/trace"),
        new("https://auth.openai.com/cdn-cgi/trace"),
        new("https://chat.openai.com/cdn-cgi/trace")
    ];
    private readonly HttpClient? _testClient;
    private readonly Func<HttpClient> _clientFactory;

    public GeoIpService(HttpClient? client = null, Func<HttpClient>? clientFactory = null)
    {
        _testClient = client;
        _clientFactory = clientFactory ?? CreateNetworkClient;
        if (client is not null) client.Timeout = Timeout.InfiniteTimeSpan;
    }

    public static HttpClient CreateNetworkClient()
    {
        // .NET 8's DefaultProxy is a process-wide snapshot. A fresh Windows handler reads
        // the current WinINet proxy/PAC configuration and owns a new connection pool.
        var handler = new WinHttpHandler
        {
            WindowsProxyUsePolicy = WindowsProxyUsePolicy.UseWinInetProxy,
            AutomaticRedirection = false,
            AutomaticDecompression = DecompressionMethods.All,
            SendTimeout = TimeSpan.FromSeconds(3),
            ReceiveHeadersTimeout = TimeSpan.FromSeconds(3),
            ReceiveDataTimeout = TimeSpan.FromSeconds(3)
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<DetectionResult> DetectAsync(CancellationToken cancellationToken = default)
    {
        using var ownedClient = _testClient is null ? _clientFactory() : null;
        var client = _testClient ?? ownedClient!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(DetectionTimeout);
        var errors = new ConcurrentQueue<string>();
        try
        {
            TraceResult? trace;
            using (var primary = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
            {
                primary.CancelAfter(TimeSpan.FromMilliseconds(2500));
                trace = await TryTraceAsync(client, TraceEndpoints[0], errors, primary.Token);
            }
            if (trace is null && !deadline.IsCancellationRequested)
            {
                using var fallback = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                fallback.CancelAfter(TimeSpan.FromSeconds(2));
                trace = await FirstValidAsync(TraceEndpoints.Skip(1).Select(endpoint =>
                    TryTraceAsync(client, endpoint, errors, fallback.Token)).ToList(), fallback.Token);
                fallback.Cancel();
            }
            if (trace is null)
                return DetectionResult.Fail("无法探测 ChatGPT/OpenAI 当前出口，未使用历史结果。\r\n" + string.Join("\r\n", errors));

            // Deny unsupported trace countries before any third-party timezone lookup.
            if (!string.IsNullOrWhiteSpace(trace.CountryCode) && !LaunchProtection.IsSupportedCountry(trace.CountryCode))
                return DetectionResult.Ok(new GeoLocation(trace.Ip, trace.CountryCode, CountryName(trace.CountryCode),
                    null, "", DateTimeOffset.Now, "出口地区（无需时区查询）", $"{trace.Host}/cdn-cgi/trace", TraceCountryCode: trace.CountryCode));

            using var geoDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            geoDeadline.CancelAfter(TimeSpan.FromMilliseconds(2500));
            var providers = new[] { "ipinfo.io", "ipapi.co", "ipwho.is" };
            var location = await FirstValidAsync(providers.Select(provider =>
                TryGeoAsync(client, trace, provider, errors, geoDeadline.Token)).ToList(), geoDeadline.Token);
            geoDeadline.Cancel();
            return location is not null ? DetectionResult.Ok(location) : DetectionResult.Fail(
                $"已探测到 ChatGPT 出口 {trace.Ip}，但无法查询该 IP 的有效地区与时区；未使用其他出口或旧结果。\r\n" + string.Join("\r\n", errors));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DetectionResult.Fail("出口检测超时（最多约 7 秒），未使用历史结果。请确认网络后重试。");
        }
    }

    private static async Task<T?> FirstValidAsync<T>(List<Task<T?>> pending, CancellationToken cancellationToken) where T : class
    {
        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending).WaitAsync(cancellationToken);
            pending.Remove(finished);
            if (await finished is { } result) return result;
        }
        return null;
    }

    private static async Task<TraceResult?> TryTraceAsync(HttpClient client, Uri endpoint,
        ConcurrentQueue<string> errors, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendFreshAsync(client, endpoint, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (!string.Equals(response.RequestMessage?.RequestUri?.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("重定向到其他域名，已忽略");
            var fields = ParseTrace(await ReadSmallBodyAsync(response, cancellationToken));
            if (fields.TryGetValue("ip", out var text) && IPAddress.TryParse(text, out var ip))
                return new(ip.ToString(), endpoint.Host, fields.GetValueOrDefault("loc")?.ToUpperInvariant());
            throw new InvalidDataException("trace 未返回有效 IP");
        }
        catch (Exception ex) when (IsNetworkFailure(ex))
        {
            errors.Enqueue($"{endpoint.Host}: {Friendly(ex)}");
            return null;
        }
    }

    private static async Task<GeoLocation?> TryGeoAsync(HttpClient client, TraceResult trace, string provider,
        ConcurrentQueue<string> errors, CancellationToken cancellationToken)
    {
        var url = provider switch
        {
            "ipinfo.io" => $"https://ipinfo.io/{trace.Ip}/json",
            "ipapi.co" => $"https://ipapi.co/{trace.Ip}/json/",
            _ => $"https://ipwho.is/{trace.Ip}"
        };
        try
        {
            using var response = await SendFreshAsync(client, new Uri(url), cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await ReadSmallBodyAsync(response, cancellationToken));
            var root = json.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True ||
                root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                throw new InvalidDataException("指定 IP 查询失败");
            var ip = String(root, "ip");
            var code = String(root, provider == "ipinfo.io" ? "country" : "country_code")?.ToUpperInvariant();
            var zone = provider == "ipwho.is" && root.TryGetProperty("timezone", out var timezone)
                ? String(timezone, "id") : String(root, "timezone");
            if (!IPAddress.TryParse(ip, out var returned) || !returned.Equals(IPAddress.Parse(trace.Ip)) ||
                !TimeZoneCatalog.IsValid(zone) || code is not { Length: 2 } || !code.All(char.IsAsciiLetter))
                throw new InvalidDataException("地区、时区不完整或 IP 不匹配");
            return new(ip!, code, String(root, "country_name") ?? CountryName(code), String(root, "city"),
                zone!, DateTimeOffset.Now, $"{provider}（指定 IP）", Method(trace), TraceCountryCode: trace.CountryCode);
        }
        catch (Exception ex) when (IsNetworkFailure(ex))
        {
            errors.Enqueue($"{provider}: {Friendly(ex)}");
            return null;
        }
    }

    private static async Task<HttpResponseMessage> SendFreshAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("ChatGPTTimezoneLauncher/1.3");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        request.Headers.TryAddWithoutValidation("Pragma", "no-cache");
        request.Headers.ConnectionClose = true;
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).WaitAsync(cancellationToken);
    }

    private static async Task<string> ReadSmallBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > 64 * 1024) throw new InvalidDataException("响应过大");
        await response.Content.LoadIntoBufferAsync(64 * 1024).WaitAsync(cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string? String(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static string CountryName(string code)
    {
        if (code == "CN") return "中国大陆";
        if (code == "HK") return "香港";
        if (code == "MO") return "澳门";
        try { return new RegionInfo(code).DisplayName; } catch (ArgumentException) { return code; }
    }
    private static string Method(TraceResult trace) => $"{trace.Host}/cdn-cgi/trace → 指定 IP GeoIP";
    private static bool IsNetworkFailure(Exception ex) => ex is HttpRequestException or OperationCanceledException or
        NotSupportedException or JsonException or InvalidDataException or ObjectDisposedException;
    private static string Friendly(Exception ex) => ex is OperationCanceledException ? "请求超时或已取消" : ex.Message;
    private sealed record TraceResult(string Ip, string Host, string? CountryCode);

    public static IReadOnlyDictionary<string, string> ParseTrace(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = raw.IndexOf('=');
            if (separator <= 0) continue;
            var key = raw[..separator].Trim(); var value = raw[(separator + 1)..].Trim();
            if (key.Length > 0 && value.Length > 0) result[key] = value;
        }
        return result;
    }
}
