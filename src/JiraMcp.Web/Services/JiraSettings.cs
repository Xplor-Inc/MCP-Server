using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace JiraMcp.Web.Services;

public sealed class JiraSettings
{
    public const string DefaultClaudeModel = "claude-opus-5-5";

    /// <summary>Jira Cloud site, e.g. https://yourcompany.atlassian.net</summary>
    public string BaseUrl { get; set; } = "";
    public string Email { get; set; } = "";

    /// <summary>True for Jira Data Center / Server: Personal Access Token as Bearer, REST API v2, username-based users.</summary>
    public bool DataCenter { get; set; }
    public string ApiToken { get; set; } = "";

    /// <summary>Bearer key Copilot must send to /mcp.</summary>
    public string McpKey { get; set; } = "";

    /// <summary>When false, every tool that changes Jira is rejected.</summary>
    public bool AllowWrites { get; set; } = true;

    /// <summary>Optional override; auto-detected from the Jira field list when empty.</summary>
    public string StoryPointsFieldId { get; set; } = "";

    /// <summary>Anthropic API key used by the in-app chat page.</summary>
    public string AnthropicApiKey { get; set; } = "";
    public string ClaudeModel { get; set; } = DefaultClaudeModel;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && (DataCenter || !string.IsNullOrWhiteSpace(Email)) && !string.IsNullOrWhiteSpace(ApiToken);
}

/// <summary>Persists settings to App_Data/jira-settings.json; secrets are encrypted with ASP.NET Data Protection.</summary>
public sealed class JiraSettingsStore
{
    private sealed record FileModel(
        string BaseUrl, string Email, string ProtectedApiToken, string McpKey, bool AllowWrites, string StoryPointsFieldId,
        string? ProtectedAnthropicKey = null, string? ClaudeModel = null, bool DataCenter = false);

    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly object _gate = new();
    private JiraSettings _current;

    public JiraSettingsStore(IWebHostEnvironment env, IDataProtectionProvider dataProtection)
    {
        var dir = Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "jira-settings.json");
        _protector = dataProtection.CreateProtector("JiraMcp.ApiToken");
        _current = Load();
        if (!File.Exists(_path)) Save(_current); // persist the generated MCP key so it survives restarts
    }

    public JiraSettings Get()
    {
        lock (_gate) return Clone(_current);
    }

    public void Save(JiraSettings settings)
    {
        lock (_gate)
        {
            settings.BaseUrl = settings.BaseUrl.Trim().TrimEnd('/');
            settings.AnthropicApiKey = settings.AnthropicApiKey.Trim();
            if (string.IsNullOrWhiteSpace(settings.ClaudeModel)) settings.ClaudeModel = JiraSettings.DefaultClaudeModel;
            _current = Clone(settings);
            var model = new FileModel(
                settings.BaseUrl, settings.Email.Trim(), Protect(settings.ApiToken),
                settings.McpKey, settings.AllowWrites, settings.StoryPointsFieldId.Trim(),
                Protect(settings.AnthropicApiKey), settings.ClaudeModel.Trim(), settings.DataCenter);
            File.WriteAllText(_path, JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public static string NewMcpKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    private string Protect(string value) => string.IsNullOrEmpty(value) ? "" : _protector.Protect(value);

    private string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try { return _protector.Unprotect(value); }
        catch (CryptographicException) { return ""; } // keys lost (e.g. moved machine): user re-enters the secret
    }

    private JiraSettings Load()
    {
        if (!File.Exists(_path))
            return new JiraSettings { McpKey = NewMcpKey() };

        try
        {
            var m = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path));
            if (m is null) return new JiraSettings { McpKey = NewMcpKey() };
            return new JiraSettings
            {
                BaseUrl = m.BaseUrl ?? "", Email = m.Email ?? "", ApiToken = Unprotect(m.ProtectedApiToken),
                McpKey = string.IsNullOrEmpty(m.McpKey) ? NewMcpKey() : m.McpKey,
                DataCenter = m.DataCenter, AllowWrites = m.AllowWrites, StoryPointsFieldId = m.StoryPointsFieldId ?? "",
                AnthropicApiKey = Unprotect(m.ProtectedAnthropicKey),
                ClaudeModel = string.IsNullOrWhiteSpace(m.ClaudeModel) ? JiraSettings.DefaultClaudeModel : m.ClaudeModel
            };
        }
        catch (JsonException)
        {
            return new JiraSettings { McpKey = NewMcpKey() };
        }
    }

    private static JiraSettings Clone(JiraSettings s) => new()
    {
        BaseUrl = s.BaseUrl, Email = s.Email, DataCenter = s.DataCenter, ApiToken = s.ApiToken, McpKey = s.McpKey,
        AllowWrites = s.AllowWrites, StoryPointsFieldId = s.StoryPointsFieldId,
        AnthropicApiKey = s.AnthropicApiKey, ClaudeModel = s.ClaudeModel
    };
}
