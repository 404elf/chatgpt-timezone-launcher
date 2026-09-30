using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ChatGptTimezoneLauncher;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Integration <IANA timezone|default> <published launcher exe>");
    return 2;
}

var zone = args.FirstOrDefault() ?? "America/New_York";
var expectedZone = zone == "default" ? null : zone;
var initial = Process.GetProcessesByName("ChatGPT").Where(p => !p.HasExited).Select(p => p.Id).Order().ToArray();
var systemZone = TimeZoneInfo.Local.Id;
var userTz = Environment.GetEnvironmentVariable("TZ", EnvironmentVariableTarget.User);
var machineTz = Environment.GetEnvironmentVariable("TZ", EnvironmentVariableTarget.Machine);
var discovery = await new ChatGptDiscovery().DiscoverAsync();
if (discovery.Installation is not { } installation) throw new Exception(discovery.Diagnostics);
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
listener.Stop();
var browserProfile = Path.Combine(AppContext.BaseDirectory, "isolated-browser-" + Guid.NewGuid().ToString("N"));
installation = installation with { Parameters = $"--user-data-dir=\"{browserProfile}\" --no-first-run --remote-debugging-address=127.0.0.1 --remote-debugging-port={port}" };
using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
var launch = await new PackagedProcessLauncher(Path.GetFullPath(args[1])).LaunchAsync(installation, expectedZone);
Console.WriteLine(JsonSerializer.Serialize(new { stage = "launch", zone, launch }));
if (!launch.Success) return 1;
using var launchedProcess = Process.GetProcessById(launch.ProcessId);
string? browserSocket = null;
object? rendererResult = null;
try
{
    var deadline = DateTime.UtcNow.AddSeconds(35);
    while (DateTime.UtcNow < deadline)
    {
        try
        {
            using var version = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/version"));
            browserSocket = version.RootElement.GetProperty("webSocketDebuggerUrl").GetString();
            using var targets = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list"));
            var target = targets.RootElement.EnumerateArray().FirstOrDefault(x => x.GetProperty("type").GetString() == "page");
            if (target.ValueKind == JsonValueKind.Undefined) { await Task.Delay(500); continue; }
            var socket = target.GetProperty("webSocketDebuggerUrl").GetString()!;
            var response = await Cdp(socket, "Runtime.evaluate", new { expression = "JSON.stringify({timezone:Intl.DateTimeFormat().resolvedOptions().timeZone,offset:new Date().getTimezoneOffset(),ready:document.readyState})", returnByValue = true });
            var value = response.GetProperty("result").GetProperty("result").GetProperty("value").GetString()!;
            using var valueDocument = JsonDocument.Parse(value);
            var actual = valueDocument.RootElement.GetProperty("timezone").GetString();
            rendererResult = new { zone, actual, renderer = valueDocument.RootElement.Clone(), targetUrl = target.GetProperty("url").GetString() };
            Console.WriteLine(JsonSerializer.Serialize(rendererResult));
            if (expectedZone is not null && actual != expectedZone) throw new InvalidOperationException($"TZ mismatch: {actual} != {expectedZone}");
            if (expectedZone is null && valueDocument.RootElement.GetProperty("offset").GetInt32() != -(int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes)
                throw new InvalidOperationException("Default timezone differs from Windows");
            break;
        }
        catch (HttpRequestException) { await Task.Delay(500); }
        catch (TaskCanceledException) { await Task.Delay(500); }
    }
    if (rendererResult is null) throw new TimeoutException("No renderer result");
}
finally
{
    if (browserSocket is not null)
    {
        try { await Cdp(browserSocket, "Browser.close", new { }); }
        catch (WebSocketException) { }
    }
    else
    {
        launchedProcess.CloseMainWindow();
    }
}
var closedNormally = true;
try
{
    var closing = launchedProcess;
    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    try { await closing.WaitForExitAsync(closeTimeout.Token); }
    catch (OperationCanceledException)
    {
        // Only this harness-created process, using its fresh temporary profile.
        // Never terminate any PID captured before the test.
        if (initial.Contains(closing.Id)) throw;
        closing.Kill();
        await closing.WaitForExitAsync();
        closedNormally = false;
    }
}
catch (ArgumentException) { }
var after = Process.GetProcessesByName("ChatGPT").Where(p => !p.HasExited).Select(p => p.Id).ToHashSet();
if (!initial.All(after.Contains)) throw new Exception("An original ChatGPT process disappeared");
if (after.Contains(launch.ProcessId)) throw new Exception("Isolated test instance did not close");
if (TimeZoneInfo.Local.Id != systemZone || Environment.GetEnvironmentVariable("TZ", EnvironmentVariableTarget.User) != userTz || Environment.GetEnvironmentVariable("TZ", EnvironmentVariableTarget.Machine) != machineTz)
    throw new Exception("System timezone/environment changed");
Console.WriteLine(JsonSerializer.Serialize(new { result = "PASS", zone, installation.PackageFullName, originalProcessesPreserved = initial.Length, isolatedInstanceClosed = true, closedNormally, systemTimezoneUnchanged = systemZone }));
return 0;

static async Task<JsonElement> Cdp(string socket, string method, object parameters)
{
    using var client = new ClientWebSocket();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await client.ConnectAsync(new Uri(socket), timeout.Token);
    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id = 1, method, @params = parameters }));
    await client.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token);
    var buffer = new byte[65536];
    while (true)
    {
        using var stream = new MemoryStream();
        WebSocketReceiveResult part;
        do
        {
            part = await client.ReceiveAsync(buffer, timeout.Token);
            if (part.MessageType == WebSocketMessageType.Close) return default;
            stream.Write(buffer, 0, part.Count);
        } while (!part.EndOfMessage);
        using var document = JsonDocument.Parse(stream.ToArray());
        if (document.RootElement.TryGetProperty("id", out var id) && id.GetInt32() == 1) return document.RootElement.Clone();
    }
}
