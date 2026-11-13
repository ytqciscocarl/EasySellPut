using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace Meili.Core.Export;

/// <summary>A named sheet/table of already-formatted text cells, ready for CSV or XLSX.</summary>
public sealed record ExportTable
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Headers { get; init; }
    public required IReadOnlyList<IReadOnlyList<string>> Rows { get; init; }
    /// <summary>F9: provenance rows (source, as-of, export time, delay, pricing assumptions) written with the data.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Metadata { get; init; } = [];
}

/// <summary>Writes export tables as UTF-8 BOM CSV (Excel-friendly Chinese) or a formatted .xlsx workbook.</summary>
public static class TableExporter
{
    public static void WriteCsv(ExportTable table, string path)
    {
        ArgumentNullException.ThrowIfNull(table);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var sb = new StringBuilder();
        sb.AppendLine(Join(table.Headers));
        foreach (var row in table.Rows)
            sb.AppendLine(JoinGuarded(row));
        // F9: provenance after one blank line, so the data block stays a clean table for Excel/pandas.
        if (table.Metadata.Count > 0)
        {
            sb.AppendLine();
            foreach (var (k, v) in table.Metadata) sb.AppendLine(JoinGuarded([k, v]));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public static void WriteXlsx(ExportTable table, string path) => WriteXlsx([table], path);

    /// <param name="infoSheet">F9: name and headers (table, item, value) of the provenance sheet added when any table has metadata.</param>
    public static void WriteXlsx(IReadOnlyList<ExportTable> tables, string path, (string Name, string Table, string Item, string Value)? infoSheet = null)
    {
        ArgumentNullException.ThrowIfNull(tables);
        if (tables.Count == 0) throw new ArgumentException("At least one table is required.", nameof(tables));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var wb = new XLWorkbook();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            var name = UniqueSheetName(table.Name, used);
            var ws = wb.Worksheets.Add(name);
            for (var c = 0; c < table.Headers.Count; c++)
                ws.Cell(1, c + 1).Value = table.Headers[c] ?? "";
            var header = ws.Range(1, 1, 1, Math.Max(1, table.Headers.Count));
            header.Style.Font.Bold = true;
            header.Style.Font.FontSize = 12;
            for (var r = 0; r < table.Rows.Count; r++)
            {
                var row = table.Rows[r];
                for (var c = 0; c < table.Headers.Count; c++)
                {
                    var text = c < row.Count ? row[c] ?? "" : "";
                    var cell = ws.Cell(r + 2, c + 1);
                    cell.Style.Font.FontSize = 12;
                    if (TryNumber(text, out var number)) cell.Value = number;
                    else cell.Value = text;
                }
            }
            if (table.Headers.Count > 0)
                ws.SheetView.FreezeRows(1);
            ws.Columns().AdjustToContents(1, 40, 8, 40);
        }
        if (tables.Any(t => t.Metadata.Count > 0))
        {
            var info = infoSheet ?? ("Info", "Table", "Item", "Value");
            var ws = wb.Worksheets.Add(UniqueSheetName(info.Name, used));
            ws.Cell(1, 1).Value = info.Table; ws.Cell(1, 2).Value = info.Item; ws.Cell(1, 3).Value = info.Value;
            ws.Range(1, 1, 1, 3).Style.Font.Bold = true;
            var r = 2;
            foreach (var t in tables)
                foreach (var (k, v) in t.Metadata)
                {
                    ws.Cell(r, 1).Value = t.Name; ws.Cell(r, 2).Value = k; ws.Cell(r, 3).Value = v; // text, never parsed as numbers
                    r++;
                }
            ws.Range(1, 1, Math.Max(1, r - 1), 3).Style.Font.FontSize = 12;
            ws.SheetView.FreezeRows(1);
            ws.Columns().AdjustToContents(1, 60, 8, 80);
        }
        wb.SaveAs(path);
    }

    public static string CsvEscape(string? value)
    {
        var s = value ?? "";
        if (s.Contains('"') || s.Contains(',') || s.Contains('\n') || s.Contains('\r'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    private static string Join(IReadOnlyList<string> cells) =>
        string.Join(",", (cells ?? []).Select(CsvEscape));

    /// <summary>Joins data or metadata cells; third-party text must not start a spreadsheet formula (see <see cref="GuardFormula"/>).</summary>
    private static string JoinGuarded(IReadOnlyList<string> cells) =>
        string.Join(",", (cells ?? []).Select(c => CsvEscape(GuardFormula(c))));

    /// <summary>
    /// CSV injection guard: Excel evaluates a cell starting with = + - @ tab or CR as a formula. Text that is not a
    /// plain number gets one leading apostrophe; signed numbers and percents such as "-1.25" or "+2.43%" stay as they are.
    /// </summary>
    private static string GuardFormula(string? value)
    {
        var s = value ?? "";
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r' && !TryNumber(s, out _))
            return "'" + s;
        return s;
    }

    private static string UniqueSheetName(string? raw, HashSet<string> used)
    {
        var name = string.IsNullOrWhiteSpace(raw) ? "Sheet" : raw.Trim();
        foreach (var ch in Path.GetInvalidFileNameChars().Concat([':', '\\', '/', '?', '*', '[', ']']))
            name = name.Replace(ch, ' ');
        name = name.Trim();
        if (name.Length == 0) name = "Sheet";
        if (name.Length > 31) name = name[..31];
        var candidate = name;
        var i = 2;
        while (!used.Add(candidate))
        {
            var suffix = "_" + i++;
            candidate = (name.Length + suffix.Length <= 31 ? name : name[..(31 - suffix.Length)]) + suffix;
        }
        return candidate;
    }

    /// <summary>True only for finite numbers: "NAN" (a real ticker), "Infinity" and overflowing text stay text.</summary>
    private static bool TryNumber(string text, out double number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (t.EndsWith('%')) t = t[..^1].Trim();
        var parsed = double.TryParse(t, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out number)
            || double.TryParse(t, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out number);
        if (parsed && double.IsFinite(number)) return true;
        number = 0;
        return false;
    }
}