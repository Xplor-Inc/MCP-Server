using System.ComponentModel;
using System.Data.Common;
using DbMcp.Web.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DbMcp.Web.Tools;

[McpServerToolType]
public sealed class DbTools(DbEngine db)
{
    [McpServerTool(Name = "db_list_connections", ReadOnly = true),
     Description("List the saved database connections (name, engine, server, database, and whether writes are allowed). Always start here. Credentials are never shown.")]
    public IReadOnlyList<ConnectionSummary> ListConnections() => db.ListConnections();

    [McpServerTool(Name = "db_list_databases", ReadOnly = true), Description("List the databases available on a connection's server.")]
    public Task<string[]> ListDatabases([Description("Connection name from db_list_connections.")] string connection, CancellationToken ct = default)
        => Run(() => db.ListDatabasesAsync(connection, ct));

    [McpServerTool(Name = "db_list_tables", ReadOnly = true),
     Description("List tables and views with estimated row counts and size in MB. Uses the connection's default database unless 'database' is given.")]
    public Task<TableInfo[]> ListTables(
        [Description("Connection name.")] string connection,
        [Description("Database to look in; omit for the connection's own database.")] string? database = null,
        CancellationToken ct = default)
        => Run(() => db.ListTablesAsync(connection, database, ct));

    [McpServerTool(Name = "db_describe_table", ReadOnly = true),
     Description("Columns (type, nullable, key, default), foreign keys and indexes of one table. Look at this before writing a query; do not guess column names.")]
    public Task<TableDescription> DescribeTable(
        [Description("Connection name.")] string connection,
        [Description("Table name. MySQL: 'table' or 'database.table'. SQL Server: 'table' or 'schema.table'.")] string table,
        [Description("Database, if not the connection's default.")] string? database = null,
        CancellationToken ct = default)
        => Run(() => db.DescribeTableAsync(connection, table, database, ct));

    [McpServerTool(Name = "db_search_schema", ReadOnly = true),
     Description("Find tables or columns whose name contains some text, e.g. 'invoice' or 'email'. Useful in a large database when you do not know where something lives.")]
    public Task<SchemaMatch[]> SearchSchema(
        [Description("Connection name.")] string connection,
        [Description("Text to look for in table and column names.")] string text,
        [Description("Database, if not the connection's default.")] string? database = null,
        CancellationToken ct = default)
        => Run(() => db.SearchSchemaAsync(connection, text, database, ct));

    [McpServerTool(Name = "db_query", ReadOnly = true),
     Description("Run ONE read-only SQL statement (SELECT / WITH, plus SHOW / DESCRIBE / EXPLAIN on MySQL) and return rows. Nothing is changed. Results are capped, so aggregate, filter, and use LIMIT instead of pulling whole tables.")]
    public Task<QueryResult> Query(
        [Description("Connection name.")] string connection,
        [Description("A single SELECT statement in the connection's SQL dialect (MySQL or T-SQL).")] string sql,
        [Description("Maximum rows to return; defaults to the connection's limit, at most 1000.")] int? maxRows = null,
        CancellationToken ct = default)
        => Run(() => db.QueryAsync(connection, sql, maxRows, ct));

    [McpServerTool(Name = "db_explain", ReadOnly = true), Description("Show the execution plan of a SELECT without running it (MySQL EXPLAIN, SQL Server SHOWPLAN). Use for slow queries and missing indexes.")]
    public Task<QueryResult> Explain(
        [Description("Connection name.")] string connection,
        [Description("The SELECT statement to explain.")] string sql,
        CancellationToken ct = default)
        => Run(() => db.ExplainAsync(connection, sql, ct));

    [McpServerTool(Name = "db_execute", Destructive = true),
     Description("CHANGE data with ONE INSERT / UPDATE / DELETE statement (schema changes only if enabled for the connection). Works only on connections with 'Allow writes' on. UPDATE and DELETE need a WHERE clause. Use dryRun=true first to see how many rows would be affected without changing anything.")]
    public Task<ExecResult> Execute(
        [Description("Connection name.")] string connection,
        [Description("A single INSERT, UPDATE or DELETE statement.")] string sql,
        [Description("When true, run it and roll back, reporting the row count only.")] bool dryRun = false,
        CancellationToken ct = default)
        => Run(() => db.ExecuteAsync(connection, sql, dryRun, ct));

    // The SDK hides arbitrary exception text from the model; McpException messages are passed through.
    private static async Task<T> Run<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (DbToolException ex) { throw new McpException(ex.Message, ex); }
        catch (DbException ex) { throw new McpException($"Database error: {ex.Message}", ex); }
        catch (TimeoutException ex) { throw new McpException($"Timed out: {ex.Message}", ex); }
    }
}
