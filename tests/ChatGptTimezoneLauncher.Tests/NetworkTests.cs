using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ChatGptTimezoneLauncher;

internal static class NetworkTests
{
    public static readonly (string Name, Func<Task> Run)[] Tests =
    [
        ("same detector switches real local Windows proxy connections", TestProxySwitch),
        ("unsupported trace region skips all slow GeoIP requests", TestFastBlock),
        ("fast valid GeoIP cancels slow providers", TestParallelGeo),
        ("slow network detection is bounded by the overall budget", TestBudget),
        ("ChatGPT trace remains preferred over other domain routes", TestPrimaryPriority),
        ("caller cancellation cannot return an allowed exit", TestCancellation)
    ];

    private static async Task TestProxySwitch()
    {
        using var mainland = new LocalProxy("CN", "203.0.113.8");
        using var overseas = new LocalProxy("US", "198.51.100.9");
        var selected = mainland.Address; var clients = 0;
        var detector = new GeoIpService(clientFactory: () =>
        {
            clients++;
            return new HttpClient(new WinHttpHandler
            {
                WindowsProxyUsePolicy = WindowsProxyUsePolicy.UseCustomProxy,
                Proxy = new WebProxy(selected), AutomaticRedirection = false,
                // Pin only these ephemeral local test servers; system trust is never changed.
                ServerCertificateValidationCallback = (_, cert, _, _) => cert is not null &&
                    (cert.Thumbprint == mainland.Thumbprint || cert.Thumbprint == overseas.Thumbprint)
            }) { Timeout = Timeout.InfiniteTimeSpan };
        });
        var watch = Stopwatch.StartNew();
        var first = await detector.DetectAsync(); var firstMs = watch.ElapsedMilliseconds;
        selected = overseas.Address; watch.Restart();
        var second = await detector.DetectAsync();
        Require(first.Location?.Ip == "203.0.113.8" && LaunchProtection.GetIpError(first) is not null,
            "mainland proxy was not detected: " + first.Message.Replace("\r\n", " | ") + " server=" + mainland.Errors);
        Require(second.Location?.Ip == "198.51.100.9" && LaunchProtection.GetIpError(second) is null && clients == 2,
            "same process retained its old proxy/client");
        Require(mainland.Requests == 1, "old proxy connection was reused after switching");
        Console.WriteLine($"METRIC local-proxy CN={firstMs}ms US={watch.ElapsedMilliseconds}ms; same detector, two real CONNECT proxies");
    }

