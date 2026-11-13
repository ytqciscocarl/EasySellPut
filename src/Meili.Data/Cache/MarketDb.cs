using System.Globalization;
using Microsoft.Data.Sqlite;
using Meili.Core.Models;

namespace Meili.Data.Cache;

/// <summary>Metadata stored with a cached bar series.</summary>
/// <param name="CoveredFrom">Start the full download asked for; null means the provider's default history window.</param>
public sealed record BarCacheMeta(string Source, DateTimeOffset FetchedAt, DateTimeOffset? AsOf, int? DelayMinutes, bool IsRealtime,
    DateTimeOffset? CoveredFrom = null, PriceAdjustment Basis = PriceAdjustment.Unknown);

/// <summary>
/// B2: local SQLite store (<c>cache/market.db</c> in the data folder) for K-line history and, later, IV samples (B1).
/// Bars are keyed by (symbol, interval, adjust, time). F7: <c>adjust</c> is the requested mode
/// (<see cref="AdjustModeExtensions.CacheKey"/>: "" = split-adjusted, "total" = splits + dividends) and
/// <c>bar_meta.basis</c> records what the source actually delivered (schema v2).
/// Missing O/H/L (F2) are stored as NULL, never as a made-up value. Every call opens a pooled connection, so the
/// class is safe to share across threads.
/// </summary>
public sealed class MarketDb
{
    public const int SchemaVersion = 3;
    private readonly string _cs;

