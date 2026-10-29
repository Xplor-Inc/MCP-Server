# MCP Servers

Two Blazor Web Apps (.NET 8) that each host an [MCP](https://modelcontextprotocol.io) server for GitHub Copilot (VS Code agent mode) and also include a chat page powered by Claude.

| App | What it does | Port | Docs |
|---|---|---|---|
| **JiraMcp.Web** | Jira Cloud: issues, sprints and boards, epics, assigning, links, work logs | 5289 | [src/JiraMcp.Web/README.md](src/JiraMcp.Web/README.md) |
| **DbMcp.Web** | MySQL / MariaDB and SQL Server: browse schema, run read-only queries (joins included), guarded writes | 5300 | [src/DbMcp.Web/README.md](src/DbMcp.Web/README.md) |

## Run

```
dotnet run --project src/JiraMcp.Web --urls http://localhost:5289
dotnet run --project src/DbMcp.Web   --urls http://localhost:5300
```

Open the app in a browser, fill in the connection details on its home page, then copy the generated `mcp.json` snippet into `.vscode/mcp.json`.

## Secrets

Nothing sensitive is stored in this repository. Jira tokens, database connection strings, Anthropic keys and the MCP access keys are entered in each app's UI and saved encrypted in a local `App_Data` folder (git-ignored). `.vscode/mcp.json` is git-ignored too, because it contains the MCP access key.

## Tests

```
dotnet test
```

The tests cover the SQL safety checks that keep database queries read-only.
