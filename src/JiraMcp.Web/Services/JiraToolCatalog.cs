using System.Reflection;
using System.Text.Json;
using Anthropic.Models.Messages;
using JiraMcp.Web.Tools;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace JiraMcp.Web.Services;

public sealed record ToolEntry(AIFunction Function, bool IsWrite);

/// <summary>
/// Exposes the [McpServerTool] methods of <see cref="JiraTools"/> to the in-app chat as Claude tool definitions,
/// so the MCP endpoint and the chat page share one implementation and one set of descriptions.
/// </summary>
public sealed class JiraToolCatalog
{
    private readonly Dictionary<string, ToolEntry> _byName = new();

    public JiraToolCatalog(JiraClient jira)
    {
        var target = new JiraTools(jira);
        foreach (var method in typeof(JiraTools).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var attr = method.GetCustomAttribute<McpServerToolAttribute>();
            if (attr?.Name is null) continue;

            var fn = AIFunctionFactory.Create(method, target, new AIFunctionFactoryOptions { Name = attr.Name });
            _byName[attr.Name] = new ToolEntry(fn, IsWrite: !attr.ReadOnly);
        }

        ClaudeTools = [.. _byName.Values.Select(e => ToClaudeTool(e.Function))];
    }

    public IReadOnlyList<ToolUnion> ClaudeTools { get; }

    public ToolEntry? Find(string name) => _byName.GetValueOrDefault(name);

    private static ToolUnion ToClaudeTool(AIFunction fn)
    {
        var schema = fn.JsonSchema;
        var props = new Dictionary<string, JsonElement>();
        if (schema.TryGetProperty("properties", out var p))
            foreach (var prop in p.EnumerateObject()) props[prop.Name] = prop.Value.Clone();

        List<string> required = [];
        if (schema.TryGetProperty("required", out var r))
            required = [.. r.EnumerateArray().Select(x => x.GetString()!)];

        return new Tool
        {
            Name = fn.Name,
            Description = fn.Description,
            InputSchema = new() { Properties = props, Required = required },
        };
    }
}
