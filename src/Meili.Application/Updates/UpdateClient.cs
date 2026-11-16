using System.Reflection;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Meili.Core.Settings;
using Meili.Data.Http;

namespace Meili.Automation;

public sealed record UpdateAsset(string Rid, string FileName, string Sha256, long Size);
public sealed record UpdateManifest(int SchemaVersion, string Version, UpdateAsset[] Assets, string? Repository = null);
public sealed record UpdateRelease(UpdateManifest Manifest, UpdateAsset Asset, string Notes, string Page, string Download, string SignedManifest, string Signature);
public sealed record UpdateCheck(string Status, string CurrentVersion, string? LatestVersion = null, UpdateRelease? Release = null);

/// <summary>Stable releases from one repository, authenticated by a pinned ECDSA key before package selection.</summary>
public sealed class UpdateClient(HttpClient http, string rid, Version current)
{
    internal string VerificationKey { get; init; } = PublicKey;
    public static string Repository { get; } = typeof(UpdateClient).Assembly.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
        .SingleOrDefault(a => a.Key == "EasySellPut.UpdateRepository")?.Value ?? "";
    internal string SourceRepository { get; init; } = Repository;
    public static bool IsConfigured => IsValidRepository(Repository);
    public static string? ReleaseHistoryUrl => IsConfigured ? $"https://github.com/{Repository}/releases" : null;
    public static bool IsValidRepository(string value) => System.Text.RegularExpressions.Regex.IsMatch(value,
        @"\A[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}\z") && value.Split('/')[1] is not ("." or "..");
    public static string PublicKeyFingerprint
    {
        get { using var key = ECDsa.Create(); key.ImportFromPem(PublicKey); return Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant(); }
    }
    internal static void VerifyRepository(UpdateManifest manifest, string repository)
    {
        if (!IsValidRepository(repository) || manifest.Repository != repository) throw new InvalidDataException("Update repository does not match this installation.");
    }
    public static object SourceConfiguration => new { enabled = IsConfigured, repository = Repository, publicKeySha256 = PublicKeyFingerprint, historyUrl = ReleaseHistoryUrl };
    public const string ManifestName = "EasySellPut-update.json";
    public const long MaxPackageBytes = 768L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string RuntimeId => (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    public static Version CurrentVersion => typeof(UpdateClient).Assembly.GetName().Version!;
    public static string PublicKey
    {
        get
        {
            using var stream = typeof(UpdateClient).Assembly.GetManifestResourceStream("EasySellPut.UpdatePublicKey")!;
            using var reader = new StreamReader(stream); return reader.ReadToEnd();
        }
    }
    public static async Task<HttpClient> CreateHttpAsync(NetworkSettings settings, CancellationToken ct)
    {
        var proxy = await ProxyDetector.ResolveAsync(settings, ct);
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10), UseProxy = proxy.UseSystemProxy || proxy.ProxyUri is not null };
        if (proxy.ProxyUri is not null) handler.Proxy = new WebProxy(proxy.ProxyUri);
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public static void VerifyManifest(byte[] bytes, byte[] signature, string publicKey)
    {
        using var key = ECDsa.Create(); key.ImportFromPem(publicKey);
        if (!key.VerifyData(bytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            throw new InvalidDataException("Update manifest signature is invalid.");
    }
    public async Task<UpdateCheck> CheckAsync(CancellationToken ct)
    {
        var version = new Version(current.Major, current.Minor, Math.Max(0, current.Build)).ToString();
        if (string.IsNullOrEmpty(SourceRepository)) return new("disabled", version);
        if (!IsValidRepository(SourceRepository)) throw new InvalidDataException("Invalid compiled update repository.");
        if (rid is not ("win-x64" or "win-arm64" or "osx-x64" or "osx-arm64")) throw new PlatformNotSupportedException("No desktop updates for this platform.");
        using var response = await GetAsync(new Uri($"https://api.github.com/repos/{SourceRepository}/releases/latest"), ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return new("no_release", version);
        response.EnsureSuccessStatusCode();
        using var release = JsonDocument.Parse(await ReadBoundedAsync(response.Content, 2 * 1024 * 1024, ct));
        var root = release.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) throw new InvalidDataException("Expected a stable release.");
        var tag = root.GetProperty("tag_name").GetString()!;
        if (!tag.StartsWith('v') || !Version.TryParse(tag[1..], out var latest) || latest.Build < 0 || latest.ToString(3) != tag[1..]) throw new InvalidDataException("Unsupported release version.");
        if (latest <= new Version(version)) return new("current", version, tag[1..]);
        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        string Url(string name)
        {
            var asset = assets.SingleOrDefault(a => a.GetProperty("name").GetString() == name);
            if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("The release does not contain a signed desktop update.");
            var url = asset.GetProperty("browser_download_url").GetString()!;
            if (url != $"https://github.com/{SourceRepository}/releases/download/{tag}/{name}") throw new InvalidDataException("Unexpected release asset URL.");
            return url;
        }
        async Task<byte[]> ReadAsset(string name, int limit)
        {
            using var message = await GetAsync(new Uri(Url(name)), ct); message.EnsureSuccessStatusCode();
            return await ReadBoundedAsync(message.Content, limit, ct);
        }
        var manifestBytes = await ReadAsset(ManifestName, 65536);
        var signature = await ReadAsset(ManifestName + ".sig", 1024);
        VerifyManifest(manifestBytes, signature, VerificationKey);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes, Json) ?? throw new InvalidDataException("Empty manifest.");
        VerifyRepository(manifest, SourceRepository);
        if (manifest.SchemaVersion != 1 || manifest.Version != tag[1..] || manifest.Assets is null) throw new InvalidDataException("Manifest version mismatch.");
        var package = manifest.Assets.SingleOrDefault(a => a.Rid == rid) ?? throw new InvalidDataException("This architecture is missing from the release.");
        if (package.FileName != $"EasySellPut-Desktop-{manifest.Version}-{rid}.zip" || package.Size is <= 0 or > MaxPackageBytes
            || package.Sha256 is null || !System.Text.RegularExpressions.Regex.IsMatch(package.Sha256, "^[0-9a-fA-F]{64}$")) throw new InvalidDataException("Invalid package metadata.");
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new("available", version, manifest.Version, new(manifest, package, notes[..Math.Min(notes.Length, 20000)], $"https://github.com/{SourceRepository}/releases/tag/{tag}", Url(package.FileName), Convert.ToBase64String(manifestBytes), Convert.ToBase64String(signature)));
    }
    public async Task<string> DownloadAsync(UpdateRelease release, string folder, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, release.Asset.FileName);
        var temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            using var response = await GetAsync(new Uri(release.Download), ct).ConfigureAwait(false); response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != release.Asset.Size) throw new InvalidDataException("Package size mismatch.");
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536]; long total = 0; var lastProgress = Environment.TickCount64; int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0)
                {
                    total += count; if (total > release.Asset.Size) throw new InvalidDataException("Package exceeds signed size.");
                    digest.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), ct); if (total == release.Asset.Size || Environment.TickCount64 - lastProgress >= 100) { progress?.Report(total * 100.0 / release.Asset.Size); lastProgress = Environment.TickCount64; }
                }
                if (total != release.Asset.Size || !Convert.ToHexString(digest.GetHashAndReset()).Equals(release.Asset.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Package checksum mismatch.");
            }
            File.Move(temporary, destination, true); return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken ct)
    {
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            if (uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort || uri.Host is not ("api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com")) throw new InvalidDataException("Unexpected update host.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.UserAgent.ParseAdd("EasySellPut-Updater/1.0");
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location; response.Dispose();
                if (location is null) throw new InvalidDataException("Missing redirect target."); uri = new Uri(uri, location); continue;
            }
            return response;
        }
        throw new HttpRequestException("Too many update redirects.");
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        await using var input = await content.ReadAsStreamAsync(ct); using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0) { if (output.Length + count > limit) throw new InvalidDataException("Update metadata is too large."); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
}
