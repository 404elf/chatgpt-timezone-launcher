using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ChatGptTimezoneLauncher;

public sealed class DiagnosticLog
{
    private const int MaxSessionChars = 64 * 1024;
    private readonly object _gate = new();
    private readonly StringBuilder _session = new();
    private readonly string _directory;
    private readonly string _header;
    public string? LogPath { get; private set; }

    public DiagnosticLog(string directory)
    {
        _directory = directory;
        var header = $"ChatGPT Timezone Launcher diagnostics\nTime: {DateTimeOffset.Now:O}\n" +
            $"LauncherVersion: {typeof(MainForm).Assembly.GetName().Version}\n" +
            $"OS: {RuntimeInformation.OSDescription} ({Environment.OSVersion.Version})\n" +
            $"Architecture: {RuntimeInformation.ProcessArchitecture}; OS64Bit: {Environment.Is64BitOperatingSystem}\n" +
            $"Runtime: {RuntimeInformation.FrameworkDescription}\nSystemTimeZone: {TimeZoneInfo.Local.Id}\n" +
            $"LauncherPath: {Environment.ProcessPath}\n" +
            "Privacy: user paths, IP addresses and credential fields are redacted; no chats, account data or proxy configuration are collected.\n";
        _header = Redact(header);
        _session.AppendLine(_header);
        OpenLogFile();
    }

    private void OpenLogFile()
    {
        LogPath = null;
        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, $"launcher-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(file, new UTF8Encoding(false));
            writer.Write(_session.ToString());
            LogPath = path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _session.AppendLine("Log persistence unavailable; export uses the in-memory session. " + Redact(ex.Message)); }
    }

    public void Write(string stage, string message)
    {
        lock (_gate)
        {
            var entry = Redact($"[{DateTimeOffset.Now:O}] [{stage}] {message}\n");
            var entryLimit = MaxSessionChars - _header.Length - 256;
            if (entry.Length > entryLimit) entry = entry[..entryLimit] + "\n[entry truncated]\n";
            if (_session.Length + entry.Length > MaxSessionChars)
            {
                // Start a new bounded chunk; preserve old files and keep the latest failure.
                _session.Clear();
                _session.AppendLine(_header).AppendLine("[earlier entries retained in previous chunks when persistence is available]");
                OpenLogFile();
            }
            _session.Append(entry);
            try { if (LogPath is not null) File.AppendAllText(LogPath, entry, new UTF8Encoding(false)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LogPath = null; }
        }
    }

    public void Error(string stage, Exception exception)
    {
        var data = string.Join("\n", exception.Data.Keys.Cast<object>().Select(key => $"{key}: {exception.Data[key]}"));
        Write(stage, $"HResult: 0x{unchecked((uint)exception.HResult):X8}\n{exception}\n{data}");
    }

    public void Export(string destination)
    {
        // A fresh name avoids overwriting either user files or the automatic recovery logs.
        using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(file, new UTF8Encoding(true));
        lock (_gate) writer.Write(_session.ToString());
        writer.WriteLine("\n--- Previous sessions (up to four, bounded and redacted) ---");
        try
        {
            var recent = Directory.EnumerateFiles(_directory, "*.log")
                .Where(path => path != LogPath && (Path.GetFileName(path).StartsWith("launcher-") || Path.GetFileName(path).StartsWith("error-")))
                .OrderByDescending(File.GetLastWriteTimeUtc).Take(4).ToArray();
            foreach (var path in recent)
            {
                try
                {
                    using var reader = new StreamReader(path, Encoding.UTF8);
                    var buffer = new char[MaxSessionChars];
                    var count = reader.ReadBlock(buffer, 0, buffer.Length);
                    writer.WriteLine("\n--- " + Path.GetFileName(path) + " ---");
                    writer.WriteLine(Redact(new string(buffer, 0, count)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { writer.WriteLine("Previous log unavailable: " + Redact(ex.Message)); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { writer.WriteLine("Previous sessions unavailable: " + Redact(ex.Message)); }
    }

    public static string Redact(string text)
    {
        text = Regex.Replace(text, @"(?i)([A-Z]:[/\\]+Users[/\\]+)[^/\\\r\n]+(?=[/\\])", "$1<user>");
        text = Regex.Replace(text, @"(?i)\b(https?|socks5?)://[^/\s@]+@", "$1://<redacted>@");
        text = Regex.Replace(text, @"(?im)(\b(?:authorization|proxy-authorization|cookie|set-cookie)\s*[:=]\s*)[^\r\n]+", "$1<redacted>");
        text = Regex.Replace(text, """(?i)(\b(?:access_token|refresh_token|api_key|apikey|token|password|passwd)(?:\\?["'])?\s*[=:]\s*)(?:"[^"]*"|'[^']*'|[^\s;&\r\n]+)""", "$1<redacted>");
        text = Regex.Replace(text, @"(?i)(?<version>\b(?:[A-Z]*Version|Runtime)\s*[:=]\s*(?:\.NET\s+)?\d+(?:\.\d+){1,3})|(?<ip>(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?![\w.]))", match =>
            match.Groups["version"].Success || !IPAddress.TryParse(match.Value, out _) ? match.Value : "<IPv4>");
        return Regex.Replace(text, @"(?<!\w)(?:[A-Fa-f0-9]{0,4}:){2,}[A-Fa-f0-9:.%]+", match =>
            IPAddress.TryParse(match.Value, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? "<IPv6>" : match.Value);
    }
}
