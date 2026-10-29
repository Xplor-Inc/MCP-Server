# DB MCP

Blazor Web App (.NET 8) that hosts an MCP server at `/mcp` so GitHub Copilot (VS Code agent mode) can explore and query MySQL / MariaDB and SQL Server databases, plus a `/chat` page that does the same with Claude.

## Run
    dotnet run --project src/DbMcp.Web

Open http://localhost:5300, add a connection (or import `ConnectionStrings` from an appsettings.json), click **Test**, then copy the `mcp.json` snippet into `.vscode/mcp.json`.

## Tools
db_list_connections, db_list_databases, db_list_tables, db_describe_table, db_search_schema, db_query (read-only), db_explain, db_execute (writes).

## Safety
- Connections are **read-only** unless "Allow writes" is on for that connection; schema changes (CREATE/ALTER/DROP TABLE/TRUNCATE) need a second switch.
- `db_query` accepts one SELECT-like statement; comments and string contents are ignored when checking it, and it runs in a transaction that is always rolled back (READ ONLY on MySQL).
- `db_execute`: one INSERT/UPDATE/DELETE; UPDATE/DELETE need a WHERE; `dryRun` runs and rolls back; DROP DATABASE/USER, GRANT, EXEC etc. are never allowed. The chat page asks for approval first.
- Results are capped (rows, cell length, total size). Connection strings are stored encrypted in `App_Data` and never returned by any tool.
- Only loopback requests are served, and `/mcp` needs the bearer key.
- These checks are a safety net: for a hard guarantee connect with a database login that only has SELECT permission.

Tests: `dotnet test tests/DbMcp.Tests` (SQL guard).
