using System.Diagnostics;

namespace ChatGptTimezoneLauncher;

public sealed class ChatGptLauncher
{
    private readonly Func<ProcessStartInfo, Process?> _startProcess;
    private readonly Func<ChatGptInstallation, string?, Task<PackageLaunchResult>> _startPackaged;

    public ChatGptLauncher(Func<ProcessStartInfo, Process?>? startProcess = null,
        Func<ChatGptInstallation, string?, Task<PackageLaunchResult>>? startPackaged = null)
    {
        _startProcess = startProcess ?? Process.Start;
        _startPackaged = startPackaged ?? new PackagedProcessLauncher().LaunchAsync;
    }

    public bool IsRunning(ChatGptInstallation installation) => FindRunning(installation).Count > 0;

    public async Task<bool> WaitForRunningAsync(ChatGptInstallation installation, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        do
        {
            if (IsRunning(installation)) return true;
            await Task.Delay(250);
        } while (DateTime.UtcNow < deadline);
        return false;
    }

    public async Task<LaunchResult> LaunchAsync(ChatGptInstallation installation, string? ianaTimeZone)
    {
        var running = IsRunning(installation);
        if (running && ianaTimeZone is not null)
            return new LaunchResult(false, true, "ChatGPT 已经在运行。新的时区将在 ChatGPT 重启后生效。");

        try
        {
            ProcessStartInfo start;
            if (ianaTimeZone is null)
            {
                start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                start.ArgumentList.Add($"shell:AppsFolder\\{installation.Aumid}");
                start.Environment.Remove("TZ");
            }
            else
            {
                if (!TimeZoneCatalog.IsValid(ianaTimeZone))
                    return new LaunchResult(false, false, "时区无效，未启动 ChatGPT。");
                if (!File.Exists(installation.ExecutablePath))
                    return new LaunchResult(false, false, $"ChatGPT 入口文件不存在，可能刚刚完成更新。请重试。\r\n{installation.ExecutablePath}");

                var result = await _startPackaged(installation, ianaTimeZone);
                return new LaunchResult(result.Success, false, result.Success
                    ? $"已启动 ChatGPT，本次进程时区：{ianaTimeZone}。\r\n已验证包身份与初始启动（PID {result.ProcessId}）。"
                    : $"启动失败：{result.Message}");
            }

            _startProcess(start);
            return new LaunchResult(true, false, "已请求按 ChatGPT 默认方式启动（未注入 TZ）。");
        }
        catch (Exception ex)
        {
            return new LaunchResult(false, false, $"启动失败：{ex.Message}");
        }
    }

    public async Task<(bool Success, string Message)> CloseGracefullyAsync(ChatGptInstallation installation)
    {
        var processes = FindRunning(installation);
        if (processes.Count == 0) return (true, "ChatGPT 当前未运行。");
        foreach (var process in processes)
        {
            try { process.CloseMainWindow(); } catch { }
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
            if (!IsRunning(installation)) return (true, "ChatGPT 已正常关闭。");
        }
        return (false, "ChatGPT 未能正常关闭。启动器不会强制结束进程，请手动退出 ChatGPT 后重试。");
    }

    private static List<Process> FindRunning(ChatGptInstallation installation)
    {
        var result = new List<Process>();
        var expectedName = Path.GetFileNameWithoutExtension(installation.ExecutablePath);
        foreach (var process in Process.GetProcessesByName(expectedName))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (path is not null && path.StartsWith(installation.InstallLocation + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)) result.Add(process);
                else if (path is null) result.Add(process);
            }
            catch { result.Add(process); }
        }
        return result;
    }

}
