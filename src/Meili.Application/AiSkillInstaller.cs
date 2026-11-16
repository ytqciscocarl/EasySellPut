using System.Reflection;
using System.Text;
using System.Security.Cryptography;
using Meili.Core.Settings;

namespace Meili.Automation;

public sealed class SkillInstallConflictException(string message) : IOException(message);

public static class AiSkillInstaller
{
    public const string SkillName = "easysellput-local";
    public const string SkillVersion = "2.0";
    public static string Content
    {
        get
        {
            using var stream = typeof(AiSkillInstaller).Assembly.GetManifestResourceStream("EasySellPut.Skill") ?? throw new InvalidOperationException("Bundled skill missing.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Replace("\r\n", "\n");
        }
    }

    public static string ClientFolder(string client) => client switch
    {
        "codex" => ".agents",
        "claude-code" => ".claude",
        _ => throw new ArgumentException("client must be codex or claude-code.")
    };

    // Only exact previously shipped content can be upgraded automatically; edited skills remain user-owned.
    private static readonly HashSet<string> PreviousVersions = ["D7AFE8DCE54D95C2FE04DB8246F3AA9C22C5685165C13A2C97E6538539C2F722", "EA52A6EAFD2150E510FBF79BB6C08B4D7864F4487B01AFFBD58676AC13C3A2B5", "78ED60852D730C26AE83D00F0BB3F2C1BA45419EDBC9F980F873ECB473798990"];
    private static string Destination(string client, string root, bool create)
    {
        var destination = Path.GetFullPath(root);
        foreach (var segment in new[] { ClientFolder(client), "skills", SkillName })
        {
            destination = Path.Combine(destination, segment);
            if (Directory.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                throw new SkillInstallConflictException("Skill destination contains a symbolic link; choose a regular project directory.");
            if (create) Directory.CreateDirectory(destination);
        }
        return Path.Combine(destination, "SKILL.md");
    }
    private static string Inspect(string target, string? content)
    {
        foreach (var path in new[] { target, target + ".lock" })
            if (new FileInfo(path).LinkTarget is not null || (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                throw new SkillInstallConflictException("Skill destination is a symbolic link; it was not modified.");
        if (content is null) return "install";
        var normalized = content.Replace("\r\n", "\n");
        if (normalized == Content) return "already_installed";
        if (PreviousVersions.Contains(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))))) return "update";
        throw new SkillInstallConflictException("Your easysellput-local skill has custom edits. It was preserved. Back it up and move it aside before installing the bundled version.");
    }
    public static object Check(string client, string root)
    {
        var target = Destination(client, root, false);
        _ = Inspect(target, null);
        return new { client, skill = SkillName, version = SkillVersion, path = target, status = Inspect(target, File.Exists(target) ? File.ReadAllText(target) : null) };
    }
    public static object Install(string client, string root)
    {
        var target = Destination(client, root, true);
        // SnapshotFile protects cooperating installers against stale overwrites and performs atomic replacement.
        _ = Inspect(target, null);
        var snapshot = new SnapshotFile(target); var existing = snapshot.Read(); var status = Inspect(target, existing);
        if (status == "already_installed") return new { client, skill = SkillName, version = SkillVersion, path = target, status };
        string? backup = null;
        if (existing is not null)
        {
            backup = target + ".backup-" + Guid.NewGuid().ToString("N");
            using var file = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var bytes = Encoding.UTF8.GetBytes(existing); file.Write(bytes);
        }
        snapshot.Write(Content);
        return new { client, skill = SkillName, version = SkillVersion, path = target, status = status == "update" ? "updated" : "installed", backup };
    }
}
