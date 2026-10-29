using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

namespace DbMcp.Web.Services;

public enum DbProvider { MySql, SqlServer }

public sealed class ConnectionEntry
{
    public string Name { get; set; } = "";
    public DbProvider Provider { get; set; }
    public string ConnectionString { get; set; } = "";

    /// <summary>Allows INSERT / UPDATE / DELETE through db_execute. Off by default.</summary>
    public bool AllowWrites { get; set; }

    /// <summary>Also allows CREATE / ALTER / DROP TABLE / TRUNCATE. Only meaningful with AllowWrites.</summary>
    public bool AllowSchemaChanges { get; set; }

    public int MaxRows { get; set; } = 200;
    public int TimeoutSeconds { get; set; } = 30;

    public ConnectionEntry Clone() => (ConnectionEntry)MemberwiseClone();

    /// <summary>Server and database taken from the connection string, never the credentials.</summary>
    public (string? Server, string? Database) Describe()
    {
        try
        {
            var b = new DbConnectionStringBuilder { ConnectionString = ConnectionString };
            return (First(b, "server", "data source", "host", "address", "addr", "network address"),
                    First(b, "database", "initial catalog"));
        }
        catch (ArgumentException)
        {
            return (null, null);
        }
    }

    /// <summary>Connection string with any password value replaced, safe to show on screen.</summary>
    public string Masked()
    {
        try
        {
            var b = new DbConnectionStringBuilder { ConnectionString = ConnectionString };
            foreach (string key in b.Keys.Cast<string>().ToList())
                if (key.Equals("password", StringComparison.OrdinalIgnoreCase) || key.Equals("pwd", StringComparison.OrdinalIgnoreCase))
                    b[key] = "********";
            return b.ConnectionString;
        }
        catch (ArgumentException)
        {
            return "(unreadable connection string)";
        }
    }

    private static string? First(DbConnectionStringBuilder b, params string[] keys)
    {
        foreach (var k in keys)
            if (b.TryGetValue(k, out var v) && v is not null && v.ToString() is { Length: > 0 } s) return s;
        return null;
    }
}

public sealed class DbSettings
{
    public const string DefaultClaudeModel = "claude-opus-5-5";

    public string McpKey { get; set; } = "";
    public string AnthropicApiKey { get; set; } = "";
    public string ClaudeModel { get; set; } = DefaultClaudeModel;
    public List<ConnectionEntry> Connections { get; set; } = [];
}

/// <summary>Persists settings to App_Data/db-settings.json. Connection strings and the Anthropic key are encrypted with ASP.NET Data Protection.</summary>
public sealed class DbSettingsStore
{
    public static readonly Regex NamePattern = new(@"^[A-Za-z0-9_.-]{1,40}$", RegexOptions.Compiled);

    private sealed record FileConnection(
        string Name, DbProvider Provider, string ProtectedConnectionString, bool AllowWrites, bool AllowSchemaChanges, int MaxRows, int TimeoutSeconds);

    private sealed record FileModel(string McpKey, string? ProtectedAnthropicKey, string? ClaudeModel, List<FileConnection>? Connections);

    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly object _gate = new();
    private DbSettings _current;

    public DbSettingsStore(IWebHostEnvironment env, IDataProtectionProvider dataProtection)
    {
        var dir = Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "db-settings.json");
        _protector = dataProtection.CreateProtector("DbMcp.Secrets");
        _current = Load();
        if (!File.Exists(_path)) Persist(); // persist the generated MCP key so it survives restarts
    }

    public DbSettings Get()
    {
        lock (_gate) return Clone(_current);
    }

    public ConnectionEntry? Find(string name)
    {
        lock (_gate)
            return _current.Connections.FirstOrDefault(c => c.Name.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase))?.Clone();
    }

    /// <summary>Applies a change under the lock and saves it.</summary>
    public void Update(Action<DbSettings> change)
    {
        lock (_gate)
        {
            change(_current);
            _current.AnthropicApiKey = _current.AnthropicApiKey.Trim();
            if (string.IsNullOrWhiteSpace(_current.ClaudeModel)) _current.ClaudeModel = DbSettings.DefaultClaudeModel;
            Persist();
        }
    }

    public static string NewMcpKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    private void Persist()
    {
        var model = new FileModel(
            _current.McpKey, Protect(_current.AnthropicApiKey), _current.ClaudeModel,
            [.. _current.Connections.Select(c => new FileConnection(
                c.Name, c.Provider, Protect(c.ConnectionString), c.AllowWrites, c.AllowSchemaChanges, c.MaxRows, c.TimeoutSeconds))]);
        File.WriteAllText(_path, JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
    }

    private string Protect(string value) => string.IsNullOrEmpty(value) ? "" : _protector.Protect(value);

    private string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try { return _protector.Unprotect(value); }
        catch (CryptographicException) { return ""; } // key ring lost (e.g. moved machine): the user re-enters the secret
    }

    private DbSettings Load()
    {
        if (!File.Exists(_path)) return new DbSettings { McpKey = NewMcpKey() };
        try
        {
            var m = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path));
            if (m is null) return new DbSettings { McpKey = NewMcpKey() };
            return new DbSettings
            {
                McpKey = string.IsNullOrEmpty(m.McpKey) ? NewMcpKey() : m.McpKey,
                AnthropicApiKey = Unprotect(m.ProtectedAnthropicKey),
                ClaudeModel = string.IsNullOrWhiteSpace(m.ClaudeModel) ? DbSettings.DefaultClaudeModel : m.ClaudeModel,
                Connections = [.. (m.Connections ?? []).Select(c => new ConnectionEntry
                {
                    Name = c.Name, Provider = c.Provider, ConnectionString = Unprotect(c.ProtectedConnectionString),
                    AllowWrites = c.AllowWrites, AllowSchemaChanges = c.AllowSchemaChanges,
                    MaxRows = c.MaxRows, TimeoutSeconds = c.TimeoutSeconds
                })]
            };
        }
        catch (JsonException)
        {
            return new DbSettings { McpKey = NewMcpKey() };
        }
    }

    private static DbSettings Clone(DbSettings s) => new()
    {
        McpKey = s.McpKey, AnthropicApiKey = s.AnthropicApiKey, ClaudeModel = s.ClaudeModel,
        Connections = [.. s.Connections.Select(c => c.Clone())]
    };
}
