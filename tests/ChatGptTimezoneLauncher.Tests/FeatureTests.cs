using System.Drawing;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ChatGptTimezoneLauncher;

internal static class FeatureTests
{
    public static readonly (string Name, Func<Task> Run)[] Tests =
    [
        ("unsupported IP cannot enter any launch/restart workflow", TestBlockedRegions),
        ("supported exit permits a manual timezone from another region", TestSupportedRegions),
        ("failed or unknown detection never reuses a previous allowed exit", TestFreshExit),
        ("unsupported trace country cannot be hidden by GeoIP", TestTraceCountry),
        ("auto-close setting persists and only applies after successful launch", TestCloseSetting),
        ("default activation must find the client before auto-close", TestDefaultConfirmation),
        ("GitHub update selects the Windows asset and checksum", TestUpdateCheck),
        ("update checks refresh their network client after a proxy switch", TestUpdateRefresh),
        ("update checker rejects untrusted or unverified downloads", TestInvalidUpdate),
        ("update download verifies bytes and preserves existing files", TestDownload),
        ("main window settings and update link fit the layout", TestWindow),
        ("window resize and display scaling keep controls accessible", TestResponsive),
        ("slow UI detection remains cancellable without starting or saving an exit", TestCancelDetection)
    ];

    private static async Task TestBlockedRegions()
    {
        foreach (var country in new[] { "CN", "HK", "MO", "RU", "IR", "KP", "--", "", "ZZ" })
        foreach (var zone in new string?[] { null, "America/New_York", "Asia/Shanghai" })
        {
            var entered = false;
            var result = await LaunchProtection.RunAsync(() => Task.FromResult(DetectionResult.Ok(Location(country))), _ =>
            {
                entered = true; // This boundary includes discovery, restart and activation in MainForm.
                return Task.FromResult<LaunchResult?>(new(true, false, zone ?? "default"));
            });
            Require(!entered && result.Launch is null && result.IpError?.Contains("IP 错误") == true,
                $"{country}/{zone} bypassed protection");
        }
    }

    private static async Task TestSupportedRegions()
    {
        foreach (var country in new[] { "US", "JP", "TW", "GB", "SG", "DE", "kr" })
        {
            var result = await LaunchProtection.RunAsync(() => Task.FromResult(DetectionResult.Ok(Location(country))), _ =>
                Task.FromResult<LaunchResult?>(new(true, false, "manual Asia/Shanghai")));
            Require(result.IpError is null && result.Launch?.Success == true, $"supported {country} was blocked");
        }
    }

    private static async Task TestFreshExit()
    {
        var results = new Queue<DetectionResult>([
            DetectionResult.Ok(Location("US")), DetectionResult.Fail("timeout"), DetectionResult.Ok(Location("HK")),
            DetectionResult.Ok(Location("ZZ"))]);
        var entered = 0;
        for (var i = 0; i < 4; i++)
            await LaunchProtection.RunAsync(() => Task.FromResult(results.Dequeue()), _ =>
            { entered++; return Task.FromResult<LaunchResult?>(new(true, false, "launched")); });
        Require(entered == 1 && results.Count == 0, "stale success or unknown country reached launch");
    }

    private static async Task TestTraceCountry()
    {
        var service = new GeoIpService(new HttpClient(new ScenarioHandler((request, _) => request.RequestUri!.Host switch
        {
            "chatgpt.com" => Text("ip=203.0.113.8\nloc=HK\n"),
            "ipinfo.io" => Json("""{"ip":"203.0.113.8","country":"US","timezone":"America/New_York"}"""),
            _ => throw new Exception("unexpected request")
        })));
        var entered = false;
        var result = await LaunchProtection.RunAsync(() => service.DetectAsync(), _ =>
        { entered = true; return Task.FromResult<LaunchResult?>(new(true, false, "launched")); });
        Require(!entered && result.Location?.TraceCountryCode == "HK" && result.IpError is not null,
            "trace region was lost or ignored");
    }

