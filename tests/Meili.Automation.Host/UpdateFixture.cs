using System.Security.Cryptography;
using Meili.Automation;

internal static class UpdateFixture
{
    // Test-only entry point: exercises the production extractor and transaction on native package copies.
    public static async Task RunAsync(string archive, string target, string data, string stage)
    {
        var name = Path.GetFileNameWithoutExtension(archive);
        var rid = name.EndsWith("arm64") ? (OperatingSystem.IsWindows() ? "win-arm64" : "osx-arm64") : (OperatingSystem.IsWindows() ? "win-x64" : "osx-x64");
        var bytes = await File.ReadAllBytesAsync(archive);
        await UpdateInstaller.VerifyArchiveAsync(archive, new(rid, Path.GetFileName(archive), Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length), default);
        await UpdateInstaller.ExtractAsync(archive, stage, name, default);
        var candidate = Path.Combine(stage, name);
        if (!OperatingSystem.IsWindows()) candidate = Path.Combine(candidate, "EasySellPut.app");
        UpdateInstaller.ValidateTarget(candidate, OperatingSystem.IsWindows());
        Directory.CreateDirectory(data); var marker = Path.Combine(data, "updater-user-data.marker"); File.WriteAllText(marker, "retain exact user data");
        UpdateInstaller.Swap(target, candidate, target + ".backup", OperatingSystem.IsWindows());
        if (File.ReadAllText(marker) != "retain exact user data" || !Directory.Exists(target + ".backup")) throw new InvalidOperationException("Update lost user data or backup");
        Console.WriteLine("PASS: production update extraction and swap retained user data and previous app backup");
    }
}
