namespace ChatGptTimezoneLauncher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == PackagedProcessLauncher.HelperArgument)
            return PackagedProcessLauncher.RunHelperAsync(args[1]).GetAwaiter().GetResult();
        var selfTest = args.Length == 2 && args[0] == "--self-test";
        var networkSelfTest = args.Length == 2 && args[0] == "--network-self-test";
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            StartupDiagnostics.Report(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        try
        {
            if (networkSelfTest)
            {
                var directory = Path.GetFullPath(args[1]); Directory.CreateDirectory(directory);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var stages = new List<string>();
                var detection = new GeoIpService().DetectAsync(report: stages.Add).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(directory, "network-result.json"), System.Text.Json.JsonSerializer.Serialize(new
                {
                    detection.Success, country = detection.Location?.CountryCode,
                    traceCountry = detection.Location?.TraceCountryCode,
                    allowed = LaunchProtection.GetIpError(detection) is null, elapsedMs = watch.ElapsedMilliseconds, stages
                }));
                return detection.Success ? 0 : 1;
            }
            Application.SetUnhandledExceptionMode(selfTest
                ? UnhandledExceptionMode.ThrowException : UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => StartupDiagnostics.Report(e.Exception);
            ApplicationConfiguration.Initialize();
            using var form = new MainForm(selfTest ? new ConfigStore(Path.GetFullPath(args[1])) : null, checkUpdates: !selfTest);
            if (selfTest)
            {
                // Exercise the real window and message loop, without touching user settings or ChatGPT.
                form.Shown += (_, _) => form.BeginInvoke(() =>
                {
                    File.WriteAllText(Path.Combine(args[1], "startup-ok.txt"),
                        $"Window created; timezones={TimeZoneCatalog.All.Count}; runtime={Environment.Version}");
                    form.Close();
                });
            }
            Application.Run(form);
            return 0;
        }
        catch (Exception ex)
        {
            if (selfTest || networkSelfTest) StartupDiagnostics.Write(ex);
            else StartupDiagnostics.Report(ex);
            return 1;
        }
    }
}