    private static Task TestCloseSetting()
    {
        using var temp = new TempDirectory();
        var store = new ConfigStore(temp.Path);
        File.WriteAllText(store.ConfigPath, """{"SchemaVersion":1,"ManualTimeZone":"Asia/Tokyo"}""");
        var config = store.Load().Config;
        Require(!config.CloseLauncherAfterLaunch, "old config unexpectedly enables auto-close");
        config.CloseLauncherAfterLaunch = true; store.Save(config);
        Require(store.Load().Config.CloseLauncherAfterLaunch && store.Load().Config.ManualTimeZone == "Asia/Tokyo",
            "setting did not persist or old setting changed");
        Require(LaunchProtection.ShouldCloseLauncher(true, new(true, false, "ok")), "successful launch cannot close");
        Require(!LaunchProtection.ShouldCloseLauncher(false, new(true, false, "ok")) &&
                !LaunchProtection.ShouldCloseLauncher(true, new(false, false, "failed")) &&
                !LaunchProtection.ShouldCloseLauncher(true, null), "disabled, failed or cancelled launch closes the launcher");
        return Task.CompletedTask;
    }

    private static async Task TestUpdateCheck()
    {
        var hash = new string('a', 64);
        var service = new UpdateService(new HttpClient(new ScenarioHandler((_, _) => Json(ReleaseJson(hash)))));
        var update = await service.CheckAsync(new Version(1, 2, 0));
        Require(update?.Version == new Version(1, 3, 0) && update.Sha256 == hash && update.FileName.EndsWith("win-x64.exe"),
            "latest Windows update was not selected");
        Require(await service.CheckAsync(new Version(1, 3, 0, 0)) is null, "same version reported as newer");
        service = new UpdateService(new HttpClient(new ScenarioHandler((request, _) =>
            request.RequestUri!.Host == "api.github.com" ? Json(ReleaseJson(null, checksums: true)) :
                Text(hash + "  ChatGPT-TimeZone-Launcher-v1.3.0-win-x64.exe\n"))));
        Require((await service.CheckAsync(new Version(1, 2, 0)))?.Sha256 == hash, "checksum file fallback failed");
    }

    private static async Task TestDefaultConfirmation()
    {
        using var temp = new TempDirectory();
        var launcher = new ChatGptLauncher();
        var missing = new ChatGptInstallation("fake", "fake", "fake", new Version(1, 0), temp.Path,
            "App", Path.Combine(temp.Path, "UniqueAbsentConfirmation.exe"), null);
        Require(!await launcher.WaitForRunningAsync(missing, TimeSpan.FromMilliseconds(50)), "absent client was confirmed");
        var current = Environment.ProcessPath!;
        var running = missing with { InstallLocation = Path.GetDirectoryName(current)!, ExecutablePath = current };
        Require(await launcher.WaitForRunningAsync(running), "existing matching client was not confirmed");
    }

    private static async Task TestInvalidUpdate()
    {
        foreach (var json in new[] { ReleaseJson(null), ReleaseJson(new string('a', 64), external: true),
            """{"tag_name":"v1.3.0","prerelease":true,"assets":[]}""", "{}" })
        {
            var service = new UpdateService(new HttpClient(new ScenarioHandler((_, _) => Json(json))));
            try { await service.CheckAsync(new Version(1, 2, 0)); throw new Exception("invalid update was accepted"); }
            catch (InvalidDataException) { }
        }
        var offline = new UpdateService(new HttpClient(new ScenarioHandler((_, _) => new(HttpStatusCode.ServiceUnavailable))));
        try { await offline.CheckAsync(new Version(1, 2, 0)); throw new Exception("offline update was accepted"); }
        catch (HttpRequestException) { }
    }

    private static async Task TestUpdateRefresh()
    {
        var online = false; var clients = 0;
        var service = new UpdateService(clientFactory: () =>
        {
            clients++; var snapshot = online;
            return new HttpClient(new ScenarioHandler((_, _) => snapshot ? Json(ReleaseJson(new string('a', 64))) : new(HttpStatusCode.ServiceUnavailable)));
        });
        try { await service.CheckAsync(new Version(1, 0, 0)); throw new Exception("offline route was accepted"); }
        catch (HttpRequestException) { }
        online = true;
        Require(await service.CheckAsync(new Version(1, 0, 0)) is not null && clients == 2, "update check reused its old route");
    }

