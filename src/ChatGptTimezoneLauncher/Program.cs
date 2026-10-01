namespace ChatGptTimezoneLauncher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == PackagedProcessLauncher.HelperArgument)
            return PackagedProcessLauncher.RunHelperAsync(args[1]).GetAwaiter().GetResult();
        var selfTest = args.Length == 2 && args[0] == "--self-test";
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            StartupDiagnostics.Report(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        try
        {
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
            if (selfTest) StartupDiagnostics.Write(ex);
            else StartupDiagnostics.Report(ex);
            return 1;
        }
    }
}