    public MarketDb(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        FilePath = path;
        _cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared, Pooling = true }.ToString();
        Migrate();
    }

    public string FilePath { get; }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=3000;";
        pragma.ExecuteNonQuery();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_version (v INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS bars (
                symbol TEXT NOT NULL, interval TEXT NOT NULL, adjust TEXT NOT NULL DEFAULT '',
                t INTEGER NOT NULL, o REAL, h REAL, l REAL, c REAL NOT NULL, v REAL NOT NULL, incomplete INTEGER NOT NULL,
                PRIMARY KEY (symbol, interval, adjust, t)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS bar_meta (
                symbol TEXT NOT NULL, interval TEXT NOT NULL, adjust TEXT NOT NULL DEFAULT '',
                source TEXT NOT NULL, fetched_at INTEGER NOT NULL, as_of INTEGER, delay_minutes INTEGER, realtime INTEGER NOT NULL, covered_from INTEGER,
                PRIMARY KEY (symbol, interval, adjust));
            CREATE TABLE IF NOT EXISTS iv_samples (
                symbol TEXT NOT NULL, t INTEGER NOT NULL, kind TEXT NOT NULL, iv REAL NOT NULL, source TEXT NOT NULL,
                PRIMARY KEY (symbol, t, kind)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS oi_daily (
                symbol TEXT NOT NULL, d INTEGER NOT NULL, contract TEXT NOT NULL, oi REAL NOT NULL,
                PRIMARY KEY (symbol, d, contract)) WITHOUT ROWID;
            """;
        cmd.ExecuteNonQuery();
        // v2 (F7): adjustment basis of each cached series. Old rows read as Unknown and get refreshed by the next fetch.
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('bar_meta') WHERE name='basis'";
        if (Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
        {
            cmd.CommandText = "ALTER TABLE bar_meta ADD COLUMN basis TEXT NOT NULL DEFAULT ''";
            cmd.ExecuteNonQuery();
        }
        cmd.CommandText = "SELECT COALESCE(MAX(v), 0) FROM schema_version";
        var v = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (v < SchemaVersion)
        {
            cmd.CommandText = v == 0 ? $"INSERT INTO schema_version (v) VALUES ({SchemaVersion})" : $"UPDATE schema_version SET v = {SchemaVersion}";
            cmd.ExecuteNonQuery();
        }
    }

    public int ReadSchemaVersion()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT MAX(v) FROM schema_version";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string Key(BarInterval i) => i.ToString();

    public (IReadOnlyList<Bar> Bars, BarCacheMeta? Meta) LoadBars(string symbol, BarInterval interval, string adjust = "")
    {
        using var c = Open();
        BarCacheMeta? meta = null;
        using (var m = c.CreateCommand())
        {
            m.CommandText = "SELECT source, fetched_at, as_of, delay_minutes, realtime, covered_from, basis FROM bar_meta WHERE symbol=$s AND interval=$i AND adjust=$a";
            m.Parameters.AddWithValue("$s", symbol);
            m.Parameters.AddWithValue("$i", Key(interval));
            m.Parameters.AddWithValue("$a", adjust);
            using var r = m.ExecuteReader();
            if (r.Read())
                meta = new BarCacheMeta(r.GetString(0), DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1)),
                    r.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(2)),
                    r.IsDBNull(3) ? null : r.GetInt32(3), r.GetInt64(4) != 0,
                    r.IsDBNull(5) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(5)),
                    Enum.TryParse<PriceAdjustment>(r.GetString(6), out var basis) ? basis : PriceAdjustment.Unknown);
        }
        if (meta is null) return ([], null);
        var bars = new List<Bar>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT t, o, h, l, c, v, incomplete FROM bars WHERE symbol=$s AND interval=$i AND adjust=$a ORDER BY t";
            q.Parameters.AddWithValue("$s", symbol);
            q.Parameters.AddWithValue("$i", Key(interval));
            q.Parameters.AddWithValue("$a", adjust);
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                double D(int k) => r.IsDBNull(k) ? double.NaN : r.GetDouble(k);
                bars.Add(new Bar(DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)), D(1), D(2), D(3), r.GetDouble(4), r.GetDouble(5))
                    { Incomplete = r.GetInt64(6) != 0 });
            }
        }
        return (bars, meta);
    }

    /// <summary>Replaces the whole cached series (and its metadata) in one transaction.</summary>
    public void SaveBars(string symbol, BarInterval interval, IReadOnlyList<Bar> bars, BarCacheMeta meta, string adjust = "")
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        DeleteRange(c, tx, symbol, interval, adjust, "");
        InsertBars(c, tx, symbol, interval, adjust, bars);
        UpsertMeta(c, tx, symbol, interval, adjust, meta);
        tx.Commit();
    }

    /// <summary>
    /// Updates a cached series in place, in one transaction: deletes rows with <c>t &gt;= deleteFrom</c> (when given) and rows
    /// with <c>t &lt; deleteBefore</c> (when given), inserts <paramref name="bars"/> (replacing equal times) and upserts the
    /// metadata row. With no bars and no deletes only the metadata is refreshed.
    /// </summary>
    public void ReplaceTail(string symbol, BarInterval interval, string adjust, IReadOnlyList<Bar> bars,
        DateTimeOffset? deleteFrom, DateTimeOffset? deleteBefore, BarCacheMeta meta)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        if (deleteFrom is { } from) DeleteRange(c, tx, symbol, interval, adjust, " AND t >= $t", from);
        if (deleteBefore is { } before) DeleteRange(c, tx, symbol, interval, adjust, " AND t < $t", before);
        InsertBars(c, tx, symbol, interval, adjust, bars);
        UpsertMeta(c, tx, symbol, interval, adjust, meta);
        tx.Commit();
    }

    private static void DeleteRange(SqliteConnection c, SqliteTransaction tx, string symbol, BarInterval interval, string adjust, string timeFilter, DateTimeOffset? t = null)
    {
        using var del = c.CreateCommand();
        del.Transaction = tx;
        del.CommandText = "DELETE FROM bars WHERE symbol=$s AND interval=$i AND adjust=$a" + timeFilter;
        del.Parameters.AddWithValue("$s", symbol);
        del.Parameters.AddWithValue("$i", Key(interval));
        del.Parameters.AddWithValue("$a", adjust);
        if (t is { } time) del.Parameters.AddWithValue("$t", time.ToUnixTimeSeconds());
        del.ExecuteNonQuery();
    }

    private static void InsertBars(SqliteConnection c, SqliteTransaction tx, string symbol, BarInterval interval, string adjust, IReadOnlyList<Bar> bars)
    {
        if (bars.Count == 0) return;
        using var ins = c.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = "INSERT OR REPLACE INTO bars (symbol, interval, adjust, t, o, h, l, c, v, incomplete) VALUES ($s,$i,$a,$t,$o,$h,$l,$c,$v,$x)";
        var ps = new[] { "$s", "$i", "$a", "$t", "$o", "$h", "$l", "$c", "$v", "$x" }.Select(n =>
        {
            var p = ins.CreateParameter();
            p.ParameterName = n;
            ins.Parameters.Add(p);
            return p;
        }).ToArray();
        ps[0].Value = symbol; ps[1].Value = Key(interval); ps[2].Value = adjust;
        foreach (var b in bars)
        {
            ps[3].Value = b.Time.ToUnixTimeSeconds();
            ps[4].Value = Db(b.Open); ps[5].Value = Db(b.High); ps[6].Value = Db(b.Low);
            ps[7].Value = b.Close; ps[8].Value = double.IsFinite(b.Volume) ? b.Volume : 0; ps[9].Value = b.Incomplete ? 1 : 0;
            ins.ExecuteNonQuery();
        }

        static object Db(double v) => double.IsFinite(v) ? v : DBNull.Value;
    }

    private static void UpsertMeta(SqliteConnection c, SqliteTransaction tx, string symbol, BarInterval interval, string adjust, BarCacheMeta meta)
    {
        using var m = c.CreateCommand();
        m.Transaction = tx;
        m.CommandText = "INSERT OR REPLACE INTO bar_meta (symbol, interval, adjust, source, fetched_at, as_of, delay_minutes, realtime, covered_from, basis) VALUES ($s,$i,$a,$src,$f,$as,$d,$r,$cf,$b)";
        m.Parameters.AddWithValue("$s", symbol);
        m.Parameters.AddWithValue("$i", Key(interval));
        m.Parameters.AddWithValue("$a", adjust);
        m.Parameters.AddWithValue("$src", meta.Source);
        m.Parameters.AddWithValue("$f", meta.FetchedAt.ToUnixTimeSeconds());
        m.Parameters.AddWithValue("$as", meta.AsOf is { } a ? a.ToUnixTimeSeconds() : DBNull.Value);
        m.Parameters.AddWithValue("$d", meta.DelayMinutes is { } d ? d : DBNull.Value);
        m.Parameters.AddWithValue("$r", meta.IsRealtime ? 1 : 0);
        m.Parameters.AddWithValue("$cf", meta.CoveredFrom is { } cf ? cf.ToUnixTimeSeconds() : DBNull.Value);
        m.Parameters.AddWithValue("$b", meta.Basis.ToString());
        m.ExecuteNonQuery();
    }

    // ---------------- IV samples (B1) ----------------

    public void AddIvSample(string symbol, DateTimeOffset t, string kind, double iv, string source)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO iv_samples (symbol, t, kind, iv, source) VALUES ($s,$t,$k,$iv,$src)";
        cmd.Parameters.AddWithValue("$s", symbol);
        cmd.Parameters.AddWithValue("$t", t.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$iv", iv);
        cmd.Parameters.AddWithValue("$src", source);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(DateTimeOffset T, string Kind, double Iv, string Source)> LoadIvSamples(string symbol, DateTimeOffset since)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT t, kind, iv, source FROM iv_samples WHERE symbol=$s AND t >= $t ORDER BY t";
        cmd.Parameters.AddWithValue("$s", symbol);
        cmd.Parameters.AddWithValue("$t", since.ToUnixTimeSeconds());
        using var r = cmd.ExecuteReader();
        var list = new List<(DateTimeOffset, string, double, string)>();
        while (r.Read()) list.Add((DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)), r.GetString(1), r.GetDouble(2), r.GetString(3)));
        return list;
    }

    /// <summary>Drops IV samples older than <paramref name="before"/> (keeps the file small).</summary>
    public int PruneIvSamples(DateTimeOffset before)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM iv_samples WHERE t < $t";
        cmd.Parameters.AddWithValue("$t", before.ToUnixTimeSeconds());
        return cmd.ExecuteNonQuery();
    }

    // ---------------- Open interest by day (B6) ----------------

    private static long Day(DateOnly d) => d.Year * 10000L + d.Month * 100 + d.Day;

    /// <summary>B6: one open-interest reading per contract and trading day (later readings of the same day replace earlier ones).</summary>
    public void SaveOpenInterest(string symbol, DateOnly day, IEnumerable<(string Contract, double Oi)> readings)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO oi_daily (symbol, d, contract, oi) VALUES ($s,$d,$c,$oi)";
        var pc = cmd.Parameters.Add("$c", Microsoft.Data.Sqlite.SqliteType.Text);
        var po = cmd.Parameters.Add("$oi", Microsoft.Data.Sqlite.SqliteType.Real);
        cmd.Parameters.AddWithValue("$s", symbol);
        cmd.Parameters.AddWithValue("$d", Day(day));
        foreach (var (contract, oi) in readings)
        {
            pc.Value = contract;
            po.Value = oi;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>B6: open interest from the most recent stored day before <paramref name="day"/>, by contract.</summary>
    public IReadOnlyDictionary<string, double> LoadPreviousOpenInterest(string symbol, DateOnly day)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT contract, oi FROM oi_daily WHERE symbol=$s AND d = (SELECT MAX(d) FROM oi_daily WHERE symbol=$s AND d < $d)";
        cmd.Parameters.AddWithValue("$s", symbol);
        cmd.Parameters.AddWithValue("$d", Day(day));
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        while (r.Read()) map[r.GetString(0)] = r.GetDouble(1);
        return map;
    }

    /// <summary>Drops open-interest days before <paramref name="before"/>.</summary>
    public int PruneOpenInterest(DateOnly before)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM oi_daily WHERE d < $d";
        cmd.Parameters.AddWithValue("$d", Day(before));
        return cmd.ExecuteNonQuery();
    }
}
