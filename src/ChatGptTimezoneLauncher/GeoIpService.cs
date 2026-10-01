using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ChatGptTimezoneLauncher;

public sealed class GeoIpService
{
    private static readonly Uri[] TraceEndpoints =
    [
        new("https://chatgpt.com/cdn-cgi/trace"),
        new("https://api.openai.com/cdn-cgi/trace"),
        new("https://auth.openai.com/cdn-cgi/trace"),
        new("https://chat.openai.com/cdn-cgi/trace")
    ];

    private readonly HttpClient _client;

    public GeoIpService(HttpClient? client = null)
    {
        _client = client ?? new HttpClient(new HttpClientHandler
        {
            UseProxy = true,
            Proxy = null,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        });
        _client.Timeout = TimeSpan.FromSeconds(8);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("ChatGPTTimezoneLauncher/1.2");
    }

    public async Task<DetectionResult> DetectAsync(CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var trace = await DetectChatGptExitAsync(errors, cancellationToken);
        if (trace is null)
            return DetectionResult.Fail("无法探测 ChatGPT/OpenAI 流量的实际出口；未使用普通网络出口。" +
                Environment.NewLine + string.Join(Environment.NewLine, errors));

        var geo = await LocateExplicitIpAsync(trace.Value, errors, cancellationToken);
        if (geo is not null) return DetectionResult.Ok(geo);

        return DetectionResult.Fail($"已探测到 ChatGPT 出口 {trace.Value.Ip}，但无法查询该指定 IP 的时区；未使用 GeoIP 请求自身的出口。" +
            Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    private async Task<TraceResult?> DetectChatGptExitAsync(List<string> errors, CancellationToken cancellationToken)
    {
        foreach (var endpoint in TraceEndpoints)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
                request.Headers.TryAddWithoutValidation("Pragma", "no-cache");
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    errors.Add($"{endpoint.Host}: HTTP {(int)response.StatusCode}");
                    continue;
                }
                var finalHost = response.RequestMessage?.RequestUri?.Host;
                if (!string.Equals(finalHost, endpoint.Host, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{endpoint.Host}: 被重定向到其他域名，已忽略");
                    continue;
                }
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (body.Length > 64 * 1024)
                {
                    errors.Add($"{endpoint.Host}: trace 响应异常过大");
                    continue;
                }
                var fields = ParseTrace(body);
                if (fields.TryGetValue("ip", out var ipText) && IPAddress.TryParse(ipText, out var ip))
                    return new TraceResult(ip.ToString(), endpoint.Host, fields.GetValueOrDefault("loc"));
                errors.Add($"{endpoint.Host}: trace 未返回有效 IP");
            }
            catch (Exception ex) when (IsNetworkFailure(ex))
            {
                errors.Add($"{endpoint.Host}: {Friendly(ex)}");
            }
        }
        return null;
    }

