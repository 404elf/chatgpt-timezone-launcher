namespace ChatGptTimezoneLauncher;

public enum TimeZoneMode { Auto, Manual }

public sealed class LauncherConfig
{
    public int SchemaVersion { get; set; } = 1;
    public bool TimeZoneOverrideEnabled { get; set; }
    public bool CloseLauncherAfterLaunch { get; set; }
    public TimeZoneMode Mode { get; set; } = TimeZoneMode.Auto;
    public string ManualTimeZone { get; set; } = "Asia/Shanghai";
    public GeoLocation? LastSuccessfulAutoDetection { get; set; }
}

public sealed record GeoLocation(string Ip, string CountryCode, string CountryName,
    string? City, string TimeZone, DateTimeOffset DetectedAt, string Provider,
    string DetectionMethod = "", string? ProxyGroup = null, string? ProxyNode = null,
    string? TraceCountryCode = null)
{
    public string LocationText => string.IsNullOrWhiteSpace(City)
        ? $"{CountryName} ({CountryCode})"
        : $"{City}, {CountryCode}";
}

public sealed record DetectionResult(bool Success, GeoLocation? Location, string Message)
{
    public static DetectionResult Ok(GeoLocation value) => new(true, value, "检测成功");
    public static DetectionResult Fail(string message) => new(false, null, message);
}

public sealed record ChatGptInstallation(string PackageName, string PackageFullName,
    string PackageFamilyName, Version Version, string InstallLocation,
    string AppId, string ExecutablePath, string? Parameters)
{
    public string Aumid => $"{PackageFamilyName}!{AppId}";
}

public sealed record DiscoveryResult(ChatGptInstallation? Installation, string Diagnostics)
{
    public bool Found => Installation is not null;
}

public sealed record LaunchResult(bool Success, bool WasAlreadyRunning, string Message);
