using Meili.Core.Settings;

namespace Meili.Automation;

public static class LocalPaths
{
    public static DataDirectoryInfo Resolve(string? explicitPath = null)
    {
        var exe = AppContext.BaseDirectory;
        var args = explicitPath is null ? Array.Empty<string>() : new[] { "--data-dir", Path.GetFullPath(explicitPath) };
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (OperatingSystem.IsMacOS())
        {
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
            // Also supports a CLI shipped inside a Mac .app bundle.
            var dir = new DirectoryInfo(Path.TrimEndingDirectorySeparator(exe));
            if (dir is { Name: "MacOS", Parent: { Name: "Contents", Parent: { } app } }) exe = app.Parent!.FullName;
        }
        return DataDirectoryResolver.Resolve(args, exe, baseDir);
    }
}
