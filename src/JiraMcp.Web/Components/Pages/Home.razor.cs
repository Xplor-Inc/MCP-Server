using JiraMcp.Web.Services;
using Microsoft.AspNetCore.Components;

namespace JiraMcp.Web.Components.Pages;

public partial class Home
{
    [Inject] private JiraSettingsStore Store { get; set; } = default!;
    [Inject] private JiraClient Jira { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;

    private JiraSettings _settings = new();
    private string? _message;
    private bool _ok;
    private bool _testing;

    private string McpUrl => Nav.BaseUri.TrimEnd('/') + "/mcp";

    private string ConfigSnippet => $$"""
        {
          "servers": {
            "jira": {
              "type": "http",
              "url": "{{McpUrl}}",
              "headers": { "Authorization": "Bearer {{_settings.McpKey}}" }
            }
          }
        }
        """;

    protected override void OnInitialized() => _settings = Store.Get();

    private Task SaveAsync()
    {
        Store.Save(_settings);
        _settings = Store.Get();
        (_ok, _message) = (true, "Saved.");
        return Task.CompletedTask;
    }

    private async Task TestAsync()
    {
        _testing = true;
        Store.Save(_settings);
        try
        {
            var me = await Jira.GetMyselfAsync();
            var sp = await Jira.GetStoryPointsFieldAsync();
            (_ok, _message) = (true, $"Connected as {me.DisplayName}. Story points field: {sp ?? "not found (set it above)"}.");
        }
        catch (JiraException ex)
        {
            (_ok, _message) = (false, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            (_ok, _message) = (false, $"Could not reach Jira: {ex.Message}");
        }
        finally
        {
            _testing = false;
        }
    }

    private Task RegenerateKeyAsync()
    {
        _settings.McpKey = JiraSettingsStore.NewMcpKey();
        Store.Save(_settings);
        (_ok, _message) = (true, "New MCP key generated and saved.");
        return Task.CompletedTask;
    }
}