    private static async Task TestDownload()
    {
        using var temp = new TempDirectory();
        var bytes = Encoding.UTF8.GetBytes("verified update fixture");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var update = new LauncherUpdate(new Version(1, 3, 0), "ChatGPT-TimeZone-Launcher-v1.3.0-win-x64.exe",
            new("https://github.com/404elf/chatgpt-timezone-launcher/releases/download/v1.3.0/test.exe"), hash);
        var original = Path.Combine(temp.Path, update.FileName); File.WriteAllText(original, "original");
        var service = new UpdateService(new HttpClient(new ScenarioHandler((_, _) =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));
        var path = await service.DownloadAsync(update, temp.Path);
        Require(File.ReadAllBytes(path).SequenceEqual(bytes) && File.ReadAllText(original) == "original", "verified download overwrote an old file");
        try { await service.DownloadAsync(update with { Sha256 = new string('0', 64) }, temp.Path); throw new Exception("bad checksum was accepted"); }
        catch (InvalidDataException) { }
        Require(Directory.GetFiles(temp.Path, "*.exe", SearchOption.AllDirectories).Length == 2, "unverified download was exposed as executable");
    }

    private static Task TestWindow()
    {
        using var temp = new TempDirectory();
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                var store = new ConfigStore(temp.Path);
                using var form = new MainForm(store, checkUpdates: false);
                form.Show(); Application.DoEvents();
                var checkbox = AllControls(form).OfType<CheckBox>().Single(x => x.Text == "启动后自动关闭启动器");
                var update = AllControls(form).OfType<LinkLabel>().Single(x => x.Text == "↻ 更新");
                Require(!checkbox.Checked && !File.Exists(store.ConfigPath), "window startup saved settings or enabled auto-close");
                checkbox.Checked = true;
                Require(store.Load().Config.CloseLauncherAfterLaunch, "checkbox did not save its setting");
                Require(form.RectangleToClient(checkbox.RectangleToScreen(checkbox.ClientRectangle)).Bottom <= form.ClientSize.Height &&
                    form.RectangleToClient(update.RectangleToScreen(update.ClientRectangle)).Right <= form.ClientSize.Width,
                    "new controls are outside the window");
                Require(AllControls(form).OfType<TextBox>().Single(x => x.Multiline).Height >= 40, "diagnostic text area was collapsed");
                var preview = Environment.GetEnvironmentVariable("LAUNCHER_TEST_PREVIEW");
                if (!string.IsNullOrWhiteSpace(preview))
                {
                    AllControls(form).OfType<RadioButton>().Single(x => x.Text == "自动跟随出口").Checked = true;
                    typeof(MainForm).GetMethod("ShowLocation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                        .Invoke(form, [new GeoLocation("203.0.113.8", "US", "美国", "洛杉矶", "America/Los_Angeles", DateTimeOffset.Now,
                            "演示", "chatgpt.com/cdn-cgi/trace → 指定 IP GeoIP"), "出口检查通过"]);
                    Application.DoEvents();
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(preview);
                    var docPreview = Environment.GetEnvironmentVariable("LAUNCHER_DOC_PREVIEW");
                    if (!string.IsNullOrWhiteSpace(docPreview))
                    {
                        var origin = form.PointToScreen(Point.Empty) - new Size(form.Location);
                        using var cropped = bitmap.Clone(new Rectangle(origin, form.ClientSize), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        cropped.Save(docPreview);
                    }
                }
                form.Close();
                using var reopened = new MainForm(store, checkUpdates: false);
                Require(AllControls(reopened).OfType<CheckBox>().Single().Checked, "checkbox state was not restored");
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Require(thread.Join(TimeSpan.FromSeconds(20)), "window test timed out");
        if (error is not null) throw error;
        return Task.CompletedTask;
    }

    private static IEnumerable<Control> AllControls(Control control) =>
        control.Controls.Cast<Control>().SelectMany(child => AllControls(child).Prepend(child));

