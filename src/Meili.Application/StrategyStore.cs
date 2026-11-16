using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meili.Analytics;
using Meili.Core.Diagnostics;
using Meili.Core.Settings;

namespace Meili.Shared.Services;

/// <summary>B7: one leg as stored in <c>strategies.json</c> (IV is null when unset).</summary>
public sealed record SavedLeg(LegType Type, PositionSide Side, int Quantity, double Strike, double Premium, DateOnly? Expiry, double? Iv, int Multiplier)
{
    public static SavedLeg From(StrategyLeg l) =>
        new(l.Type, l.Side, l.Quantity, l.Strike, l.Premium, l.Expiry, l.HasImpliedVolatility ? l.ImpliedVolatility : null, l.Multiplier);

    public StrategyLeg ToLeg() => new(Type, Side, Quantity, Strike, Premium, Expiry, Iv ?? double.NaN, Multiplier);
}

/// <summary>B7: a named strategy the user saved from the strategy page.</summary>
public sealed record SavedStrategy(string Name, string Symbol, StrategyKind Kind, IReadOnlyList<SavedLeg> Legs, DateTimeOffset SavedAt)
{
    public override string ToString() => Name;
}

/// <summary>B7: saved strategies in <c>strategies.json</c>, newest first; saving under an existing name replaces it (UI thread only).</summary>
public sealed class StrategyStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SnapshotFile _snapshot;
    private List<SavedStrategy> _items = [];
    public string FilePath { get; }
    public IReadOnlyList<SavedStrategy> All => _items;

    public StrategyStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        FilePath = Path.Combine(dataDirectory, "strategies.json");
        _snapshot = new SnapshotFile(FilePath);
        if (!File.Exists(FilePath)) return;
        try
        {
            _items = JsonSerializer.Deserialize<List<SavedStrategy>>(_snapshot.Read()!, Json) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            AppLog.Error("strategies", $"{FilePath} is unreadable, starting without saved strategies", ex);
        }
    }

    public void Save(SavedStrategy s)
    {
        _items.RemoveAll(x => x.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase));
        _items.Insert(0, s);
        Write();
    }

    public void Delete(string name)
    {
        if (_items.RemoveAll(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0) Write();
    }

    private void Write()
    {
        _snapshot.Write(JsonSerializer.Serialize(_items, Json));
    }
}
