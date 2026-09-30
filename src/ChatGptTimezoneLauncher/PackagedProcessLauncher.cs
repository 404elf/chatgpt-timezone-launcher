using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ChatGptTimezoneLauncher;

// The same executable briefly runs inside the existing ChatGPT package. Windows
// supplies its identity; only the new ChatGPT child receives the TZ override.
public sealed class PackagedProcessLauncher(string? helperPath = null)
{
    public const string HelperArgument = "--package-launch";
    private readonly string _helperPath = helperPath ?? Environment.ProcessPath!;

    public async Task<PackageLaunchResult> LaunchAsync(ChatGptInstallation installation, string? timeZone)
    {
        var pipeName = "ChatGptTimezoneLauncher-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var activation = Process.Start(CreateActivationStartInfo(installation, _helperPath, pipeName))
            ?? throw new InvalidOperationException("无法启动 Windows 包内启动接口。");
        var stdout = activation.StandardOutput.ReadToEndAsync();
        var stderr = activation.StandardError.ReadToEndAsync();
        var connected = pipe.WaitForConnectionAsync(timeout.Token);
        var exited = activation.WaitForExitAsync(timeout.Token);
        try
        {
            if (await Task.WhenAny(connected, exited) == exited)
            {
                await exited;
                if (activation.ExitCode != 0)
                    throw new InvalidOperationException($"Windows 包内启动失败：{(await stderr).Trim()}");
            }
            await connected;
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(new PackageLaunchRequest(installation, timeZone)).AsMemory(), timeout.Token);
            var response = await reader.ReadLineAsync(timeout.Token);
            return JsonSerializer.Deserialize<PackageLaunchResult>(response ?? "")
                ?? throw new InvalidOperationException("包内启动器没有返回验证结果。");
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("等待 ChatGPT 启动验证超时。请先检查 ChatGPT 是否已经打开，再重试。");
        }
    }

    public static ProcessStartInfo CreateActivationStartInfo(ChatGptInstallation installation, string helperPath, string pipeName)
    {
        // Every value is a PowerShell literal. The helper only receives a random
        // pipe name; paths, manifest arguments and TZ travel as JSON over the pipe.
        static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
        var script = $"$ErrorActionPreference='Stop'; " +
            "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); try { " +
            $"Invoke-CommandInDesktopPackage -PackageFamilyName {Literal(installation.PackageFamilyName)} " +
            $"-AppId {Literal(installation.AppId)} -Command {Literal(helperPath)} " +
            $"-Args {Literal(HelperArgument + " " + pipeName)} -PreventBreakaway -ErrorAction Stop; exit 0 " +
            "} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-WindowStyle");
        start.ArgumentList.Add("Hidden");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        start.Environment.Remove("TZ");
        return start;
    }

    public static async Task<int> RunHelperAsync(string pipeName)
    {
        if (!pipeName.StartsWith("ChatGptTimezoneLauncher-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(pipeName["ChatGptTimezoneLauncher-".Length..], "N", out _)) return 2;
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            await pipe.ConnectAsync(timeout.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            PackageLaunchResult result;
            try
            {
                var json = await reader.ReadLineAsync(timeout.Token);
                var request = JsonSerializer.Deserialize<PackageLaunchRequest>(json ?? "")
                    ?? throw new InvalidOperationException("启动请求为空。");
                result = await StartAndVerifyAsync(request);
            }
            catch (Exception ex) { result = new(false, 0, null, ex.Message); }
            await writer.WriteLineAsync(JsonSerializer.Serialize(result).AsMemory(), timeout.Token);
            return result.Success ? 0 : 1;
        }
        catch { return 1; }
    }

    private static async Task<PackageLaunchResult> StartAndVerifyAsync(PackageLaunchRequest request)
    {
        var installation = request.Installation;
        using var self = Process.GetCurrentProcess();
        var identity = GetPackageIdentity(self);
        if (!string.Equals(identity, installation.PackageFullName, StringComparison.Ordinal))
            throw new InvalidOperationException("启动器未获得当前 ChatGPT 包身份，已停止启动。请重新检测安装版本。");
        if (request.TimeZone is not null && !TimeZoneCatalog.IsValid(request.TimeZone))
            throw new InvalidOperationException("时区无效，未启动 ChatGPT。");
        var executable = Path.GetFullPath(installation.ExecutablePath);
        var root = Path.GetFullPath(installation.InstallLocation).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!executable.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
            throw new InvalidOperationException("ChatGPT 入口文件不存在或不属于当前安装目录，请重新检测。");

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            Arguments = installation.Parameters ?? ""
        };
        if (request.TimeZone is null) start.Environment.Remove("TZ");
        else start.Environment["TZ"] = request.TimeZone;
        using var child = Process.Start(start) ?? throw new InvalidOperationException("未能创建 ChatGPT 进程。");
        var childIdentity = GetPackageIdentity(child);
        if (!string.Equals(childIdentity, installation.PackageFullName, StringComparison.Ordinal))
            return new(false, child.Id, childIdentity, "ChatGPT 已创建，但未能验证其包身份。请关闭该实例后重试。");
        // Catch immediate startup crashes instead of reporting success at Process.Start.
        await Task.Delay(3000);
        if (child.HasExited)
            return new(false, child.Id, childIdentity, $"ChatGPT 启动后立即退出（退出码 {child.ExitCode}）。如已有实例，请先退出后重试。");
        return new(true, child.Id, childIdentity, "已验证 ChatGPT 进程包身份，并完成初始启动检查。");
    }

    public static string GetPackageIdentity(Process process)
    {
        uint length = 0;
        var error = GetPackageFullName(process.Handle, ref length, null);
        if (error != 122) throw new System.ComponentModel.Win32Exception(error, "无法读取进程包身份");
        var name = new StringBuilder((int)length);
        error = GetPackageFullName(process.Handle, ref length, name);
        if (error != 0) throw new System.ComponentModel.Win32Exception(error, "无法读取进程包身份");
        return name.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFullName(IntPtr process, ref uint length, StringBuilder? packageFullName);
}

public sealed record PackageLaunchRequest(ChatGptInstallation Installation, string? TimeZone);
public sealed record PackageLaunchResult(bool Success, int ProcessId, string? PackageFullName, string Message);
