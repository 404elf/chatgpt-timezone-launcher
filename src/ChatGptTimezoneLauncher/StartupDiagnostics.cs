using System.Runtime.InteropServices;

namespace ChatGptTimezoneLauncher;

internal static class StartupDiagnostics
{
    public static string? Write(Exception exception)
    {
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.GetTempPath()
        })
        {
            try
            {
                var directory = Path.Combine(root, "ChatGPTTimezoneLauncher", "logs");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"error-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
                File.WriteAllText(path,
                    $"Time: {DateTimeOffset.Now:O}\nVersion: {typeof(Program).Assembly.GetName().Version}\n" +
                    $"OS: {RuntimeInformation.OSDescription}\nArchitecture: {RuntimeInformation.ProcessArchitecture}\n" +
                    $"Runtime: {RuntimeInformation.FrameworkDescription}\n{exception}");
                return path;
            }
            catch { /* A logging failure must not hide the original error. */ }
        }
        return null;
    }

    public static void Report(Exception exception)
    {
        var log = Write(exception);
        var message = "程序发生错误：\r\n" + exception.Message + "\r\n\r\n" +
            (log is null ? "无法写入诊断日志，请记录此错误信息。" : "请将以下日志提供给维护者：\r\n" + log);
        // Native dialog also works when WinForms initialization itself fails.
        MessageBoxW(IntPtr.Zero, message, "ChatGPT 时区启动器", 0x10);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