    private static async Task TestFastBlock()
    {
        var geo = 0;
        using var client = new HttpClient(new AsyncHandler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "chatgpt.com") return Text("ip=203.0.113.8\nloc=CN\n");
            Interlocked.Increment(ref geo); await Task.Delay(TimeSpan.FromSeconds(20), token);
            return UsGeo();
        }));
        var watch = Stopwatch.StartNew(); var result = await new GeoIpService(client).DetectAsync();
        Require(result.Location?.CountryCode == "CN" && geo == 0 && watch.Elapsed < TimeSpan.FromSeconds(1), "blocked region waited for GeoIP");
        Require(LaunchProtection.GetIpError(result) is not null, "fast detection weakened the launch gate");
        Console.WriteLine($"METRIC unsupported-region={watch.ElapsedMilliseconds}ms; GeoIP requests={geo}");
    }

    private static async Task TestParallelGeo()
    {
        var cancelled = 0;
        using var client = new HttpClient(new AsyncHandler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "chatgpt.com") return Text("ip=203.0.113.8\nloc=US\n");
            if (request.RequestUri.Host == "ipapi.co")
            {
                await Task.Delay(80, token);
                return Json("""{"ip":"203.0.113.8","country_code":"US","timezone":"America/New_York"}""");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(20), token); }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
            return UsGeo();
        }));
        var watch = Stopwatch.StartNew(); var result = await new GeoIpService(client).DetectAsync();
        Require(result.Success && result.Location!.Provider.StartsWith("ipapi.co") && watch.Elapsed < TimeSpan.FromSeconds(1), "slow first provider blocked the fast result");
        await Task.Delay(50);
        Require(cancelled == 2, "losing GeoIP requests were not cancelled");
        Console.WriteLine($"METRIC parallel-geo={watch.ElapsedMilliseconds}ms; slow requests cancelled={cancelled}");
    }

    private static async Task TestBudget()
    {
        using var client = new HttpClient(new AsyncHandler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "api.openai.com")
            { await Task.Delay(1700, token); return Text("ip=203.0.113.8\nloc=US\n"); }
            await Task.Delay(Timeout.Infinite, token); return UsGeo();
        }));
        var watch = Stopwatch.StartNew(); var result = await new GeoIpService(client).DetectAsync();
        Require(!result.Success && watch.Elapsed < TimeSpan.FromSeconds(7.8), "network failure exceeded the budget or guessed success");
        Console.WriteLine($"METRIC slow-network-failure={watch.ElapsedMilliseconds}ms");
    }

    private static async Task TestPrimaryPriority()
    {
        var fallback = 0;
        using var client = new HttpClient(new AsyncHandler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "chatgpt.com")
            { await Task.Delay(120, token); return Text("ip=203.0.113.8\nloc=US\n"); }
            if (request.RequestUri.Host.EndsWith("openai.com")) Interlocked.Increment(ref fallback);
            return UsGeo();
        }));
        var result = await new GeoIpService(client).DetectAsync();
        Require(result.Success && fallback == 0 && result.Location!.DetectionMethod.StartsWith("chatgpt.com"), "fallback replaced a valid primary route");
    }

    private static async Task TestCancellation()
    {
        using var client = new HttpClient(new AsyncHandler(async (_, token) =>
        { await Task.Delay(Timeout.Infinite, token); return UsGeo(); }));
        using var cancel = new CancellationTokenSource(50);
        try
        {
            var result = await new GeoIpService(client).DetectAsync(cancel.Token);
            Require(!result.Success && result.Location is null, "cancelled detection returned an allowed exit");
        }
        catch (OperationCanceledException) { }
    }

    private static HttpResponseMessage Text(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage UsGeo() => Json("""{"ip":"203.0.113.8","country":"US","timezone":"America/New_York"}""");
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { var response = await send(request, token); response.RequestMessage = request; return response; }
    }

    private sealed class LocalProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<TcpClient> _clients = [];
        private readonly RSA _key = RSA.Create(2048);
        private readonly X509Certificate2 _certificate;
        private readonly string _country, _ip;
        private int _requests;
        private readonly ConcurrentQueue<string> _errors = new();
        public string Errors => string.Join(" / ", _errors);
        public Uri Address { get; }
        public string Thumbprint => _certificate.Thumbprint;
        public int Requests => Volatile.Read(ref _requests);

        public LocalProxy(string country, string ip)
        {
            _country = country; _ip = ip;
            var request = new CertificateRequest("CN=chatgpt.com", _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            foreach (var host in new[] { "chatgpt.com", "ipinfo.io", "ipapi.co", "ipwho.is" }) names.AddDnsName(host);
            request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now.AddHours(1));
            // SChannel needs a native key container for this local TLS fixture. This
            // temporary key is owned by the certificate, never added to a trust store.
            _certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx), "", X509KeyStorageFlags.UserKeySet);
            _listener.Start(); Address = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
            _ = AcceptAsync();
        }
        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                { var client = await _listener.AcceptTcpClientAsync(_stop.Token); _clients.Add(client); _ = ServeAsync(client); }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    using var connect = new StreamReader(stream, Encoding.ASCII, false, 512, leaveOpen: true);
                    var line = await connect.ReadLineAsync(_stop.Token);
                    if (line is null || !line.StartsWith("CONNECT ")) return;
                    var host = line.Split(' ')[1].Split(':')[0];
                    while (!string.IsNullOrEmpty(await connect.ReadLineAsync(_stop.Token))) { }
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), _stop.Token);
                    using var tls = new SslStream(stream, false);
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    { ServerCertificate = _certificate, EnabledSslProtocols = SslProtocols.Tls12, ApplicationProtocols = [SslApplicationProtocol.Http11] }, _stop.Token);
                    using var reader = new StreamReader(tls, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var get = await reader.ReadLineAsync(_stop.Token);
                    if (get is null) return;
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))) { }
                    Interlocked.Increment(ref _requests);
                    var body = host == "chatgpt.com" ? $"ip={_ip}\nloc={_country}\n" : host switch
                    {
                        "ipinfo.io" => $$"""{"ip":"{{_ip}}","country":"{{_country}}","timezone":"America/New_York"}""",
                        "ipwho.is" => $$$"""{"success":true,"ip":"{{{_ip}}}","country_code":"{{{_country}}}","timezone":{"id":"America/New_York"}}""",
                        _ => $$"""{"ip":"{{_ip}}","country_code":"{{_country}}","timezone":"America/New_York"}"""
                    };
                    var bytes = Encoding.UTF8.GetBytes(body);
                    await tls.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                    await tls.WriteAsync(bytes, _stop.Token); await tls.FlushAsync(_stop.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException or ObjectDisposedException)
            { if (!_stop.IsCancellationRequested) _errors.Enqueue(ex.Message); }
        }
        public void Dispose()
        {
            _stop.Cancel(); _listener.Stop(); foreach (var client in _clients) client.Dispose();
            _certificate.Dispose(); _key.Dispose(); _stop.Dispose();
        }
    }
}
