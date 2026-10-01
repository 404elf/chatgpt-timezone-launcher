namespace ChatGptTimezoneLauncher;

public sealed record ProtectedLaunchResult(GeoLocation? Location, LaunchResult? Launch, string? IpError);

public static class LaunchProtection
{
    // OpenAI ChatGPT supported countries/territories, checked 2026-10-01.
    // https://help.openai.com/en/articles/7947663-chatgpt-supported-countries
    private static readonly HashSet<string> SupportedCountries = new((
        "AL DZ AF AX AD AO AG AR AM AW AU AT AZ BS BH BD BB BE BZ BM " +
        "BJ BT BO BA BW BR BN BG BF BI CV KH CM CA KY CF TD CL CO KM " +
        "CG CD CR CI HR CY CZ DK DJ DM DO EC EG SV GQ ER EE SZ ET FO " +
        "FJ FI FR GF PF TF GA GM GE DE GH GR GD GL GT GP GN GW GY HT " +
        "VA HN HU IS IN ID IQ IE IL IT JM JP JO KZ KE KI KW KG LA LV " +
        "LB LS LR LY LI LT LU MG MW MY MV ML MT MH MQ MR MU YT MX FM " +
        "MD MC MN ME MA MZ MM NA NR NP NL NC NZ NI NE NG MK NO OM PK " +
        "PW PS PA PG PY PE PH PL PT QA RE RO RW BL SH KN LC MF PM VC " +
        "WS SM ST SA SN RS SC SL SG SK SI SB SO ZA KR SS ES LK SR SE " +
        "CH SD SJ TW TJ TZ TH TL TG TO TT TN TR TM TV UG UA AE GB US " +
        "UY UZ VU VN WF YE ZM ZW").Split(' '), StringComparer.OrdinalIgnoreCase);

    public static string? GetIpError(DetectionResult detection)
    {
        if (!detection.Success || detection.Location is null)
            return "IP 错误：无法确认 ChatGPT 当前出口地区，已停止启动。\r\n\r\n" + detection.Message;
        var location = detection.Location;
        if (!SupportedCountries.Contains(location.CountryCode?.Trim() ?? ""))
            return $"IP 错误：当前出口 {location.Ip}（{location.LocationText}）不在 ChatGPT 支持地区内，已停止启动。";
        // Trace and GeoIP databases can disagree. An unsupported trace country must not be hidden by GeoIP.
        if (location.TraceCountryCode is { Length: > 0 } trace && !SupportedCountries.Contains(trace.Trim()))
            return $"IP 错误：ChatGPT 出口 {location.Ip} 的地区为 {trace}，不在支持地区内，已停止启动。";
        return null;
    }

    // All UI modes use this gate before discovery, restart requests, or process activation.
    public static async Task<ProtectedLaunchResult> RunAsync(Func<Task<DetectionResult>> detect,
        Func<GeoLocation, Task<LaunchResult?>> launch)
    {
        var detection = await detect();
        var error = GetIpError(detection);
        if (error is not null) return new(detection.Location, null, error);
        return new(detection.Location, await launch(detection.Location!), null);
    }

    public static bool ShouldCloseLauncher(bool enabled, LaunchResult? launch) => enabled && launch is { Success: true };
    public static bool IsSupportedCountry(string? code) => SupportedCountries.Contains(code?.Trim() ?? "");
}
