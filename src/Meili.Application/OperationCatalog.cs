using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meili.Automation;

public sealed record Operation(string Name, string Description, bool Writes, JsonElement Schema);

/// <summary>Shared discovery/validation contract: CLI help and MCP tools cannot silently diverge.</summary>
public static partial class OperationCatalog
{
    private static JsonObject Field(string type, string description, params string[] values)
    {
        var f = new JsonObject { ["type"] = type, ["description"] = description };
        if (values.Length > 0) f["enum"] = new JsonArray(values.Select(v => JsonValue.Create(v)).ToArray());
        if (type == "string") f["maxLength"] = 200;
        if (type == "array") f["items"] = new JsonObject { ["type"] = "string" };
        return f;
    }
    private static Operation Op(string name, string description, bool writes = false, params (string Key, JsonObject Schema)[] fields)
    {
        var props = new JsonObject(); var required = new JsonArray();
        foreach (var (key, schema) in fields)
        {
            var k = key.TrimEnd('!'); var copy = (JsonObject)schema.DeepClone();
            if (k == "offset") copy["minimum"] = 0;
            if (k == "limit") { copy["minimum"] = 1; copy["maximum"] = name == "search_symbols" || name == "get_news" ? 50 : name == "get_market_events" ? 100 : 500; }
            if (k == "symbols") { copy["minItems"] = 1; copy["maxItems"] = 100; }
            if(string.IsNullOrWhiteSpace(copy["description"]?.GetValue<string>()))copy["description"]=k switch
            {
                "symbol"=>"US ticker, e.g. NVDA or BRK.B. Resolve ambiguous company names with search_symbols.",
                "query"=>"Ticker or company-name search text; use returned ticker symbols in subsequent tools.",
                "expiry"=>"Listed option expiration YYYY-MM-DD; discover actual dates with get_expirations.",
                "spot"=>"Underlying price per share. Use an observed quote or explicitly label a hypothetical scenario.",
                "strike"=>"Option strike per share in the underlying quote currency.",
                "minStrike" or "maxStrike"=>"Inclusive strike filter in underlying quote currency; not an option premium.",
                "right"=>"Option right; use the exact call/put enum spelling in this schema.",
                "legs"=>"Explicit position legs. Preserve entry premiums, side, expiry, quantity and multiplier; never fabricate quotes.",
                "rate"=>"Annual continuously compounded rate as a decimal fraction, e.g. 0.04 = 4%; obtain pricing assumptions before valuation.",
                "dividendYield"=>"Annual dividend yield as a decimal fraction, e.g. 0.02 = 2%; preserve provenance and unavailable-yield fallback.",
                "volatility"=>"Annualized IV as a decimal fraction, e.g. 0.25 = 25%; state observed versus assumed/model-derived origin.",
                "volatilityShift"=>"Additive decimal IV shift: 0.05 means five volatility points, not 5 percent relative change.",
                "horizonDays"=>"Calendar days forward from the explicit valuation time; see schema bounds.",
                "quantity"=>"Number of option contracts for a preset. Explicit stock legs use shares and multiplier 1.",
                "wingSteps"=>"Preset wing width in listed strike steps, not a raw dollar distance.",
                "groupId"=>"Existing local watchlist group id returned by get_watchlists; never substitute its display name.",
                "id"=>"Existing alert rule id from get_alerts; omit only when save_alert creates a new rule.",
                "name"=>name.Contains("strategy",StringComparison.Ordinal)?"Local strategy name; saving an existing name replaces it. Read saved strategies first.":"Local watchlist group display name.",
                "enabled"=>"Whether this local alert rule is enabled. Desktop owns continuous monitoring.",
                "note"=>"Optional user note for this alert; treated as literal text.",
                "threshold"=>"Alert trigger threshold in the selected kind's units; inspect the kind and existing rule before updating.",
                "includeExtendedHours"=>"Whether the desktop rule may evaluate valid pre/after-market quotes.",
                "low" or "high"=>"Underlying-price bound for the payoff/scenario plot, not an option premium.",
                "price1" or "price2"=>"Drawing anchor price in the underlying quote currency; not a screen coordinate.",
                "indicators"=>"Optional shared indicator parameter overrides; observe nested bounds and null warm-up values.",
                "compareSymbol"=>"Optional comparison ticker. Rebase only on shared timestamps and retain missing-basis uncertainty.",
                "kind"=>name.Contains("alert",StringComparison.Ordinal)?"Alert trigger category; select the exact enum value in the schema.":"Strategy/drawing category; select the exact schema enum and required matching fields.",
                _=>$"{k} argument for {name}; observe this field's schema type, bounds and operation semantics."
            };
            props[k] = copy; if (key.EndsWith('!')) required.Add(k);
        }
        return new(name, description, writes, JsonSerializer.SerializeToElement(new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = required, ["additionalProperties"] = false }));
    }
    private static JsonObject S(string d = "") => Field("string", d);
    private static JsonObject N(string d = "") => Field("number", d);
    private static JsonObject I(string d = "") => Field("integer", d);
    private static JsonObject B(string d = "") => Field("boolean", d);
    private static JsonObject O(string d = "") => Field("object", d);
    private static readonly (string, JsonObject)[] Page = [("offset", I("Zero-based offset, default 0.")), ("limit", I("Page size 1..500, default 100."))];
    public static IReadOnlyList<Operation> All { get; } = Build();
    private static Operation[] Build() =>
    [
        Op("search_symbols", "Search symbols across configured providers.", false, ("query!", S()), ("limit", I("1..50, default 10"))),
        Op("get_quotes", "Batch quotes with source, market time, download time and declared delay. Missing symbols are reported.", false, ("symbols!", Field("array", "1..100 ticker symbols"))),
        Op("get_bars", "Historical OHLCV with provenance, adjustment and offline-cache status. Missing OHLC is null, never fabricated.", false, [("symbol!", S()), ("interval", Field("string", "default 1d", "1m","5m","15m","30m","1h","1d","1w","1mo")), ("from", S("ISO-8601 instant")), ("to", S("ISO-8601 instant")), ("adjust", Field("string", "default split", "split","total")), ..Page]),
        Op("get_expirations", "List option expiration dates.", false, ("symbol!", S())),
        Op("get_option_chain", "Paginated option contracts with source metadata and non-executable midpoint/last entry references (missing quotes stay null). Request expiry YYYY-MM-DD and optional right/strike filters.", false, [("symbol!", S()), ("expiry", S()), ("right", Field("string", "", "call","put")), ("minStrike", N()), ("maxStrike", N()), ..Page]),
        Op("get_fundamentals", "Company fundamentals with source and freshness metadata.", false, ("symbol!", S())),
        Op("get_financials", "Financial statements with source and freshness metadata.", false, ("symbol!", S())),
        Op("get_news", "Recent provider news, retaining publisher, URL and publication time.", false, ("symbol!", S()), ("limit", I("1..50, default 20"))),
        Op("get_market_events", "Recent news and current earnings/ex-dividend calendar snapshot, with source, exact/date-only timing and estimate/confirmation metadata. Not a historical corporate-action archive.", false,
            ("symbol!", S()), ("type", Field("string", "default all", "all", "news", "earnings", "ex_dividend")), ("from", S("YYYY-MM-DD, inclusive Eastern calendar date")), ("to", S("YYYY-MM-DD, inclusive Eastern calendar date")), ("limit", I("1..100, default 50"))),
        Op("get_source_health", "Source health observed in THIS process only; unknown is not a successful connectivity check."),
        Op("calculate_option", "European Black-Scholes price/Greeks and optional solved IV; decimal units (0.25=25%). No network.", false,
            ("right!", Field("string", "", "call","put")), ("spot!", N()), ("strike!", N()), ("years!", N()), ("rate", N("Continuously compounded, default 0")), ("dividendYield", N("default 0")), ("volatility!", N()), ("price", N("Optional observed per-share price for IV solving"))),
        Op("calculate_indicators", "SMA, EMA and RSI on supplied close prices. Warm-up values are null. No network.", false, ("closes!", new JsonObject { ["type"]="array", ["items"]=new JsonObject { ["type"]="number" } }), ("period", I("1..1000, default 14"))),
        Op("analyze_strategy", "Position expiry P/L, boundedness, breakevens, optional pre-expiry Greeks and Reg-T estimate. Mixed expiries require valuationTime and IV. No network.", false,
            ("legs!", Legs()), ("spot!", N()), ("valuationTime", S("ISO-8601 instant")), ("rate", N()), ("dividendYield", N()), ("volatility", N("Fallback decimal IV")), ("low", N()), ("high", N()), ("points", I("2..1001, default 101"))),
        Op("get_watchlists", "Read grouped watchlists from the local desktop data directory."),
        Op("create_watchlist_group", "Create a local watchlist group.", true, ("name!", S())),
        Op("rename_watchlist_group", "Rename a local watchlist group.", true, ("groupId!", S()), ("name!", S())),
        Op("delete_watchlist_group", "Remove a group; its symbols move to the first remaining group. The last group cannot be deleted.", true, ("groupId!", S())),
        Op("add_watchlist_symbol", "Add a ticker to a group.", true, ("groupId!", S()), ("symbol!", S())),
        Op("remove_watchlist_symbol", "Remove a ticker from a group.", true, ("groupId!", S()), ("symbol!", S())),
        Op("get_saved_strategies", "Read saved local strategies."),
        Op("save_strategy", "Save/replace a local strategy by name, using desktop-compatible JSON.", true, ("name!", S()), ("symbol!", S()), ("legs!", Legs()), ("kind", Field("string", "default Custom", Enum.GetNames<Meili.Analytics.StrategyKind>()))),
        Op("delete_strategy", "Delete a saved strategy by name.", true, ("name!", S())),
        Op("get_alerts", "Read local alert rules. Desktop remains responsible for continuous monitoring."),
        Op("get_alert_history", "Read paginated local alert history.", false, Page),
        Op("save_alert", "Create a rule, or update an existing id while retaining monitor bookkeeping. Does not start monitoring.", true, ("symbol!", S()), ("kind!", Field("string", "", "PriceAbove","PriceBelow","PercentUp","PercentDown","VolumeAbove","IvAbove","IvBelow")), ("threshold!", N()), ("id", S()), ("enabled", B()), ("repeat", Field("string", "", "Once","Repeat")), ("cooldownMinutes", I("1..1440")), ("includeExtendedHours", B()), ("note", S())),
        Op("delete_alert", "Delete an alert rule; history is retained.", true, ("id!", S())),
        Op("set_alert_enabled", "Enable/disable and re-arm a local alert rule.", true, ("id!", S()), ("enabled!", B())),
        ..CoverageOperations(),
        Op("get_settings", "Read non-secret preferences and resolved data directory; proxy credentials/URLs are omitted.")
    ];
    private static JsonObject Legs()
    {
        var fields = new JsonObject { ["type"] = Field("string", "", "Call", "Put", "Stock"), ["side"] = Field("string", "", "Long", "Short"), ["quantity"] = I(), ["strike"] = N(), ["premium"] = N(), ["expiry"] = S("YYYY-MM-DD; required for options"), ["iv"] = N("Optional decimal IV"), ["multiplier"] = I("default 100; stock uses 1") };
        return new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 32, ["items"] = new JsonObject { ["type"] = "object", ["properties"] = fields, ["required"] = new JsonArray("type", "side", "quantity", "premium"), ["additionalProperties"] = false } };
    }
    public static Operation Find(string name) => All.FirstOrDefault(x => x.Name == name) ?? throw new ArgumentException($"Unknown operation: {name}");
    public static void Validate(Operation op, JsonElement args) => ValidateSchema(op.Schema, args, "arguments");
    private static void ValidateSchema(JsonElement schema, JsonElement value, string path)
    {
        var type = schema.GetProperty("type").GetString();
        bool valid = type switch { "object" => value.ValueKind == JsonValueKind.Object, "array" => value.ValueKind == JsonValueKind.Array, "string" => value.ValueKind == JsonValueKind.String, "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) && double.IsFinite(d), "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _), "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False, _ => false };
        if (!valid) throw new ArgumentException($"{path} must be {type}.");
        if (type == "string" && schema.TryGetProperty("maxLength", out var length) && value.GetString()!.Length > length.GetInt32()) throw new ArgumentException($"{path} is too long.");
        if (type is "number" or "integer")
        {
            var n = value.GetDouble();
            if ((schema.TryGetProperty("minimum", out var minN) && n < minN.GetDouble()) || (schema.TryGetProperty("maximum", out var maxN) && n > maxN.GetDouble())) throw new ArgumentException($"{path} is out of range.");
        }
        if (schema.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(x => x.GetString() == value.GetString())) throw new ArgumentException($"Invalid value for {path}.");
        if (type == "object")
        {
            var props = schema.GetProperty("properties");
            foreach (var req in schema.GetProperty("required").EnumerateArray()) if (!value.TryGetProperty(req.GetString()!, out _)) throw new ArgumentException($"Missing {path}.{req.GetString()}.");
            var seen = new HashSet<string>();
            foreach (var p in value.EnumerateObject()) { if (!seen.Add(p.Name)) throw new ArgumentException($"Duplicate {path}.{p.Name}."); if (!props.TryGetProperty(p.Name, out var f)) throw new ArgumentException($"Unknown {path}.{p.Name}."); ValidateSchema(f, p.Value, path + "." + p.Name); }
        }
        if (type == "array")
        {
            if (value.GetArrayLength() > 10000 || (schema.TryGetProperty("maxItems", out var max) && value.GetArrayLength() > max.GetInt32()) || (schema.TryGetProperty("minItems", out var min) && value.GetArrayLength() < min.GetInt32())) throw new ArgumentException($"Invalid array length: {path}.");
            foreach (var x in value.EnumerateArray()) ValidateSchema(schema.GetProperty("items"), x, path + "[]");
        }
    }
}