    private static Task TestResponsive()
    {
        using var temp = new TempDirectory();
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                foreach (var factor in new[] { 1f, 1.25f, 1.5f })
                foreach (var size in new[] { new Size(504, 501), new Size(660, 540), new Size(1024, 780) })
                {
                    using var form = new MainForm(new ConfigStore(temp.Path), checkUpdates: false);
                    if (factor != 1) form.Scale(new SizeF(factor, factor));
                    form.ClientSize = size;
                    form.Show(); Application.DoEvents();
                    AllControls(form).OfType<RadioButton>().Single(x => x.Text == "手动选择时区").Checked = true;
                    var link = AllControls(form).OfType<LinkLabel>().Single(x => x.Text == "检测详情 ▾");
                    typeof(LinkLabel).GetMethod("OnLinkClicked", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                        .Invoke(link, [new LinkLabelLinkClickedEventArgs(link.Links[0])]);
                    Application.DoEvents();
                    foreach (var control in AllControls(form).Where(x => x.Visible && x is Button or ComboBox or CheckBox))
                    {
                        var rectangle = form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                        Require(rectangle.Left >= 0 && rectangle.Right <= form.ClientSize.Width,
                            $"horizontal clipping at {size}/{factor}: {control.Text}");
                    }
                    Require(AllControls(form).OfType<ComboBox>().Single().Width >= 140, "manual search box became too narrow");
                    form.Close();
                }
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Require(thread.Join(TimeSpan.FromSeconds(30)), "responsive layout check timed out");
        if (error is not null) throw error;
        return Task.CompletedTask;
    }
    private static Task TestCancelDetection()
    {
        using var temp = new TempDirectory();
        Exception? error = null;
        var thread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            using var client = new HttpClient(new WaitingHandler());
            var store = new ConfigStore(temp.Path);
            using var form = new MainForm(store, checkUpdates: false, geoIp: new GeoIpService(client));
            form.Shown += async (_, _) =>
            {
                try
                {
                    var detect = AllControls(form).OfType<Button>().Single(x => x.Text == "重新检测");
                    var launch = AllControls(form).OfType<Button>().Single(x => x.Text.StartsWith("启动 ChatGPT"));
                    foreach (var fromLaunch in new[] { false, true })
                    {
                        (fromLaunch ? launch : detect).PerformClick();
                        Require(detect.Enabled && detect.Text == "取消检测" && !launch.Enabled,
                            "busy detection disabled its cancel action or allowed another launch");
                        if (!fromLaunch)
                        {
                            await Task.Delay(8200);
                            Require(AllControls(form).OfType<Label>().Any(x => x.Text.Contains("网络较慢，继续检测")) &&
                                detect.Enabled && detect.Text == "取消检测", "old deadline still ended the operation or progress was missing");
                        }
                        detect.PerformClick();
                        var cancellation = System.Diagnostics.Stopwatch.StartNew();
                        while (!launch.Enabled && cancellation.Elapsed < TimeSpan.FromSeconds(2))
                            await Task.Delay(10);
                        Require(launch.Enabled && detect.Enabled && detect.Text == "重新检测" &&
                            AllControls(form).OfType<Label>().Any(x => x.Text == "已取消检测（未启动）") &&
                            !File.Exists(store.ConfigPath), "cancellation was shown as failure, left UI busy or saved an exit");
                    }
                }
                catch (Exception ex) { error = ex; }
                finally { form.Close(); }
            };
            // Async UI actions need the real Windows message loop and synchronization context.
            Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Require(thread.Join(TimeSpan.FromSeconds(15)), "cancel UI test timed out");
        if (error is not null) throw error;
        return Task.CompletedTask;
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException("wait must be cancelled"); }
    }

    private static GeoLocation Location(string country) => new("203.0.113.8", country, country, null,
        "America/New_York", DateTimeOffset.Now, "test", "chatgpt.com/cdn-cgi/trace → 指定 IP GeoIP");
    private static string ReleaseJson(string? hash, bool external = false, bool checksums = false)
    {
        var prefix = external ? "https://example.com/" : "https://github.com/404elf/chatgpt-timezone-launcher/releases/download/v1.3.0/";
        var checksumAsset = checksums ? $$""",{"name":"SHA256SUMS-v1.3.0.txt","browser_download_url":"{{prefix}}SHA256SUMS-v1.3.0.txt"}""" : "";
        var digest = hash is null ? "null" : $"\"sha256:{hash}\"";
        return $$"""{"tag_name":"v1.3.0","draft":false,"prerelease":false,"assets":[{"name":"ChatGPT-TimeZone-Launcher-v1.3.0-win-x64.exe","browser_download_url":"{{prefix}}ChatGPT-TimeZone-Launcher-v1.3.0-win-x64.exe","digest":{{digest}}}{{checksumAsset}}]}""";
    }
    private static HttpResponseMessage Text(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
