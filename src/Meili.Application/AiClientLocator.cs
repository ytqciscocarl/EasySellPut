namespace Meili.Automation;

public sealed class AiSetupException(string code, string message, string recovery) : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public string Recovery { get; } = recovery;
}

public static class AiClientLocator
{
    public static string? Find(string client)
    {
        _ = AiSkillInstaller.ClientFolder(client);
        var name = client == "codex" ? "codex" : "claude";
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(OperatingSystem.IsWindows() ? new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"), Path.Combine(profile, ".local", "bin") }
                : new[] { Path.Combine(profile, ".local", "bin"), "/opt/homebrew/bin", "/usr/local/bin", Path.Combine(profile, ".claude", "local") });
        foreach (var directory in paths.Distinct())
            foreach (var suffix in OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", ".ps1" } : new[] { "" })
            {
                var path = Path.Combine(directory.Trim('"'), name + suffix);
                if (File.Exists(path)) return Path.GetFullPath(path);
            }
        return null;
    }
}
