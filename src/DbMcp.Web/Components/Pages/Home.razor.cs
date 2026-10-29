using System.Data.Common;
using System.Text.Json;
using DbMcp.Web.Services;
using Microsoft.AspNetCore.Components;

namespace DbMcp.Web.Components.Pages;

public partial class Home
{
    private sealed class ConnectionForm
    {
        public string OriginalName { get; set; } = "";
        public string Name { get; set; } = "";
        public DbProvider Provider { get; set; }
        public string ConnectionString { get; set; } = "";
        public bool AllowWrites { get; set; }
        public bool AllowSchemaChanges { get; set; }
        public int MaxRows { get; set; } = 200;
        public int TimeoutSeconds { get; set; } = 30;
    }

    [Inject] private DbSettingsStore Store { get; set; } = default!;
    [Inject] private DbEngine Engine { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;

    private List<ConnectionEntry> _connections = [];
    private ConnectionForm _form = new();
    private bool _editing;
    private string? _message;
    private bool _ok;
    private string? _testing;
    private string? _confirmRemove;

    private string _importPath = "";
    private DbProvider _importProvider;
    private string? _importMessage;
    private bool _importOk;

    private string _anthropicKey = "";
    private string _claudeModel = DbSettings.DefaultClaudeModel;
    private string? _aiMessage;
    private string _mcpKey = "";

    private string McpUrl => Nav.BaseUri.TrimEnd('/') + "/mcp";

    private string ExampleConnectionString => _form.Provider == DbProvider.MySql
        ? "Server=localhost;Port=3306;Database=mydb;Uid=user;Pwd=secret;"
        : "Server=(localdb)\\MSSQLLocalDB;Database=mydb;Integrated Security=true;TrustServerCertificate=true;";

    private string ConfigSnippet => $$"""
        {
          "servers": {
            "database": {
              "type": "http",
              "url": "{{McpUrl}}",
              "headers": { "Authorization": "Bearer {{_mcpKey}}" }
            }
          }
        }
        """;

    protected override void OnInitialized() => Reload();

    private void Reload()
    {
        var s = Store.Get();
        _connections = s.Connections;
        _anthropicKey = s.AnthropicApiKey;
        _claudeModel = s.ClaudeModel;
        _mcpKey = s.McpKey;
    }

    private void Edit(ConnectionEntry c)
    {
        _form = new ConnectionForm
        {
            OriginalName = c.Name, Name = c.Name, Provider = c.Provider, ConnectionString = "",
            AllowWrites = c.AllowWrites, AllowSchemaChanges = c.AllowSchemaChanges, MaxRows = c.MaxRows, TimeoutSeconds = c.TimeoutSeconds
        };
        _editing = true;
        _message = null;
    }

    private void CancelEdit()
    {
        _form = new ConnectionForm();
        _editing = false;
    }

    private void Save()
    {
        var name = _form.Name.Trim();
        if (!DbSettingsStore.NamePattern.IsMatch(name))
        {
            Fail("The name must be 1-40 characters: letters, digits, - _ .");
            return;
        }
        if (_connections.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !c.Name.Equals(_form.OriginalName, StringComparison.OrdinalIgnoreCase)))
        {
            Fail($"There is already a connection called '{name}'.");
            return;
        }

        var cs = _form.ConnectionString.Trim();
        if (cs.Length == 0 && !_editing)
        {
            Fail("Enter a connection string.");
            return;
        }
        if (cs.Length > 0)
        {
            try { _ = new DbConnectionStringBuilder { ConnectionString = cs }; }
            catch (ArgumentException ex)
            {
                Fail($"That connection string is not valid: {ex.Message}");
                return;
            }
        }

        var maxRows = Math.Clamp(_form.MaxRows, 1, 1000);
        var timeout = Math.Clamp(_form.TimeoutSeconds, 1, 600);
        Store.Update(s =>
        {
            var existing = s.Connections.FirstOrDefault(c => c.Name.Equals(_form.OriginalName, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new ConnectionEntry();
                s.Connections.Add(existing);
            }
            existing.Name = name;
            existing.Provider = _form.Provider;
            if (cs.Length > 0) existing.ConnectionString = cs;
            existing.AllowWrites = _form.AllowWrites;
            existing.AllowSchemaChanges = _form.AllowWrites && _form.AllowSchemaChanges;
            existing.MaxRows = maxRows;
            existing.TimeoutSeconds = timeout;
        });

        (_ok, _message) = (true, $"Saved '{name}'. Use Test to check it connects.");
        _form = new ConnectionForm { Provider = _form.Provider };
        _editing = false;
        Reload();

        void Fail(string text) => (_ok, _message) = (false, text);
    }

    private async Task TestAsync(string name)
    {
        _testing = name;
        try
        {
            var entry = Store.Find(name) ?? throw new DbToolException("Connection not found.");
            (_ok, _message) = (true, $"{name}: {await Engine.TestAsync(entry)}");
        }
        catch (Exception ex) when (ex is DbException or DbToolException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            (_ok, _message) = (false, $"{name}: {ex.Message}");
        }
        finally
        {
            _testing = null;
        }
    }

    private void Remove(string name)
    {
        Store.Update(s => s.Connections.RemoveAll(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        _confirmRemove = null;
        (_ok, _message) = (true, $"Removed '{name}'.");
        Reload();
    }

    private void Import()
    {
        try
        {
            var path = _importPath.Trim().Trim('"');
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            {
                (_importOk, _importMessage) = (false, "Give the full path of an existing .json file.");
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("ConnectionStrings", out var section) || section.ValueKind != JsonValueKind.Object)
            {
                (_importOk, _importMessage) = (false, "No ConnectionStrings section found in that file.");
                return;
            }

            var added = new List<string>();
            var skipped = new List<string>();
            Store.Update(s =>
            {
                foreach (var prop in section.EnumerateObject())
                {
                    var value = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
                    var name = new string(prop.Name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_').ToArray());
                    name = name.Length > 40 ? name[..40] : name;
                    if (string.IsNullOrWhiteSpace(value) || name.Length == 0
                        || s.Connections.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        skipped.Add(prop.Name);
                        continue;
                    }
                    s.Connections.Add(new ConnectionEntry { Name = name, Provider = _importProvider, ConnectionString = value });
                    added.Add(name);
                }
            });

            _importOk = added.Count > 0;
            _importMessage = (added.Count > 0 ? $"Imported {added.Count}: {string.Join(", ", added)} (read-only)." : "Nothing imported.")
                + (skipped.Count > 0 ? $" Skipped (empty or name already used): {string.Join(", ", skipped)}." : "");
            Reload();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            (_importOk, _importMessage) = (false, $"Could not read that file: {ex.Message}");
        }
    }

    private void SaveAi()
    {
        Store.Update(s =>
        {
            s.AnthropicApiKey = _anthropicKey;
            s.ClaudeModel = _claudeModel;
        });
        _aiMessage = "Saved.";
        Reload();
    }

    private void RegenerateKey()
    {
        Store.Update(s => s.McpKey = DbSettingsStore.NewMcpKey());
        (_ok, _message) = (true, "New MCP key generated and saved. Update mcp.json.");
        Reload();
    }
}
