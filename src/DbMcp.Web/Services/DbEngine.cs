using System.Data.Common;
using System.Diagnostics;
using System.Text;
using Microsoft.Data.SqlClient;
using MySqlConnector;

namespace DbMcp.Web.Services;

public sealed record ConnectionSummary(string Name, string Provider, string? Server, string? Database, bool AllowWrites, bool AllowSchemaChanges, int MaxRows);

public sealed record QueryResult(string Connection, string[] Columns, List<object?[]> Rows, bool Truncated, string? Note, long ElapsedMs);

public sealed record ExecResult(string Connection, int RowsAffected, bool Committed, string Message);

public sealed record TableInfo(string Name, string Type, long? EstimatedRows, double? SizeMb);

public sealed record ColumnInfo(string Name, string Type, bool Nullable, string? Key, string? Default, string? Extra, string? Comment);

public sealed record ForeignKeyInfo(string Name, string Column, string ReferencesTable, string ReferencesColumn);

public sealed record IndexInfo(string Name, bool Unique, string Columns);

public sealed record TableDescription(string Table, ColumnInfo[] Columns, ForeignKeyInfo[] ForeignKeys, IndexInfo[] Indexes);

public sealed record SchemaMatch(string Table, string Column, string Type);

/// <summary>Runs SQL and schema queries for the named connections. Reads always happen inside a transaction that is rolled back.</summary>
public sealed class DbEngine(DbSettingsStore store)
{
    private const int HardMaxRows = 1000;
    private const int MaxCellChars = 500;
    private const int MaxResultChars = 80_000;

    public IReadOnlyList<ConnectionSummary> ListConnections() =>
        [.. store.Get().Connections.Select(c =>
        {
            var (server, db) = c.Describe();
            return new ConnectionSummary(c.Name, c.Provider.ToString(), server, db, c.AllowWrites, c.AllowWrites && c.AllowSchemaChanges, c.MaxRows);
        })];

    /// <summary>Opens a connection and reports the server version. Used by the "Test" button.</summary>
    public async Task<string> TestAsync(ConnectionEntry c, CancellationToken ct = default)
    {
        await using var conn = Create(c);
        await conn.OpenAsync(ct);
        return $"Connected to {c.Provider} {conn.ServerVersion}, database '{(string.IsNullOrEmpty(conn.Database) ? "(none)" : conn.Database)}'.";
    }

    // ---- queries ----------------------------------------------------------------------------------------------

    public Task<QueryResult> QueryAsync(string connection, string sql, int? maxRows, CancellationToken ct)
    {
        var c = Resolve(connection);
        SqlGuard.EnsureReadOnly(sql, c.Provider);
        return RunReadAsync(c, sql, Math.Clamp(maxRows ?? c.MaxRows, 1, HardMaxRows), ct);
    }

    public async Task<QueryResult> ExplainAsync(string connection, string sql, CancellationToken ct)
    {
        var c = Resolve(connection);
        SqlGuard.EnsureReadOnly(sql, c.Provider);

        if (c.Provider == DbProvider.MySql)
            return await RunReadAsync(c, "EXPLAIN " + sql, 200, ct);

        // SQL Server: with SHOWPLAN on, the statement is compiled and described but not executed.
        await using var conn = Create(c);
        await conn.OpenAsync(ct);
        await ExecAsync(conn, "SET SHOWPLAN_TEXT ON", c.TimeoutSeconds, ct);
        try { return await ReadRowsAsync(conn, c, sql, 200, ct, allResultSets: true); }
        finally { await TryExecAsync(conn, "SET SHOWPLAN_TEXT OFF", c.TimeoutSeconds); }
    }

