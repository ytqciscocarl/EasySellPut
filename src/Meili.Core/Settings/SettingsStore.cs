using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meili.Core.Settings;

/// <summary>Loads/saves <see cref="AppSettings"/> as JSON with atomic writes and corrupt-file recovery.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _gate = new();
    private readonly SnapshotFile _snapshot;
    public string FilePath { get; }
    public AppSettings Current { get; private set; } = new();

    public event EventHandler<AppSettings>? Saved;

    public SettingsStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        FilePath = Path.Combine(dataDirectory, "settings.json");
        _snapshot = new SnapshotFile(FilePath);
    }

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var text = _snapshot.Read()!;
                    Current = (JsonSerializer.Deserialize<AppSettings>(text, Json) ?? new AppSettings()).Normalize();
                    return Current;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
            {
                // Keep the broken file for inspection and start from defaults.
                Diagnostics.AppLog.Error("settings", $"{FilePath} is unreadable, starting from defaults (copy kept as .corrupt-*)", ex);
                try { File.Copy(FilePath, FilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); }
                catch (Exception copyEx) { Diagnostics.AppLog.Warn("settings", "could not keep a copy of the corrupt file", copyEx); }
            }
            _snapshot.Read();
            Current = new AppSettings().Normalize();
            return Current;
        }
    }

    public void Save(AppSettings? settings = null)
    {
        lock (_gate)
        {
            Current = (settings ?? Current).Normalize();
            _snapshot.Write(JsonSerializer.Serialize(Current, Json));
        }
        Saved?.Invoke(this, Current);
    }

    /// <summary>Mutate and persist in one step.</summary>
    public void Update(Action<AppSettings> change)
    {
        lock (_gate) change(Current);
        Save();
    }
}
