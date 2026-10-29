using System.Text;
using System.Text.Json.Nodes;

namespace JiraMcp.Web.Services;

/// <summary>Minimal plain text &lt;-&gt; Atlassian Document Format conversion (Jira Cloud REST v3 uses ADF for rich text).</summary>
public static class AdfConverter
{
    public static JsonObject ToAdf(string text)
    {
        var content = new JsonArray();
        var paragraphs = text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in paragraphs)
        {
            var inline = new JsonArray();
            var lines = p.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) inline.Add(new JsonObject { ["type"] = "hardBreak" });
                if (lines[i].Length > 0)
                    inline.Add(new JsonObject { ["type"] = "text", ["text"] = lines[i] });
            }
            content.Add(new JsonObject { ["type"] = "paragraph", ["content"] = inline });
        }
        return new JsonObject { ["type"] = "doc", ["version"] = 1, ["content"] = content };
    }

    public static string ToText(JsonNode? node)
    {
        if (node is null) return "";
        if (node is JsonValue v) return v.TryGetValue<string>(out var s) ? s : "";
        var sb = new StringBuilder();
        Append(node, sb);
        return sb.ToString().Trim();
    }

    private static void Append(JsonNode? node, StringBuilder sb)
    {
        if (node is not JsonObject o) return;
        var type = (string?)o["type"];
        switch (type)
        {
            case "text": sb.Append((string?)o["text"]); return;
            case "hardBreak": sb.Append('\n'); return;
            case "mention": sb.Append((string?)o["attrs"]?["text"]); return;
            case "emoji": sb.Append((string?)o["attrs"]?["text"] ?? (string?)o["attrs"]?["shortName"]); return;
            case "inlineCard": sb.Append((string?)o["attrs"]?["url"]); return;
            case "listItem": sb.Append("- "); break;
        }

        if (o["content"] is JsonArray children)
            foreach (var c in children) Append(c, sb);

        if (type is "paragraph" or "heading" or "listItem" or "codeBlock" or "blockquote" or "rule")
            sb.Append('\n');
    }
}
