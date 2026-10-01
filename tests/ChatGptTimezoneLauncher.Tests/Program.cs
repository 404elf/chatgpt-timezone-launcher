using System.Diagnostics;
using System.Net;
using System.Text;
using ChatGptTimezoneLauncher;

if (args.Length == 2 && args[0] == PackagedProcessLauncher.HelperArgument)
    return await PackagedProcessLauncher.RunHelperAsync(args[1]);

var tests = new (string Name, Func<Task> Run)[]
{
    ("IANA timezone validation", TestTimeZones),
    ("config save/load and backup", TestConfigSaveLoad),
    ("corrupt config safely defaults", TestCorruptConfig),
    ("main window survives corrupt config", TestCorruptConfigWindow),
    ("legacy self-exit cache is discarded", TestLegacyDetectionCache),
    ("Cloudflare trace parser", TestTraceParser),
    ("ChatGPT US route wins while default route is TW", TestSplitRouting),
    ("ChatGPT node switch is re-detected", TestChatGptNodeSwitch),
    ("default proxy switch cannot change ChatGPT result", TestDefaultProxySwitch),
    ("OpenAI trace endpoint fallback", TestTraceFallback),
    ("explicit-IP GeoIP provider fallback", TestGeoFallback),
    ("explicit-IP GeoIP total failure", TestGeoFailure),
    ("ChatGPT trace total failure never uses default exit", TestTraceFailure),
    ("Clash not running does not block trace detection", TestWithoutClash),
    ("non-Clash network works without controller API", TestWithoutClash),
    ("AppX manifest candidate and version selection", TestDiscovery),
    ("AppX not installed diagnostics", TestNotInstalled),
    ("manual TZ is process-local", TestTzLaunch),
    ("restore/default launch has no TZ injection", TestDefaultLaunch),
    ("default activation allows an existing ChatGPT instance", TestDefaultExisting),
    ("TZ override refuses an existing ChatGPT instance", TestTzExisting),
    ("package startup failure cannot report success", TestPackageFailure),
    ("invalid timezone cannot start a package", TestInvalidLaunch),
    ("missing updated executable cannot start a package", TestMissingExecutable),
    ("default startup strips an inherited TZ", TestDefaultInheritedTz),
    ("missing package returns the Windows activation error", TestMissingPackageActivation),
}.Concat(FeatureTests.Tests).ToArray();

var failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS  {test.Name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL  {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"\n{tests.Length - failures}/{tests.Length} tests passed");
return failures == 0 ? 0 : 1;

static Task TestTimeZones()
{
    Assert(TimeZoneCatalog.IsValid("Asia/Shanghai"), "valid zone rejected");
    Assert(TimeZoneCatalog.IsValid("America/Los_Angeles"), "valid US zone rejected");
    Assert(!TimeZoneCatalog.IsValid("Mars/Olympus"), "invalid zone accepted");
    Assert(TimeZoneCatalog.All.Count > 300, "timezone catalog is unexpectedly small");
    return Task.CompletedTask;
}

static Task TestConfigSaveLoad()
{
    using var temp = new TempDirectory(); var store = new ConfigStore(temp.Path);
    store.Save(new LauncherConfig { TimeZoneOverrideEnabled = true, Mode = TimeZoneMode.Manual, ManualTimeZone = "Asia/Tokyo" });
    var first = store.Load(); Assert(first.Warning is null && first.Config.ManualTimeZone == "Asia/Tokyo", "roundtrip failed");
    store.Save(new LauncherConfig { ManualTimeZone = "Europe/London" });
    Assert(File.Exists(store.BackupPath) && new FileInfo(store.BackupPath).Length > 0, "verified backup missing");
    Assert(store.Load().Config.ManualTimeZone == "Europe/London", "replacement save failed");
    return Task.CompletedTask;
}

static Task TestCorruptConfig()
{
    using var temp = new TempDirectory(); Directory.CreateDirectory(temp.Path);
    File.WriteAllText(System.IO.Path.Combine(temp.Path, "settings.json"), "{broken");
    var loaded = new ConfigStore(temp.Path).Load();
    Assert(loaded.Warning is not null && !loaded.Config.TimeZoneOverrideEnabled, "corruption did not safely default");
    Assert(File.ReadAllText(System.IO.Path.Combine(temp.Path, "settings.json")) == "{broken", "corrupt source was destroyed");
    return Task.CompletedTask;
}

static Task TestCorruptConfigWindow()
{
    using var temp = new TempDirectory();
    var path = System.IO.Path.Combine(temp.Path, "settings.json");
    File.WriteAllText(path, "{broken");
    Exception? error = null;
    var thread = new Thread(() =>
    {
        try
        {
            using var form = new MainForm(new ConfigStore(temp.Path));
            Assert(!form.IsDisposed, "window was disposed");
        }
        catch (Exception ex) { error = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    Assert(thread.Join(TimeSpan.FromSeconds(20)), "window construction timed out");
    if (error is not null) throw error;
    Assert(File.ReadAllText(path) == "{broken", "corrupt config was changed");
    return Task.CompletedTask;
}

static Task TestLegacyDetectionCache()
{
    using var temp = new TempDirectory(); Directory.CreateDirectory(temp.Path);
    File.WriteAllText(System.IO.Path.Combine(temp.Path, "settings.json"),
        """{"SchemaVersion":1,"Mode":0,"ManualTimeZone":"Asia/Shanghai","LastSuccessfulAutoDetection":{"Ip":"60.248.221.27","CountryCode":"TW","CountryName":"Taiwan","City":"Taipei","TimeZone":"Asia/Taipei","DetectedAt":"2026-09-01T00:00:00+08:00","Provider":"ipapi.co"}}""");
    var loaded = new ConfigStore(temp.Path).Load();
    Assert(loaded.Config.LastSuccessfulAutoDetection is null && loaded.Config.ManualTimeZone == "Asia/Shanghai",
        "old incorrect self-exit cache was retained");
    return Task.CompletedTask;
}

static Task TestTraceParser()
{
    var fields = GeoIpService.ParseTrace("fl=1\nip=203.0.113.8\nloc=US\ncolo=SJC\n");
    Assert(fields["ip"] == "203.0.113.8" && fields["loc"] == "US", "trace parse failed");
    return Task.CompletedTask;
}

static async Task TestSplitRouting()
{
    // 模拟 ChatGPT 规则走美国，而 ipapi.co 请求自身若被查询会走台湾。
    var handler = new ScenarioHandler((request, _) => request.RequestUri! switch
    {
        { Host: "chatgpt.com" } => Text("ip=203.0.113.8\nloc=US\n"),
        { Host: "ipinfo.io", AbsolutePath: var path } when path.Contains("203.0.113.8") =>
            Json("""{"ip":"203.0.113.8","city":"Los Angeles","country":"US","timezone":"America/Los_Angeles"}"""),
        { Host: "ipinfo.io" } => Json("""{"ip":"60.248.221.27","city":"Taipei","country":"TW","timezone":"Asia/Taipei"}"""),
        _ => throw new HttpRequestException("unexpected request")
    });
    var result = await new GeoIpService(new HttpClient(handler)).DetectAsync();
    Assert(result.Success && result.Location?.Ip == "203.0.113.8" && result.Location.TimeZone == "America/Los_Angeles",
        "default/TW route incorrectly determined ChatGPT timezone");
    Assert(handler.Requests.Any(x => x.Contains("ipinfo.io/203.0.113.8/json")) &&
           handler.Requests.All(x => !x.EndsWith("ipinfo.io/json")), "GeoIP did not query the explicit trace IP");
}

static async Task TestChatGptNodeSwitch()
{
    var traceCount = 0;
    var handler = new ScenarioHandler((request, _) =>
    {
        if (request.RequestUri!.Host == "chatgpt.com")
            return Text(++traceCount == 1 ? "ip=198.51.100.10\n" : "ip=198.51.100.20\n");
        if (request.RequestUri.AbsolutePath.Contains("198.51.100.10"))
            return Json("""{"ip":"198.51.100.10","city":"New York","country":"US","timezone":"America/New_York"}""");
        return Json("""{"ip":"198.51.100.20","city":"Tokyo","country":"JP","timezone":"Asia/Tokyo"}""");
    });
    var service = new GeoIpService(new HttpClient(handler));
    var first = await service.DetectAsync(); var second = await service.DetectAsync();
    Assert(first.Location?.TimeZone == "America/New_York" && second.Location?.TimeZone == "Asia/Tokyo",
        "ChatGPT node switch reused stale exit");
}

static async Task TestDefaultProxySwitch()
{
    var handler = ExplicitUsScenario();
    var service = new GeoIpService(new HttpClient(handler));
    var first = await service.DetectAsync(); var second = await service.DetectAsync();
    Assert(first.Location?.TimeZone == "America/Los_Angeles" && second.Location?.TimeZone == "America/Los_Angeles",
        "default proxy change affected explicit ChatGPT exit lookup");
}

static async Task TestTraceFallback()
{
    var handler = new ScenarioHandler((request, _) => request.RequestUri!.Host switch
    {
        "chatgpt.com" => Status(HttpStatusCode.ServiceUnavailable),
        "api.openai.com" => Text("ip=203.0.113.8\nloc=US\n"),
        "ipinfo.io" => UsGeo(),
        _ => throw new HttpRequestException("unexpected request")
    });
    var result = await new GeoIpService(new HttpClient(handler)).DetectAsync();
    Assert(result.Success && result.Location!.DetectionMethod.StartsWith("api.openai.com"), "OpenAI trace fallback not used");
}

static async Task TestGeoFallback()
{
    var handler = new ScenarioHandler((request, _) => request.RequestUri!.Host switch
    {
        "chatgpt.com" => Text("ip=203.0.113.8\n"),
        "ipinfo.io" => Status(HttpStatusCode.TooManyRequests),
        "ipapi.co" => Status(HttpStatusCode.TooManyRequests),
        "ipwho.is" => Json("""{"success":true,"ip":"203.0.113.8","city":"Los Angeles","country_code":"US","country":"United States","timezone":{"id":"America/Los_Angeles"}}"""),
        _ => throw new HttpRequestException("unexpected request")
    });
    var result = await new GeoIpService(new HttpClient(handler)).DetectAsync();
    Assert(result.Success && result.Location?.Provider.StartsWith("ipwho.is") == true, "explicit-IP GeoIP fallback not used");
}

static async Task TestGeoFailure()
{
    var handler = new ScenarioHandler((request, _) => request.RequestUri!.Host switch
    {
        "chatgpt.com" => Text("ip=203.0.113.8\n"),
        "ipinfo.io" => Status(HttpStatusCode.TooManyRequests),
        "ipapi.co" => Status(HttpStatusCode.TooManyRequests),
        "ipwho.is" => Json("""{"success":false,"message":"rate limited"}"""),
        _ => throw new HttpRequestException("unexpected request")
    });
    var result = await new GeoIpService(new HttpClient(handler)).DetectAsync();
    Assert(!result.Success && result.Location is null && result.Message.Contains("203.0.113.8"), "GeoIP failure guessed a timezone");
}

static async Task TestTraceFailure()
{
    var handler = new ScenarioHandler((_, _) => Status(HttpStatusCode.ServiceUnavailable));
    var result = await new GeoIpService(new HttpClient(handler)).DetectAsync();
    Assert(!result.Success && result.Location is null && handler.Requests.All(x => !x.Contains("ipinfo.io") && !x.Contains("ipapi.co")),
        "trace failure fell back to default/self GeoIP exit");
}

static async Task TestWithoutClash()
{
    var handler = ExplicitUsScenario();
    var result = await new GeoIpService(new HttpClient(handler)).DetectAsync();
    Assert(result.Success && result.Location?.ProxyGroup is null && result.Location?.ProxyNode is null,
        "non-Clash network incorrectly required controller API");
}

static async Task TestDiscovery()
{
    var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FakeWindowsApps", "OpenAI.ChatGPT");
    var json = $$"""
        [{"PackageName":"OpenAI.ChatGPT-Desktop","PackageFullName":"OpenAI.ChatGPT_1.0_x64","PackageFamilyName":"OpenAI.ChatGPT_abc","Version":"1.0.0.0","InstallLocation":"{{Escape(root + "1")}}","AppId":"App","Executable":"app/ChatGPT.exe","StartName":"ChatGPT"},
         {"PackageName":"OpenAI.ChatGPT-Desktop","PackageFullName":"OpenAI.ChatGPT_2.0_x64","PackageFamilyName":"OpenAI.ChatGPT_abc","Version":"2.0.0.0","InstallLocation":"{{Escape(root + "2")}}","AppId":"App","Executable":"new/ChatGPT.exe","StartName":"ChatGPT"}]
        """;
    var result = await new ChatGptDiscovery(() => Task.FromResult(json)).DiscoverAsync();
    Assert(result.Found && result.Installation!.Version.Major == 2 && result.Installation.ExecutablePath.EndsWith(System.IO.Path.Combine("new", "ChatGPT.exe")),
        "newest dynamic package path not selected");
}

static async Task TestNotInstalled()
{
    var result = await new ChatGptDiscovery(() => Task.FromResult("[]")).DiscoverAsync();
    Assert(!result.Found && result.Diagnostics.Contains("未发现"), "missing install diagnostics absent");
}

static async Task TestTzLaunch()
{
    using var temp = new TempDirectory(); var exe = System.IO.Path.Combine(temp.Path, "UniqueChatGptTest.exe"); File.WriteAllBytes(exe, [0]);
    var directStarted = false; ChatGptInstallation? captured = null; string? capturedZone = null;
    var before = Environment.GetEnvironmentVariable("TZ");
    var launcher = new ChatGptLauncher(_ => { directStarted = true; return null; },
        (installation, zone) => { captured = installation; capturedZone = zone;
            return Task.FromResult(new PackageLaunchResult(true, 1234, installation.PackageFullName, "verified")); });
    var install = FakeInstall(temp.Path, exe);
    var result = await launcher.LaunchAsync(install, "America/New_York");
    Assert(result.Success && captured == install && capturedZone == "America/New_York" && !directStarted,
        "timezone launch bypassed package activation");
    Assert(Environment.GetEnvironmentVariable("TZ") == before, "launcher changed its own TZ");
}

static async Task TestDefaultLaunch()
{
    using var temp = new TempDirectory(); var exe = System.IO.Path.Combine(temp.Path, "UniqueChatGptDefaultTest.exe"); File.WriteAllBytes(exe, [0]);
    ProcessStartInfo? captured = null; var launcher = new ChatGptLauncher(info => { captured = info; return null; });
    var result = await launcher.LaunchAsync(FakeInstall(temp.Path, exe), null);
    Assert(result.Success && captured?.FileName == "explorer.exe" && !captured.Environment.ContainsKey("TZ") &&
           captured.ArgumentList.Single().Contains("shell:AppsFolder"), "default launch retained TZ or skipped AppX activation");
}

static async Task TestDefaultExisting()
{
    ProcessStartInfo? captured = null; var launcher = new ChatGptLauncher(info => { captured = info; return null; });
    var current = Environment.ProcessPath!;
    var install = FakeInstall(System.IO.Path.GetDirectoryName(current)!, current);
    var result = await launcher.LaunchAsync(install, null);
    Assert(result.Success && captured?.FileName == "explorer.exe", "default activation incorrectly required restart");
}

static async Task TestTzExisting()
{
    var started = false; var launcher = new ChatGptLauncher(_ => { started = true; return null; });
    var current = Environment.ProcessPath!;
    var install = FakeInstall(System.IO.Path.GetDirectoryName(current)!, current);
    var result = await launcher.LaunchAsync(install, "Asia/Tokyo");
    Assert(!result.Success && result.WasAlreadyRunning && !started, "existing process was incorrectly relaunched with TZ");
}

static async Task TestPackageFailure()
{
    using var temp = new TempDirectory(); var exe = System.IO.Path.Combine(temp.Path, "UniquePackageFailure.exe"); File.WriteAllBytes(exe, [0]);
    var launcher = new ChatGptLauncher(startPackaged: (_, _) =>
        Task.FromResult(new PackageLaunchResult(false, 10, null, "package identity missing")));
    var result = await launcher.LaunchAsync(FakeInstall(temp.Path, exe), "Asia/Tokyo");
    Assert(!result.Success && result.Message.Contains("package identity missing"), "package failure was hidden");
    launcher = new ChatGptLauncher(startPackaged: (_, _) => throw new TimeoutException("activation timed out"));
    result = await launcher.LaunchAsync(FakeInstall(temp.Path, exe), "Asia/Tokyo");
    Assert(!result.Success && result.Message.Contains("activation timed out"), "timeout was hidden");
}

static async Task TestInvalidLaunch()
{
    var started = false;
    var launcher = new ChatGptLauncher(startPackaged: (_, _) => { started = true; throw new Exception("must not start"); });
    var result = await launcher.LaunchAsync(FakeInstall(System.IO.Path.GetTempPath(), "NoSuchChatGPT.exe"), "Mars/Olympus");
    Assert(!result.Success && !started && result.Message.Contains("时区无效"), "invalid timezone reached activation");
}

static async Task TestMissingExecutable()
{
    var started = false;
    var launcher = new ChatGptLauncher(startPackaged: (_, _) => { started = true; throw new Exception("must not start"); });
    var result = await launcher.LaunchAsync(FakeInstall(System.IO.Path.GetTempPath(), "NoSuchChatGPT.exe"), "Asia/Tokyo");
    Assert(!result.Success && !started && result.Message.Contains("更新"), "missing executable reached activation");
}

static async Task TestDefaultInheritedTz()
{
    var before = Environment.GetEnvironmentVariable("TZ");
    try
    {
        Environment.SetEnvironmentVariable("TZ", "Asia/Tokyo");
        await TestDefaultLaunch();
        Assert(Environment.GetEnvironmentVariable("TZ") == "Asia/Tokyo", "default launch changed parent environment");
    }
    finally { Environment.SetEnvironmentVariable("TZ", before); }
}

static async Task TestMissingPackageActivation()
{
    try
    {
        await new PackagedProcessLauncher().LaunchAsync(FakeInstall(System.IO.Path.GetTempPath(), "NoSuchChatGPT.exe"), "Asia/Tokyo");
        throw new Exception("unregistered package unexpectedly launched");
    }
    catch (InvalidOperationException ex)
    {
        Assert(ex.Message.Contains("Windows 包内启动失败"), "activation error lost Windows diagnostics");
    }
}

static ScenarioHandler ExplicitUsScenario() => new((request, _) => request.RequestUri!.Host switch
{
    "chatgpt.com" => Text("ip=203.0.113.8\nloc=US\n"),
    "ipinfo.io" when request.RequestUri.AbsolutePath.Contains("203.0.113.8") => UsGeo(),
    _ => throw new HttpRequestException("unexpected request")
});

static HttpResponseMessage UsGeo() => Json(
    """{"ip":"203.0.113.8","city":"Los Angeles","country":"US","timezone":"America/Los_Angeles"}""");
static HttpResponseMessage Text(string value) => new(HttpStatusCode.OK)
    { Content = new StringContent(value, Encoding.UTF8, "text/plain") };
static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    { Content = new StringContent(value, Encoding.UTF8, "application/json") };
static HttpResponseMessage Status(HttpStatusCode status) => new(status);

static ChatGptInstallation FakeInstall(string root, string exe) => new("OpenAI.ChatGPT", "OpenAI.ChatGPT_1", "OpenAI.ChatGPT_abc",
    new Version(1, 0), root, "App", exe, null);
static string Escape(string value) => value.Replace("\\", "\\\\");
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

sealed class ScenarioHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responseFactory) : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responseFactory = responseFactory;
    public int Calls { get; private set; }
    public List<string> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++; Requests.Add(request.RequestUri!.AbsoluteUri);
        var response = _responseFactory(request, Calls);
        response.RequestMessage ??= request;
        return Task.FromResult(response);
    }
}

sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ChatGptTzTests", Guid.NewGuid().ToString("N"));
    public TempDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}