    public async Task<ExecResult> ExecuteAsync(string connection, string sql, bool dryRun, CancellationToken ct)
    {
        var c = Resolve(connection);
        if (!c.AllowWrites)
            throw new DbToolException($"Connection '{c.Name}' is read-only. Turn on 'Allow writes' for it on the DbMcp home page first.");

        var kind = SqlGuard.EnsureWritable(sql, c.Provider, c.AllowSchemaChanges);
        if (dryRun && kind == StatementKind.Ddl)
            throw new DbToolException("A dry run is not possible for schema changes: the database commits them immediately.");

        await using var conn = Create(c);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.CommandTimeout = c.TimeoutSeconds;
            var affected = await cmd.ExecuteNonQueryAsync(ct);

            if (dryRun)
            {
                await tx.RollbackAsync(ct);
                return new ExecResult(c.Name, affected, false, $"Dry run: {affected} row(s) would be affected. Nothing was changed.");
            }
            await tx.CommitAsync(ct);
            return new ExecResult(c.Name, Math.Max(affected, 0), true, affected < 0 ? "Statement executed." : $"{affected} row(s) affected.");
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); } catch (Exception) { /* connection already gone */ }
            throw;
        }
    }

    private async Task<QueryResult> RunReadAsync(ConnectionEntry c, string sql, int limit, CancellationToken ct)
    {
        await using var conn = Create(c);
        await conn.OpenAsync(ct);
        await ExecAsync(conn, c.Provider == DbProvider.MySql ? "START TRANSACTION READ ONLY" : "BEGIN TRANSACTION", c.TimeoutSeconds, ct);
        try
        {
            return await ReadRowsAsync(conn, c, sql, limit, ct);
        }
        finally
        {
            await TryExecAsync(conn, c.Provider == DbProvider.MySql ? "ROLLBACK" : "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION", c.TimeoutSeconds);
        }
    }

    /// <param name="allResultSets">SQL Server's SHOWPLAN output arrives as one result set per statement; they are concatenated.</param>
    private static async Task<QueryResult> ReadRowsAsync(DbConnection conn, ConnectionEntry c, string sql, int limit, CancellationToken ct, bool allResultSets = false)
    {
        var sw = Stopwatch.StartNew();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = c.TimeoutSeconds;
        var reader = await cmd.ExecuteReaderAsync(ct);
        try
        {
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
            var rows = new List<object?[]>();
            var budget = MaxResultChars;
            string? note = null;

            do
            {
                while (await reader.ReadAsync(ct))
                {
                    if (rows.Count >= limit)
                    {
                        note = $"Stopped after {limit} rows; there are more. Add a WHERE / LIMIT / aggregate to narrow the query, or raise maxRows (max {HardMaxRows}).";
                        break;
                    }
                    var row = new object?[reader.FieldCount];
                    for (var i = 0; i < row.Length; i++)
                    {
                        row[i] = Cell(reader.GetValue(i));
                        budget -= row[i] is string s ? s.Length : 8;
                    }
                    rows.Add(row);
                    if (budget <= 0)
                    {
                        note = "Stopped early because the result is large. Select fewer columns or rows.";
                        break;
                    }
                }
            } while (note is null && allResultSets && await reader.NextResultAsync(ct));

            // Don't make the server stream the rest of a huge result just to throw it away.
            if (note is not null) try { cmd.Cancel(); } catch (Exception) { /* nothing to cancel */ }
            return new QueryResult(c.Name, columns, rows, note is not null, note, sw.ElapsedMilliseconds);
        }
        finally
        {
            try { await reader.DisposeAsync(); } catch (Exception) { /* cancelled mid-stream */ }
        }
    }

    // ---- schema -----------------------------------------------------------------------------------------------

    public async Task<string[]> ListDatabasesAsync(string connection, CancellationToken ct)
    {
        var c = Resolve(connection);
        var sql = c.Provider == DbProvider.MySql
            ? "SELECT schema_name FROM information_schema.schemata WHERE schema_name NOT IN ('mysql','sys','information_schema','performance_schema') ORDER BY schema_name"
            : "SELECT name FROM sys.databases WHERE database_id > 4 AND HAS_DBACCESS(name) = 1 ORDER BY name";
        await using var conn = await OpenAsync(c, null, ct);
        return [.. (await RowsAsync(conn, sql, c.TimeoutSeconds, ct)).Select(r => Str(r[0]) ?? "")];
    }

    public async Task<TableInfo[]> ListTablesAsync(string connection, string? database, CancellationToken ct)
    {
        var c = Resolve(connection);
        await using var conn = await OpenAsync(c, c.Provider == DbProvider.SqlServer ? database : null, ct);
        List<object?[]> rows;
        if (c.Provider == DbProvider.MySql)
        {
            rows = await RowsAsync(conn,
                """
                SELECT table_name, table_type, table_rows, ROUND((COALESCE(data_length,0) + COALESCE(index_length,0)) / 1048576, 2)
                FROM information_schema.tables WHERE table_schema = COALESCE(@db, DATABASE()) ORDER BY table_name
                """, c.TimeoutSeconds, ct, ("@db", Blank(database)));
        }
        else
        {
            rows = await RowsAsync(conn,
                """
                SELECT s.name + '.' + t.name, 'TABLE',
                       SUM(CASE WHEN i.index_id IN (0, 1) THEN p.rows END),
                       CAST(ROUND(SUM(a.total_pages) * 8 / 1024.0, 2) AS float)
                FROM sys.tables t
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                JOIN sys.indexes i ON i.object_id = t.object_id
                JOIN sys.partitions p ON p.object_id = i.object_id AND p.index_id = i.index_id
                JOIN sys.allocation_units a ON a.container_id = p.partition_id
                GROUP BY s.name, t.name
                UNION ALL
                SELECT s.name + '.' + v.name, 'VIEW', NULL, NULL FROM sys.views v JOIN sys.schemas s ON s.schema_id = v.schema_id
                ORDER BY 1
                """, c.TimeoutSeconds, ct);
        }
        return [.. rows.Select(r => new TableInfo(
            Str(r[0]) ?? "", (Str(r[1]) ?? "").Contains("VIEW", StringComparison.OrdinalIgnoreCase) ? "VIEW" : "TABLE", Long(r[2]), Dbl(r[3])))];
    }

    public async Task<TableDescription> DescribeTableAsync(string connection, string table, string? database, CancellationToken ct)
    {
        var c = Resolve(connection);
        var parts = table.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim('`', '[', ']', '"')).ToArray();
        if (parts.Length == 0) throw new DbToolException("Give a table name.");

        return c.Provider == DbProvider.MySql
            ? await DescribeMySqlAsync(c, parts, database, ct)
            : await DescribeSqlServerAsync(c, parts, database, ct);
    }

    private async Task<TableDescription> DescribeMySqlAsync(ConnectionEntry c, string[] parts, string? database, CancellationToken ct)
    {
        // "db.table" in the name wins over the database argument.
        var (db, name) = parts.Length >= 2 ? (parts[^2], parts[^1]) : (Blank(database) as string, parts[0]);
        await using var conn = await OpenAsync(c, null, ct);
        var p = new (string, object?)[] { ("@db", db), ("@t", name) };

        var cols = await RowsAsync(conn,
            """
            SELECT column_name, column_type, is_nullable, column_key, column_default, extra, column_comment
            FROM information_schema.columns WHERE table_schema = COALESCE(@db, DATABASE()) AND table_name = @t ORDER BY ordinal_position
            """, c.TimeoutSeconds, ct, p);
        if (cols.Count == 0) throw new DbToolException($"Table '{name}' was not found" + (db is null ? " in the current database." : $" in database '{db}'."));

        var fks = await RowsAsync(conn,
            """
            SELECT constraint_name, column_name, CONCAT(referenced_table_schema, '.', referenced_table_name), referenced_column_name
            FROM information_schema.key_column_usage
            WHERE table_schema = COALESCE(@db, DATABASE()) AND table_name = @t AND referenced_table_name IS NOT NULL
            ORDER BY constraint_name, ordinal_position
            """, c.TimeoutSeconds, ct, p);

        var idx = await RowsAsync(conn,
            """
            SELECT index_name, non_unique, GROUP_CONCAT(column_name ORDER BY seq_in_index SEPARATOR ', ')
            FROM information_schema.statistics WHERE table_schema = COALESCE(@db, DATABASE()) AND table_name = @t
            GROUP BY index_name, non_unique ORDER BY index_name
            """, c.TimeoutSeconds, ct, p);

        return new TableDescription(
            db is null ? name : $"{db}.{name}",
            [.. cols.Select(r => new ColumnInfo(Str(r[0]) ?? "", Str(r[1]) ?? "", string.Equals(Str(r[2]), "YES", StringComparison.OrdinalIgnoreCase),
                NullIfEmpty(Str(r[3])), Str(r[4]), NullIfEmpty(Str(r[5])), NullIfEmpty(Str(r[6]))))],
            [.. fks.Select(r => new ForeignKeyInfo(Str(r[0]) ?? "", Str(r[1]) ?? "", Str(r[2]) ?? "", Str(r[3]) ?? ""))],
            [.. idx.Select(r => new IndexInfo(Str(r[0]) ?? "", Long(r[1]) == 0, Str(r[2]) ?? ""))]);
    }

    private async Task<TableDescription> DescribeSqlServerAsync(ConnectionEntry c, string[] parts, string? database, CancellationToken ct)
    {
        await using var conn = await OpenAsync(c, database, ct);
        string schema;
        var name = parts[^1];
        if (parts.Length >= 2)
        {
            schema = parts[^2];
        }
        else
        {
            var found = await RowsAsync(conn,
                "SELECT TOP 3 s.name FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE o.name = @n AND o.type IN ('U','V')",
                c.TimeoutSeconds, ct, ("@n", name));
            if (found.Count == 0) throw new DbToolException($"Table or view '{name}' was not found.");
            if (found.Count > 1) throw new DbToolException($"'{name}' exists in several schemas ({string.Join(", ", found.Select(r => Str(r[0])))}). Use schema.table.");
            schema = Str(found[0][0]) ?? "dbo";
        }

        var obj = $"[{schema.Replace("]", "]]")}].[{name.Replace("]", "]]")}]";
        var p = ("@obj", (object?)obj);

        var cols = await RowsAsync(conn,
            """
            SELECT c.name,
                   TYPE_NAME(c.user_type_id) + CASE
                     WHEN TYPE_NAME(c.user_type_id) IN ('varchar','char','varbinary','binary') THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length AS varchar(10)) END + ')'
                     WHEN TYPE_NAME(c.user_type_id) IN ('nvarchar','nchar') THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length / 2 AS varchar(10)) END + ')'
                     WHEN TYPE_NAME(c.user_type_id) IN ('decimal','numeric') THEN '(' + CAST(c.precision AS varchar(10)) + ',' + CAST(c.scale AS varchar(10)) + ')'
                     ELSE '' END,
                   c.is_nullable,
                   CASE WHEN pk.column_id IS NOT NULL THEN 'PRI' ELSE '' END,
                   OBJECT_DEFINITION(c.default_object_id),
                   CASE WHEN c.is_identity = 1 THEN 'identity' WHEN c.is_computed = 1 THEN 'computed' ELSE '' END,
                   CAST(ep.value AS nvarchar(500))
            FROM sys.columns c
            LEFT JOIN (SELECT ic.object_id, ic.column_id FROM sys.index_columns ic
                       JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id WHERE i.is_primary_key = 1) pk
                   ON pk.object_id = c.object_id AND pk.column_id = c.column_id
            LEFT JOIN sys.extended_properties ep ON ep.class = 1 AND ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = 'MS_Description'
            WHERE c.object_id = OBJECT_ID(@obj) ORDER BY c.column_id
            """, c.TimeoutSeconds, ct, p);
        if (cols.Count == 0) throw new DbToolException($"Table or view '{schema}.{name}' was not found.");

        var fks = await RowsAsync(conn,
            """
            SELECT fk.name, pc.name, rs.name + '.' + rt.name, rc.name
            FROM sys.foreign_key_columns fkc
            JOIN sys.foreign_keys fk ON fk.object_id = fkc.constraint_object_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.tables rt ON rt.object_id = fkc.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE fkc.parent_object_id = OBJECT_ID(@obj) ORDER BY fk.name, fkc.constraint_column_id
            """, c.TimeoutSeconds, ct, p);

        var idx = await RowsAsync(conn,
            """
            SELECT i.name, CASE WHEN i.is_unique = 1 THEN 0 ELSE 1 END, STRING_AGG(c.name, ', ') WITHIN GROUP (ORDER BY ic.key_ordinal)
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(@obj) AND i.name IS NOT NULL
            GROUP BY i.name, i.is_unique ORDER BY i.name
            """, c.TimeoutSeconds, ct, p);

        return new TableDescription(
            $"{schema}.{name}",
            [.. cols.Select(r => new ColumnInfo(Str(r[0]) ?? "", Str(r[1]) ?? "", r[2] is true,
                NullIfEmpty(Str(r[3])), Str(r[4]), NullIfEmpty(Str(r[5])), NullIfEmpty(Str(r[6]))))],
            [.. fks.Select(r => new ForeignKeyInfo(Str(r[0]) ?? "", Str(r[1]) ?? "", Str(r[2]) ?? "", Str(r[3]) ?? ""))],
            [.. idx.Select(r => new IndexInfo(Str(r[0]) ?? "", Long(r[1]) == 0, Str(r[2]) ?? ""))]);
    }

    public async Task<SchemaMatch[]> SearchSchemaAsync(string connection, string text, string? database, CancellationToken ct)
    {
        var c = Resolve(connection);
        if (string.IsNullOrWhiteSpace(text)) throw new DbToolException("Give some text to search for.");
        var like = "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[") + "%";

        await using var conn = await OpenAsync(c, c.Provider == DbProvider.SqlServer ? database : null, ct);
        List<object?[]> rows;
        if (c.Provider == DbProvider.MySql)
        {
            rows = await RowsAsync(conn,
                """
                SELECT table_name, column_name, column_type FROM information_schema.columns
                WHERE table_schema = COALESCE(@db, DATABASE()) AND (table_name LIKE @p OR column_name LIKE @p)
                ORDER BY table_name, ordinal_position LIMIT 200
                """, c.TimeoutSeconds, ct, ("@db", Blank(database)), ("@p", like));
        }
        else
        {
            rows = await RowsAsync(conn,
                """
                SELECT TOP 200 s.name + '.' + o.name, c.name, TYPE_NAME(c.user_type_id)
                FROM sys.columns c
                JOIN sys.objects o ON o.object_id = c.object_id AND o.type IN ('U','V')
                JOIN sys.schemas s ON s.schema_id = o.schema_id
                WHERE o.name LIKE @p ESCAPE '\' OR c.name LIKE @p ESCAPE '\'
                ORDER BY 1, c.column_id
                """, c.TimeoutSeconds, ct, ("@p", like));
        }
        return [.. rows.Select(r => new SchemaMatch(Str(r[0]) ?? "", Str(r[1]) ?? "", Str(r[2]) ?? ""))];
    }

    // ---- plumbing ---------------------------------------------------------------------------------------------

    private ConnectionEntry Resolve(string name)
    {
        var c = store.Find(name);
        if (c is null)
        {
            var names = store.Get().Connections.Select(x => x.Name).ToList();
            throw new DbToolException($"Unknown connection '{name}'. " + (names.Count == 0 ? "No connections are saved yet; add one on the DbMcp home page." : $"Available: {string.Join(", ", names)}."));
        }
        if (string.IsNullOrWhiteSpace(c.ConnectionString))
            throw new DbToolException($"Connection '{c.Name}' has no connection string saved. Re-enter it on the DbMcp home page.");
        return c;
    }

    private static DbConnection Create(ConnectionEntry c) =>
        c.Provider == DbProvider.MySql ? new MySqlConnection(c.ConnectionString) : new SqlConnection(c.ConnectionString);

    private static async Task<DbConnection> OpenAsync(ConnectionEntry c, string? database, CancellationToken ct)
    {
        var conn = Create(c);
        try
        {
            await conn.OpenAsync(ct);
            if (!string.IsNullOrWhiteSpace(database)) await conn.ChangeDatabaseAsync(database.Trim(), ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private static async Task<List<object?[]>> RowsAsync(DbConnection conn, string sql, int timeout, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = timeout;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        var rows = new List<object?[]>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static async Task ExecAsync(DbConnection conn, string sql, int timeout, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = timeout;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task TryExecAsync(DbConnection conn, string sql, int timeout)
    {
        try { await ExecAsync(conn, sql, timeout, CancellationToken.None); }
        catch (Exception) { /* the connection is being thrown away anyway */ }
    }

    private static object? Cell(object v) => v switch
    {
        DBNull => null,
        string s => s.Length > MaxCellChars ? s[..MaxCellChars] + $"... [{s.Length} chars]" : s,
        byte[] b => "0x" + Convert.ToHexString(b.AsSpan(0, Math.Min(b.Length, 16))) + (b.Length > 16 ? $"... [{b.Length} bytes]" : ""),
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd") : d.ToString("yyyy-MM-dd HH:mm:ss"),
        DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm:ss zzz"),
        double x when double.IsNaN(x) || double.IsInfinity(x) => x.ToString(),
        float x when float.IsNaN(x) || float.IsInfinity(x) => x.ToString(),
        bool or sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => v,
        _ => v.ToString(),
    };

    private static object Blank(string? s) => string.IsNullOrWhiteSpace(s) ? DBNull.Value : s.Trim();

    private static string? Str(object? v) => v switch
    {
        null or DBNull => null,
        byte[] b => Encoding.UTF8.GetString(b),
        _ => v.ToString(),
    };

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static long? Long(object? v) => v is null or DBNull ? null : Convert.ToInt64(v);

    private static double? Dbl(object? v) => v is null or DBNull ? null : Convert.ToDouble(v);
}
