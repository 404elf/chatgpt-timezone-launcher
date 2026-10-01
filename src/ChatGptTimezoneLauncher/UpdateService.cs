using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace ChatGptTimezoneLauncher;

public sealed record LauncherUpdate(Version Version, string FileName, Uri DownloadUrl, string Sha256);

public sealed class UpdateService
{
    public const string ReleasesUrl = "https://github.com/404elf/chatgpt-timezone-launcher/releases/latest";
    private const string DownloadPrefix = "https://github.com/404elf/chatgpt-timezone-launcher/releases/download/";
    private readonly HttpClient? _testClient;
    private readonly Func<HttpClient> _clientFactory;

    public UpdateService(HttpClient? client = null, Func<HttpClient>? clientFactory = null)
    {
        _testClient = client;
        _clientFactory = clientFactory ?? (() => new HttpClient(new WinHttpHandler
        {
            WindowsProxyUsePolicy = WindowsProxyUsePolicy.UseWinInetProxy,
            AutomaticRedirection = true,
            SendTimeout = TimeSpan.FromSeconds(10), ReceiveHeadersTimeout = TimeSpan.FromSeconds(10),
            ReceiveDataTimeout = TimeSpan.FromSeconds(30)
        }));
        if (client is not null) Configure(client);
    }

    private static void Configure(HttpClient client)
    {
        client.Timeout = TimeSpan.FromMinutes(3);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ChatGPTTimezoneLauncher/1.3");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
    }
    private HttpClient CreateClient() { var client = _clientFactory(); Configure(client); return client; }

    public async Task<LauncherUpdate?> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        using var ownedClient = _testClient is null ? CreateClient() : null;
        var client = _testClient ?? ownedClient!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var release = await client.GetFromJsonAsync<Release>(
            "https://api.github.com/repos/404elf/chatgpt-timezone-launcher/releases/latest", timeout.Token);
        if (release is null || release.Draft || release.Prerelease ||
            !Version.TryParse(release.Tag?.TrimStart('v'), out var version))
            throw new InvalidDataException("更新信息无效，请稍后重试。");
        if (version <= currentVersion) return null;
        var fileName = $"ChatGPT-TimeZone-Launcher-v{version}-win-x64.exe";
        var asset = release.Assets?.SingleOrDefault(x => x.Name == fileName);
        if (asset is null || !IsReleaseDownload(asset.Url))
            throw new InvalidDataException("新版尚未提供 Windows 下载文件。");
        var hash = asset.Digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true
            ? asset.Digest[7..] : null;
        if (!IsSha256(hash))
        {
            var checksums = release.Assets?.SingleOrDefault(x => x.Name == $"SHA256SUMS-v{version}.txt");
            if (checksums is null || !IsReleaseDownload(checksums.Url))
                throw new InvalidDataException("新版缺少校验信息，暂时无法安全下载。");
            var text = await client.GetStringAsync(checksums.Url!, timeout.Token);
            if (text.Length > 64 * 1024) throw new InvalidDataException("校验文件无效。");
            hash = text.Split('\n').Select(line => line.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length == 2 && parts[1].TrimStart('*') == fileName)
                .Select(parts => parts[0]).SingleOrDefault();
        }
        if (!IsSha256(hash)) throw new InvalidDataException("新版校验信息无效。");
        return new(version, fileName, new Uri(asset.Url!), hash!);
    }

    public async Task<string> DownloadAsync(LauncherUpdate update, string updatesDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!IsReleaseDownload(update.DownloadUrl.AbsoluteUri) || !IsSha256(update.Sha256) ||
            update.FileName != $"ChatGPT-TimeZone-Launcher-v{update.Version}-win-x64.exe")
            throw new InvalidDataException("下载信息无效。");
        // Each download owns a new directory; existing programs and user files are never overwritten.
        var directory = Path.Combine(Path.GetFullPath(updatesDirectory), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, update.FileName);
        var partial = path + ".part";
        using var ownedClient = _testClient is null ? CreateClient() : null;
        var client = _testClient ?? ownedClient!;
        using var response = await client.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            await input.CopyToAsync(output, cancellationToken);
        await using (var file = File.OpenRead(partial))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
            if (!hash.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("下载文件校验失败，请重新下载。未替换当前启动器。");
        }
        File.Move(partial, path);
        return path;
    }

    private static bool IsReleaseDownload(string? url) => url is not null &&
        url.StartsWith(DownloadPrefix, StringComparison.Ordinal) && Uri.TryCreate(url, UriKind.Absolute, out _);
    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private sealed class Release
    {
        [JsonPropertyName("tag_name")] public string? Tag { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public Asset[]? Assets { get; set; }
    }

    private sealed class Asset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? Url { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }
}
