using System.IO;
using Meili.Automation;
using Meili.Shared.Services;

namespace Meili.Shared.Tests;

public sealed class FakeUpdates : IUpdateCoordinator
{
    public bool IsConfigured { get; set; } = true;
    public bool CanInstall { get; set; } = true;
    public string Status { get; set; } = "available";
    public Exception? CheckError { get; set; }
    public Exception? InstallError { get; set; }
    public bool WaitForCancel { get; set; }
    public string ReleaseVersion { get; set; } = "0.7.0";
    public bool WaitForCheckCancel { get; set; }
    public int Downloads { get; private set; }
    public int Checks { get; private set; }
    public int Installs { get; private set; }
    public async Task<UpdateCheck> CheckAsync(CancellationToken ct)
    {
        Checks++;
        if (WaitForCheckCancel) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        if (CheckError is not null) throw CheckError;
        var asset = new UpdateAsset("osx-arm64", "fixture.zip", new string('0', 64), 100);
        return new UpdateCheck(Status, "0.6.0", ReleaseVersion, Status == "available" ? new(new(1, ReleaseVersion, [asset]), asset, "Fixture release / 测试更新\n• 新闻历史导航\n• 盘前与盘后背景\n• CLI / MCP 同步更新", "https://github.com/two4i6/EasySellPut/releases/tag/v0.7.0", "fixture", "fixture", "fixture") : null);
    }
    public async Task<string> DownloadAsync(UpdateRelease release, IProgress<double> progress, CancellationToken ct)
    {
        Downloads++;
        progress.Report(60);
        if (WaitForCancel) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return Path.Combine(Path.GetTempPath(), "fixture-update", "package.zip");
    }
    public Task PrepareInstallAsync(UpdateRelease release, string archive, CancellationToken ct)
    {
        Installs++; if (InstallError is not null) throw InstallError; return Task.CompletedTask;
    }
}
