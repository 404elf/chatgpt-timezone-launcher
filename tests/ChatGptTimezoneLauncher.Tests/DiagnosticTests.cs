using ChatGptTimezoneLauncher;

internal static class DiagnosticTests
{
    public static readonly (string Name, Func<Task> Run)[] Tests =
    [
        ("empty Windows activation output still reports decimal and hex exit codes", TestEmptyOutput),
        ("converted launch errors retain full process output in an exported log", TestLaunchFailure),
        ("diagnostic exports redact sensitive fields, preserve versions and keep previous sessions", TestPrivacy),
        ("log persistence failure still permits export without overwriting user files", TestFallback),
        ("bounded log chunks preserve the latest failure without deleting old logs", TestLogLimit)
    ];

    private static Task TestEmptyOutput()
    {
        var error = PackagedProcessLauncher.CreateActivationException(unchecked((int)0xC0000142), "", "");
        Require(error.Message.Contains("0xC0000142") && error.Message.Contains("Windows 未返回错误说明") &&
            error.Data["Activation.Stderr"]?.ToString() == "<empty>", "empty output recreated the blank error");
        var stdoutOnly = PackagedProcessLauncher.CreateActivationException(3, "stdout-only explanation", "");
        Require(stdoutOnly.Message.Contains("stdout-only explanation"), "standard output was discarded");
        return Task.CompletedTask;
    }

    private static async Task TestLaunchFailure()
    {
        using var temp = new TempDirectory();
        var logger = new DiagnosticLog(Path.Combine(temp.Path, "logs"));
        var executable = Path.Combine(temp.Path, "DiagnosticFixture.exe");
        File.WriteAllText(executable, "not executed");
        var install = new ChatGptInstallation("OpenAI.ChatGPT", "OpenAI.ChatGPT_1.2.3.4_x64__test", "OpenAI.ChatGPT_test",
            new Version(1, 2, 3, 4), temp.Path, "ChatGPT", executable, null);
        var output = new string('x', 1000) + " output-tail-marker";
        var launcher = new ChatGptLauncher(startPackaged: (_, _) => throw
            PackagedProcessLauncher.CreateActivationException(19, output, "native-error-marker"), diagnostics: logger);
        var result = await launcher.LaunchAsync(install, "Asia/Tokyo");
        Require(!result.Success && result.Message.Contains("0x00000013"), "converted launch result lost exit status");
        var export = Path.Combine(temp.Path, "feedback.txt"); logger.Export(export);
        var text = File.ReadAllText(export);
        Require(text.Contains("output-tail-marker") && text.Contains("native-error-marker") &&
            text.Contains("Activation.ExitCode") && text.Contains("AppId=ChatGPT") && text.Contains("launch-exception"),
            "automatic logs did not retain the real launch failure context");
    }

    private static Task TestPrivacy()
    {
        using var temp = new TempDirectory();
        var logs = Path.Combine(temp.Path, "logs");
        var first = new DiagnosticLog(logs);
        first.Write("previous-failure", "previous-session-marker");
        var current = new DiagnosticLog(logs);
        current.Write("privacy", "LauncherVersion: 1.4.0.0; IP=203.0.113.21\nPath=C:\\Users\\PrivatePerson\\Desktop\\launcher.exe\n" +
            "JSONPath=C:\\\\Users\\\\PrivatePerson\\\\Desktop\\\\launcher.exe\n" +
            "IP=203.0.113.21; IPv6=2001:db8::1234\nAuthorization: Bearer private-bearer\n" +
            "token=private-token&password=private-pass\n\"access_token\":\"private-json\"\n" +
            "proxy=https://private-user:private-proxy@localhost:8080\n");
        var export = Path.Combine(temp.Path, "feedback.txt"); current.Export(export);
        var text = File.ReadAllText(export);
        foreach (var secret in new[] { "PrivatePerson", "203.0.113.21", "2001:db8::1234", "private-bearer", "private-token", "private-pass", "private-json", "private-user", "private-proxy" })
            Require(!text.Contains(secret), "diagnostic export exposed " + secret);
        Require(text.Contains("LauncherVersion: 1.4.0.0") && text.Contains("previous-session-marker") &&
            text.Contains("Architecture:") && text.Contains("<redacted>"), "redaction destroyed versions or previous-session context");
        Require(!File.ReadAllText(current.LogPath!).Contains("PrivatePerson"), "automatic log was not redacted before saving");
        return Task.CompletedTask;
    }

    private static Task TestFallback()
    {
        using var temp = new TempDirectory();
        var blocked = Path.Combine(temp.Path, "cannot-be-a-directory"); File.WriteAllText(blocked, "keep-original");
        var logger = new DiagnosticLog(blocked); logger.Write("failure", "memory-fallback-marker");
        Require(logger.LogPath is null, "invalid log directory unexpectedly succeeded");
        var export = Path.Combine(temp.Path, "feedback.txt"); logger.Export(export);
        Require(File.ReadAllText(export).Contains("memory-fallback-marker"), "persistence failure lost the export");
        try { logger.Export(export); throw new Exception("existing file was overwritten"); }
        catch (IOException) { }
        Require(File.ReadAllText(blocked) == "keep-original" && File.ReadAllText(export).Contains("memory-fallback-marker"), "export damaged existing files");
        return Task.CompletedTask;
    }

    private static Task TestLogLimit()
    {
        using var temp = new TempDirectory();
        var logger = new DiagnosticLog(temp.Path);
        var firstPath = logger.LogPath!;
        for (var i = 0; i < 10; i++) logger.Write("repeated-stage", new string('x', 12000));
        logger.Error("latest-failure", new InvalidOperationException("last-error-marker"));
        var export = Path.Combine(temp.Path, "feedback.txt"); logger.Export(export);
        Require(File.Exists(firstPath) && Directory.GetFiles(temp.Path, "*.log").Length > 1 &&
            File.ReadAllText(export).Contains("last-error-marker"), "log limit discarded the latest error or deleted its history");
        Require(Directory.GetFiles(temp.Path, "*.log").All(x => new FileInfo(x).Length < 200000), "automatic log chunks became unbounded");
        return Task.CompletedTask;
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
