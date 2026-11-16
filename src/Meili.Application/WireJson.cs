using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meili.Automation;

/// <summary>One JSON contract for CLI, MCP and local automation. Non-finite numeric values are null.</summary>
public static class WireJson
{
    public static JsonSerializerOptions Options { get; } = Create();
    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        o.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        o.Converters.Add(new FiniteDouble());
        return o;
    }
    public static JsonElement Element(object? value) => JsonSerializer.SerializeToElement(value, Options);
    public static T Read<T>(JsonElement value) => value.Deserialize<T>(Options) ?? throw new ArgumentException("Input must not be null.");
    public static object Success(object? value) => new { schemaVersion = 1, success = true, generatedAt = DateTimeOffset.UtcNow, data = value, numberEncoding = "Non-finite values are null; inspect unbounded flags, incomplete bars and calculation status." };
    public static object Error(Exception e) => new { schemaVersion = 1, success = false, error = new { code = Code(e), message = SafeMessage(e), recovery = Recovery(e) } };
    public static string Code(Exception e) => e switch
    {
        AiSetupException setup => setup.Code,
        SkillInstallConflictException => "skill_conflict",
        Core.Settings.DataConflictException => "data_conflict",
        ArgumentException or JsonException or FormatException or OverflowException => "invalid_arguments",
        OperationCanceledException => "cancelled",
        UnauthorizedAccessException => "permission_denied",
        IOException => "io_error",
        Meili.Data.Routing.AllSourcesFailedException or Meili.Data.Http.DataSourceException => "data_source_error",
        _ => "operation_failed"
    };
    public static string Recovery(Exception e) => e is AiSetupException setup ? setup.Recovery : Code(e) switch
    {
        "invalid_arguments" => "Read tools/list inputSchema or CLI --help; correct the named fields and decimal units before retrying.",
        "permission_denied" => "Use available read-only tools. Request explicit user authorization before a separate writable connection; never bypass permissions with shell or file edits.",
        "data_conflict" => "Reload the affected local state, reconcile the user's requested change, then retry with a fresh snapshot.",
        "data_source_error" => "Keep missing data explicit. Retry once with a smaller request when useful; configured provider routing handles failover. Do not invent source parameters. get_source_health is process-local, not a live connectivity guarantee.",
        "skill_conflict" => "Custom skill content was preserved. Review it, make a backup and move it aside before retrying installation.",
        "cancelled" => "The request was cancelled or timed out. Reduce the date range or page size before retrying.",
        "io_error" => "Check the selected local directory and permissions; do not switch to another data directory silently.",
        _ => "Inspect local diagnostics and report the unresolved limitation; do not invent a successful result."
    };
    private static string SafeMessage(Exception e) => e is AiSetupException setup ? setup.Message : e is SkillInstallConflictException conflict ? conflict.Message
        : e is Meili.Data.Routing.AllSourcesFailedException all ? $"All sources failed for {all.Capability}: {string.Join(", ", all.Errors.Select(x => x.Source))}. Check connectivity and provider availability."
        : e is Meili.Data.Http.DataSourceException source ? $"Source {source.SourceId}: {source.Kind} (HTTP {source.StatusCode?.ToString() ?? "unknown"})."
        : e is ArgumentException or JsonException or FormatException or OverflowException or Core.Settings.DataConflictException ? e.Message
        : e is OperationCanceledException ? "Operation cancelled or timed out."
        : e is IOException ? "Local file operation failed. Check the data directory and file permissions."
        : e is UnauthorizedAccessException ? "This operation is not permitted. Check local file access; MCP writes additionally require an explicitly authorized --allow-writes connection."
        : "Operation failed. Check data source connectivity or local diagnostic logs.";
    private sealed class FiniteDouble : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDouble();
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        { if (double.IsFinite(value)) writer.WriteNumberValue(value); else writer.WriteNullValue(); }
    }
}
