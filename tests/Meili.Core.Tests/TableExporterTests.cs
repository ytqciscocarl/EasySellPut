using System.Text;
using ClosedXML.Excel;
using Meili.Core.Export;
using Meili.Core.Models;

namespace Meili.Core.Tests;

public class TableExporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tyu-export-" + Guid.NewGuid().ToString("N"));
    public TableExporterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static ExportTable Sample() => new()
    {
        Name = "自选",
        Headers = ["代码", "名称", "价格"],
        Rows =
        [
            ["AAPL", "Apple, Inc.", "190.5"],
            ["SPY", "Line" + "\n" + "two", "500"],
        ],
    };

    [Fact]
    public void Csv_HasUtf8Bom_AndEscapesCommasAndQuotes()
    {
        var path = Path.Combine(_dir, "t.csv");
        TableExporter.WriteCsv(Sample(), path);
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        var text = Encoding.UTF8.GetString(bytes[3..]).Replace("\r\n", "\n");
        Assert.Contains("代码,名称,价格", text);
        Assert.Contains("\"Apple, Inc.\"", text);
        Assert.Contains("\"Line\ntwo\"", text);
    }

    [Fact]
    public void CsvEscape_DoublesQuotes() =>
        Assert.Equal("\"say \"\"hi\"\"\"", TableExporter.CsvEscape("say \"hi\""));

    [Fact]
    public void Xlsx_RoundTripsTextAndNumbers_AndKeepsChineseSheetName()
    {
        var path = Path.Combine(_dir, "t.xlsx");
        TableExporter.WriteXlsx(Sample(), path);
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheet("自选");
        Assert.Equal("代码", ws.Cell(1, 1).GetString());
        Assert.Equal("AAPL", ws.Cell(2, 1).GetString());
        Assert.Equal(190.5, ws.Cell(2, 3).GetDouble());
        Assert.Equal(500, ws.Cell(3, 3).GetDouble());
        Assert.True(ws.Cell(1, 1).Style.Font.Bold);
        Assert.True(ws.Cell(1, 1).Style.Font.FontSize >= 12);
    }

    [Fact]
    public void MarketExport_QuotesAndBars_PopulateExpectedColumns()
    {
        var q = new Quote
        {
            Symbol = "AAPL",
            Name = "Apple",
            Price = 190.5,
            Change = 1.25,
            ChangePercent = 0.66,
            Volume = 1000,
            Source = "yahoo",
        };
        var table = MarketExport.Quotes("Watchlist", ["Symbol", "Name", "Price", "Chg", "Chg%", "Vol", "Time", "Source", "Bid", "Ask", "Ext", "Ext%", "State"], [q]);
        Assert.Equal("AAPL", table.Rows[0][0]);
        Assert.Equal("190.5", table.Rows[0][2]);
        Assert.Equal("yahoo", table.Rows[0][7]);

        var series = new BarSeries
        {
            Symbol = "AAPL",
            Interval = BarInterval.Day1,
            Bars = [new Bar(new DateTimeOffset(2026, 11, 11, 16, 0, 0, TimeSpan.FromHours(-5)), 1, 2, 0.5, 1.5, 10)],
        };
        var bars = MarketExport.Bars("K", ["Time", "O", "H", "L", "C", "V"], series);
        Assert.Single(bars.Rows);
        Assert.Equal("1.5", bars.Rows[0][4]);
        Assert.Equal("10", bars.Rows[0][5]);
    }

    [Fact]
    public void MarketExport_Statement_UsesLocalizedItemNames()
    {
        var st = new StatementTable(
            FinancialStatementKind.Income,
            true,
            [new DateOnly(2025, 12, 31)],
            [new StatementRow("TotalRevenue", [1234.5])]);
        var table = MarketExport.Statement("利润表", st, ["营业收入"]);
        Assert.Equal(["利润表", "2025-12-31"], table.Headers);
        Assert.Equal("营业收入", table.Rows[0][0]);
        Assert.Equal("1234.5", table.Rows[0][1]);
    }

    // ---- F9: provenance metadata ----

    private static ExportTable WithMeta() => Sample() with
    {
        Metadata = [new("导出时间", "2026-11-12 21:00:00 +08:00"), new("时效", "延迟 15 分钟"), new("定价假设", "BSM · r = 4.12%")],
    };

    [Fact]
    public void Csv_writes_metadata_after_a_blank_line()
    {
        var path = Path.Combine(_dir, "m.csv");
        TableExporter.WriteCsv(WithMeta(), path);
        var lines = File.ReadAllText(path, Encoding.UTF8).Split(Environment.NewLine);
        var blank = Array.IndexOf(lines, "");
        Assert.True(blank > 2);                                  // header + rows first
        Assert.Equal("导出时间,2026-11-12 21:00:00 +08:00", lines[blank + 1]);
        Assert.Equal("时效,延迟 15 分钟", lines[blank + 2]);
    }

    [Fact]
    public void Xlsx_adds_an_info_sheet_with_text_values()
    {
        var path = Path.Combine(_dir, "m.xlsx");
        TableExporter.WriteXlsx([WithMeta()], path, ("导出信息", "表", "项目", "内容"));
        using var wb = new XLWorkbook(path);
        Assert.Equal(2, wb.Worksheets.Count);
        var info = wb.Worksheet("导出信息");
        Assert.Equal("项目", info.Cell(1, 2).GetString());
        Assert.Equal("自选", info.Cell(2, 1).GetString());
        Assert.Equal("BSM · r = 4.12%", info.Cell(4, 3).GetString());
        Assert.Equal(XLDataType.Text, info.Cell(2, 3).DataType);
        Assert.Equal(3, wb.Worksheet("自选").LastRowUsed()!.RowNumber());  // data sheet untouched
    }

    // ---- non-finite text must stay text in XLSX ----

    [Fact]
    public void Xlsx_NanAndInfinityText_StaysText_AndFiniteNumbersStayNumbers()
    {
        var table = new ExportTable
        {
            Name = "T",
            Headers = ["代码", "价格"],
            Rows =
            [
                ["NAN", "11.52"],
                ["Infinity", "-1.25"],
                ["-Infinity", "+2.43%"],
                ["nan", "NaN"],
            ],
        };
        var path = Path.Combine(_dir, "nan.xlsx");
        TableExporter.WriteXlsx(table, path);   // used to throw ArgumentException (NaN or infinity)
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheet("T");
        foreach (var (row, original) in new[] { (2, "NAN"), (3, "Infinity"), (4, "-Infinity"), (5, "nan") })
        {
            Assert.Equal(XLDataType.Text, ws.Cell(row, 1).DataType);
            Assert.Equal(original, ws.Cell(row, 1).GetString());
        }
        Assert.Equal(XLDataType.Text, ws.Cell(5, 2).DataType);
        Assert.Equal("NaN", ws.Cell(5, 2).GetString());
        Assert.Equal(XLDataType.Number, ws.Cell(2, 2).DataType);
        Assert.Equal(11.52, ws.Cell(2, 2).GetDouble());
        Assert.Equal(-1.25, ws.Cell(3, 2).GetDouble());
        Assert.Equal(2.43, ws.Cell(4, 2).GetDouble());
    }

    // ---- CSV formula guard ----

    private string WriteCsvLines(ExportTable table)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".csv");
        TableExporter.WriteCsv(table, path);
        return File.ReadAllText(path, Encoding.UTF8);
    }

    [Fact]
    public void Csv_formula_like_text_gets_a_leading_apostrophe()
    {
        var table = new ExportTable
        {
            Name = "T",
            Headers = ["A", "B"],
            Rows =
            [
                ["=HYPERLINK(\"http://x\",\"y\")", "@name"],
                ["-5% drop on earnings", "+cmd|' /C calc'!A0"],
                ["\tTabbed", "\rReturned"],
            ],
        };
        var text = WriteCsvLines(table).Replace("\r\n", "\n");
        Assert.Contains("\"'=HYPERLINK(\"\"http://x\"\",\"\"y\"\")\",'@name\n", text);
        Assert.Contains("'-5% drop on earnings,'+cmd|' /C calc'!A0\n", text);
        Assert.Contains("'\tTabbed,\"'\rReturned\"\n", text);
    }

    [Fact]
    public void Csv_numbers_percents_and_plain_text_are_left_alone()
    {
        var table = new ExportTable
        {
            Name = "T",
            Headers = ["A", "B", "C", "D", "E"],
            Rows = [["-1.25", "+2.43%", "AAPL", "2026-11-13", "NAN"]],
        };
        var text = WriteCsvLines(table).Replace("\r\n", "\n");
        Assert.Contains("\n-1.25,+2.43%,AAPL,2026-11-13,NAN\n", text);
    }

    [Fact]
    public void Csv_metadata_values_get_the_same_guard()
    {
        var table = Sample() with
        {
            Metadata = [new("来源", "=cmd|' /C calc'!A0"), new("涨跌", "-1.25"), new("备注", "@alert")],
        };
        var text = WriteCsvLines(table).Replace("\r\n", "\n");
        Assert.Contains("\n来源,'=cmd|' /C calc'!A0\n", text);
        Assert.Contains("\n涨跌,-1.25\n", text);
        Assert.Contains("\n备注,'@alert\n", text);
    }

    [Fact]
    public void CsvEscape_keeps_its_plain_quoting_contract() =>
        Assert.Equal("=1+1", TableExporter.CsvEscape("=1+1"));

    [Fact]
    public void No_metadata_means_no_info_sheet()
    {
        var path = Path.Combine(_dir, "n.xlsx");
        TableExporter.WriteXlsx([Sample()], path);
        using var wb = new XLWorkbook(path);
        Assert.Single(wb.Worksheets);
    }
}