    private async Task<GeoLocation?> LocateExplicitIpAsync(TraceResult trace, List<string> errors,
        CancellationToken cancellationToken)
    {
        try
        {
            var data = await _client.GetFromJsonAsync<IpInfoResponse>(
                $"https://ipinfo.io/{trace.Ip}/json", cancellationToken);
            if (data is not null && IsExplicitResult(trace.Ip, data.Ip, data.TimeZone))
                return BuildLocation(trace, data.Ip!, data.Country, data.Country, data.City,
                    data.TimeZone!, "IPinfo（指定 IP）");
            errors.Add("IPinfo 指定 IP 查询返回数据不完整或 IP 不匹配");
        }
        catch (Exception ex) when (IsNetworkFailure(ex))
        {
            errors.Add($"IPinfo 指定 IP 查询: {Friendly(ex)}");
        }

        try
        {
            var data = await _client.GetFromJsonAsync<IpApiResponse>(
                $"https://ipapi.co/{trace.Ip}/json/", cancellationToken);
            if (data is not null && !data.Error && IsExplicitResult(trace.Ip, data.Ip, data.TimeZone))
                return BuildLocation(trace, data.Ip!, data.CountryCode, data.CountryName, data.City,
                    data.TimeZone!, "ipapi.co（指定 IP）");
            errors.Add("ipapi.co 指定 IP 查询返回数据不完整或 IP 不匹配");
        }
        catch (Exception ex) when (IsNetworkFailure(ex))
        {
            errors.Add($"ipapi.co 指定 IP 查询: {Friendly(ex)}");
        }

        try
        {
            var data = await _client.GetFromJsonAsync<IpWhoResponse>(
                $"https://ipwho.is/{trace.Ip}", cancellationToken);
            var zone = data?.Timezone?.Id;
            if (data is { Success: true } && IsExplicitResult(trace.Ip, data.Ip, zone))
                return BuildLocation(trace, data.Ip!, data.CountryCode, data.Country, data.City,
                    zone!, "ipwho.is（指定 IP）");
            errors.Add($"ipwho.is 指定 IP 查询失败{(string.IsNullOrWhiteSpace(data?.Message) ? "" : $": {data.Message}")}");
        }
        catch (Exception ex) when (IsNetworkFailure(ex))
        {
            errors.Add($"ipwho.is 指定 IP 查询: {Friendly(ex)}");
        }
        return null;
    }

    private static GeoLocation BuildLocation(TraceResult trace, string ip, string? countryCode,
        string? countryName, string? city, string zone, string provider) =>
        new(ip, countryCode ?? "--", countryName ?? "未知", city, zone, DateTimeOffset.Now, provider,
            $"{trace.Host}/cdn-cgi/trace → 指定 IP GeoIP", null, null, trace.CountryCode);

    public static IReadOnlyDictionary<string, string> ParseTrace(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = rawLine.IndexOf('=');
            if (separator <= 0) continue;
            var key = rawLine[..separator].Trim();
            var value = rawLine[(separator + 1)..].Trim();
            if (key.Length > 0 && value.Length > 0) result[key] = value;
        }
        return result;
    }

    private static bool IsExplicitResult(string requestedIp, string? returnedIp, string? zone) =>
        IPAddress.TryParse(requestedIp, out var requested) && IPAddress.TryParse(returnedIp, out var returned) &&
        requested.Equals(returned) && TimeZoneCatalog.IsValid(zone);

    private static bool IsNetworkFailure(Exception ex) => ex is HttpRequestException or TaskCanceledException or
        NotSupportedException or System.Text.Json.JsonException or InvalidDataException;
    private static string Friendly(Exception ex) => ex is TaskCanceledException ? "请求超时" : ex.Message;
    private readonly record struct TraceResult(string Ip, string Host, string? CountryCode);

    private sealed class IpApiResponse
    {
        [JsonPropertyName("ip")] public string? Ip { get; set; }
        [JsonPropertyName("city")] public string? City { get; set; }
        [JsonPropertyName("country_code")] public string? CountryCode { get; set; }
        [JsonPropertyName("country_name")] public string? CountryName { get; set; }
        [JsonPropertyName("timezone")] public string? TimeZone { get; set; }
        [JsonPropertyName("error")] public bool Error { get; set; }
    }

    private sealed class IpInfoResponse
    {
        [JsonPropertyName("ip")] public string? Ip { get; set; }
        [JsonPropertyName("city")] public string? City { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("timezone")] public string? TimeZone { get; set; }
    }

    private sealed class IpWhoResponse
    {
        [JsonPropertyName("success")] public bool Success { get; set; } = true;
        [JsonPropertyName("ip")] public string? Ip { get; set; }
        [JsonPropertyName("city")] public string? City { get; set; }
        [JsonPropertyName("country_code")] public string? CountryCode { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("timezone")] public IpWhoTimeZone? Timezone { get; set; }
    }

    private sealed class IpWhoTimeZone
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
    }
}
