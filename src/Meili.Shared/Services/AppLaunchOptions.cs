namespace Meili.Shared.Services;

/// <summary>
/// Process start flags. <c>--no-alerts</c> skips background monitoring and toasts (used for UI checks).
/// <c>--exit-on-close</c> makes the main window close quit the process instead of hiding to the tray.
/// </summary>
public sealed class AppLaunchOptions
{
    public bool NoAlerts { get; init; }
    public bool StartInBackground { get; init; }
    public bool ExitOnClose { get; init; }
    /// <summary>B3: run the built-in self-check, write a report and exit (implies --no-alerts and --exit-on-close).</summary>
    public bool Smoke { get; init; }
    /// <summary>B3: skip the network part of the self-check.</summary>
    public bool SmokeOffline { get; init; }
    /// <summary>B3: report path (default: data folder\logs\smoke.json).</summary>
    public string? SmokeReport { get; init; }
    /// <summary>C6: write a startup timing and memory timeline to logs\perf.json.</summary>
    public bool Perf { get; init; }

    public static AppLaunchOptions Parse(IReadOnlyList<string> args)
    {
        args ??= [];
        bool Has(string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        var smoke = Has("--smoke") || Has("--smoke-offline");
        string? report = null;
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith("--smoke-report=", StringComparison.OrdinalIgnoreCase)) report = args[i]["--smoke-report=".Length..].Trim('"');
            else if (args[i].Equals("--smoke-report", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count) report = args[i + 1];
        }
        return new AppLaunchOptions
        {
            StartInBackground = Has("--background"),
            NoAlerts = smoke || Has("--no-alerts") || Has("--no-notifications"),
            ExitOnClose = smoke || Has("--exit-on-close"),
            Smoke = smoke,
            SmokeOffline = Has("--smoke-offline"),
            SmokeReport = string.IsNullOrWhiteSpace(report) ? null : report,
            Perf = Has("--perf"),
        };
    }
}