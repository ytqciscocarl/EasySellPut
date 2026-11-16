using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meili.Automation;

namespace Meili.Automation.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "update-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string TestRepository = "tests/EasySellPut";
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Requests.Add(request.RequestUri!.AbsoluteUri); return Task.FromResult(response(request)); }
    }
    private (UpdateClient Client, Handler Handler, byte[] Package) Fixture(bool corruptSignature = false, bool corruptHash = false, string manifestRepository = TestRepository)
    {
        var package = Encoding.UTF8.GetBytes("verified test package, never executable");
        var name = "EasySellPut-Desktop-0.7.0-win-x64.zip";
        var asset = new UpdateAsset("win-x64", name, corruptHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(package)), package.Length);
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new UpdateManifest(1, "0.7.0", [asset], manifestRepository), Json);
        var signature = _key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        if (corruptSignature) manifest[^2] ^= 1;
        var names = new[] { name, UpdateClient.ManifestName, UpdateClient.ManifestName + ".sig" };
        var release = JsonSerializer.SerializeToUtf8Bytes(new { draft = false, prerelease = false, tag_name = "v0.7.0", body = "Changes <script>literal</script>", assets = names.Select(n => new { name = n, browser_download_url = $"https://github.com/{TestRepository}/releases/download/v0.7.0/{n}" }) });
        var handler = new Handler(request =>
        {
            var bytes = request.RequestUri!.Host == "api.github.com" ? release : request.RequestUri.AbsolutePath.EndsWith(".sig") ? signature : request.RequestUri.AbsolutePath.EndsWith(".json") ? manifest : package;
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        return (new UpdateClient(new HttpClient(handler), "win-x64", new Version(0, 6, 0)) { SourceRepository = TestRepository, VerificationKey = _key.ExportSubjectPublicKeyInfoPem() }, handler, package);
    }
    [Fact]
    public async Task Signed_release_selects_architecture_and_verifies_streamed_download()
    {
        var (client, handler, package) = Fixture();
        var check = await client.CheckAsync(default);
        Assert.Equal("available", check.Status); Assert.Equal("0.7.0", check.LatestVersion);
        Assert.Contains("<script>literal</script>", check.Release!.Notes);
        var archive = await client.DownloadAsync(check.Release, _folder, null, default);
        Assert.Equal(package, File.ReadAllBytes(archive)); Assert.DoesNotContain(Directory.GetFiles(_folder), f => f.Contains("partial"));
        Assert.All(handler.Requests, u => Assert.StartsWith("https://", u));
    }
    [Fact]
    public async Task Changed_manifest_is_rejected_before_download()
    {
        var (client, handler, _) = Fixture(corruptSignature: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.CheckAsync(default));
        Assert.DoesNotContain(handler.Requests, u => u.EndsWith(".zip"));
    }
    [Fact]
    public async Task Failed_hash_preserves_existing_cache_and_removes_partial_files()
    {
        var (client, _, _) = Fixture(corruptHash: true); var check = await client.CheckAsync(default);
        Directory.CreateDirectory(_folder); var old = Path.Combine(_folder, check.Release!.Asset.FileName); File.WriteAllText(old, "previous verified file");
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(check.Release, _folder, null, default));
        Assert.Equal("previous verified file", File.ReadAllText(old)); Assert.Single(Directory.GetFiles(_folder));
    }
    [Fact]
    public async Task Missing_release_is_distinct_from_network_failure_and_redirects_are_restricted()
    {
        var empty = new UpdateClient(new HttpClient(new Handler(_ => new(HttpStatusCode.NotFound))), "osx-arm64", new(0, 6, 0)) { SourceRepository = TestRepository };
        Assert.Equal("no_release", (await empty.CheckAsync(default)).Status);
        var redirect = new UpdateClient(new HttpClient(new Handler(_ => { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new("http://127.0.0.1/not-an-update"); return response; })), "win-x64", new(0, 6, 0)) { SourceRepository = TestRepository };
        await Assert.ThrowsAsync<InvalidDataException>(() => redirect.CheckAsync(default));
        var failed = new UpdateClient(new HttpClient(new Handler(_ => new(HttpStatusCode.ServiceUnavailable))), "win-x64", new(0, 6, 0)) { SourceRepository = TestRepository };
        await Assert.ThrowsAsync<HttpRequestException>(() => failed.CheckAsync(default));
    }
    [Fact]
    public void Unrelated_key_cannot_authenticate_a_release()
    {
        using var pinned = ECDsa.Create(); pinned.ImportFromPem(UpdateClient.PublicKey); Assert.Equal(256, pinned.KeySize);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256); var bytes = Encoding.UTF8.GetBytes("manifest");
        var signature = other.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.Throws<InvalidDataException>(() => UpdateClient.VerifyManifest(bytes, signature, _key.ExportSubjectPublicKeyInfoPem()));
    }
    [Theory]
    [InlineData("win-x64")]
    [InlineData("linux-x64")]
    public async Task Unconfigured_build_never_contacts_a_release_server(string rid)
    {
        var handler = new Handler(_ => throw new InvalidOperationException("Network must not be used"));
        var client = new UpdateClient(new HttpClient(handler), rid, new(0, 6, 0)) { SourceRepository = "" };
        Assert.Equal("disabled", (await client.CheckAsync(default)).Status); Assert.Empty(handler.Requests);
    }
    [Theory]
    [InlineData("https://github.com/owner/repo")]
    [InlineData("owner/../other")]
    [InlineData("owner/..")]
    [InlineData("owner/repo?x=1")]
    public async Task Invalid_repository_is_rejected_before_any_request(string repository)
    {
        var handler = new Handler(_ => throw new InvalidOperationException("Network must not be used"));
        var client = new UpdateClient(new HttpClient(handler), "win-x64", new(0, 6, 0)) { SourceRepository = repository };
        await Assert.ThrowsAsync<InvalidDataException>(() => client.CheckAsync(default)); Assert.Empty(handler.Requests);
    }
    [Fact]
    public async Task Signed_manifest_for_another_repository_is_rejected()
    {
        var (client, handler, _) = Fixture(manifestRepository: "other/EasySellPut");
        await Assert.ThrowsAsync<InvalidDataException>(() => client.CheckAsync(default));
        Assert.DoesNotContain(handler.Requests, u => u.EndsWith(".zip"));
    }
    [Fact]
    public void Client_and_installer_share_the_same_signed_repository_guard()
    {
        var manifest = new UpdateManifest(1, "0.7.0", [], TestRepository);
        UpdateClient.VerifyRepository(manifest, TestRepository);
        Assert.Throws<InvalidDataException>(() => UpdateClient.VerifyRepository(manifest, ""));
        Assert.Throws<InvalidDataException>(() => UpdateClient.VerifyRepository(manifest, "other/EasySellPut"));
        Assert.Throws<InvalidDataException>(() => UpdateClient.VerifyRepository(manifest with { Repository = null }, TestRepository));
    }
    private string Zip(string entry, int mode = 0)
    {
        Directory.CreateDirectory(_folder); var path = Path.Combine(_folder, Guid.NewGuid() + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create); var item = zip.CreateEntry(entry); item.ExternalAttributes = mode << 16;
        using var stream = new StreamWriter(item.Open()); stream.Write("test"); return path;
    }
    [Theory]
    [InlineData("package/../escape")]
    [InlineData("/absolute")]
    [InlineData("package/dir\\escape")]
    [InlineData("package/file:stream")]
    [InlineData("package/CON.txt")]
    [InlineData("other/file")]
    public async Task Extraction_rejects_unsafe_paths(string entry)
    {
        var archive = Zip(entry); await Assert.ThrowsAsync<InvalidDataException>(() => UpdateInstaller.ExtractAsync(archive, Path.Combine(_folder, "out"), "package", default));
    }
    [Fact]
    public async Task Extraction_rejects_links_and_restores_executable_permissions()
    {
        var linked = Zip("package/link", 0xA1FF);
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateInstaller.ExtractAsync(linked, Path.Combine(_folder, "link-out"), "package", default));
        var normal = Zip("package/program", 0x81ED); var output = Path.Combine(_folder, "normal-out");
        await UpdateInstaller.ExtractAsync(normal, output, "package", default);
        Assert.Equal("test", File.ReadAllText(Path.Combine(output, "package/program")));
        if (!OperatingSystem.IsWindows()) Assert.True((File.GetUnixFileMode(Path.Combine(output, "package/program")) & UnixFileMode.UserExecute) != 0);
    }
    [Fact]
    public void Replacement_preserves_portable_data_and_retains_old_application_backup()
    {
        var target = Path.Combine(_folder, "old"); var candidate = Path.Combine(_folder, "new"); var backup = Path.Combine(_folder, "backup");
        Directory.CreateDirectory(Path.Combine(target, "data")); Directory.CreateDirectory(candidate);
        File.WriteAllText(Path.Combine(target, "app"), "old"); File.WriteAllText(Path.Combine(candidate, "app"), "new"); File.WriteAllText(Path.Combine(target, "data/settings.json"), "user data");
        UpdateInstaller.Swap(target, candidate, backup, true);
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "app"))); Assert.Equal("old", File.ReadAllText(Path.Combine(backup, "app")));
        Assert.Equal("user data", File.ReadAllText(Path.Combine(target, "data/settings.json")));
    }
    [Fact]
    public void Failed_replacement_rolls_back_portable_data_without_overwriting_existing_backup()
    {
        var target = Path.Combine(_folder, "old"); var candidate = Path.Combine(_folder, "new"); var backup = Path.Combine(_folder, "backup");
        Directory.CreateDirectory(Path.Combine(target, "data")); Directory.CreateDirectory(candidate); Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "marker"), "keep"); File.WriteAllText(Path.Combine(target, "data/settings.json"), "user data");
        Assert.ThrowsAny<IOException>(() => UpdateInstaller.Swap(target, candidate, backup, true));
        Assert.Equal("user data", File.ReadAllText(Path.Combine(target, "data/settings.json"))); Assert.Equal("keep", File.ReadAllText(Path.Combine(backup, "marker")));
        Assert.False(Directory.Exists(Path.Combine(candidate, "data")));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Persisted_cancel_blocks_installation_even_if_parent_has_already_exited(bool parentAlive)
    {
        Directory.CreateDirectory(_folder);var ready=Path.Combine(_folder,"ready");File.WriteAllText(ready+".cancel","");
        using var parent=System.Diagnostics.Process.GetCurrentProcess();
        var plan=new UpdateInstallPlan("unused","unused","unused","unused","unused","unused",parentAlive?parent.Id:int.MaxValue,parent.StartTime.ToUniversalTime().Ticks,ready);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>UpdateInstaller.WaitForParentExitAsync(plan,default));
        Assert.False(parent.HasExited);
    }
    [Fact]
    public void Failed_candidate_move_restores_the_original_app_directory()
    {
        var target=Path.Combine(_folder,"old");var candidate=Path.Combine(_folder,"missing");var backup=Path.Combine(_folder,"backup");
        Directory.CreateDirectory(target);File.WriteAllText(Path.Combine(target,"app"),"old");
        Assert.ThrowsAny<IOException>(()=>UpdateInstaller.Swap(target,candidate,backup,false));
        Assert.Equal("old",File.ReadAllText(Path.Combine(target,"app")));Assert.False(Directory.Exists(backup));
    }
    public void Dispose() { _key.Dispose(); if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
}
