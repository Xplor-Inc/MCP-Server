using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace JiraMcp.Web.Services;

public sealed class JiraException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

public sealed record JiraUser(string AccountId, string DisplayName, string? Email);

public sealed record JiraIssueSummary(
    string Key, string Summary, string Status, string? IssueType, string? Priority,
    string? Assignee, string? Reporter, double? StoryPoints, string Created, string Updated, string Url, string? StatusCategory = null);

public sealed record JiraIssueDetail(JiraIssueSummary Summary, string Description, string[] Labels, string? Parent, JiraComment[] RecentComments);

public sealed record JiraComment(string Author, string Created, string Body);

public sealed record JiraTransition(string Id, string Name, string ToStatus);

public sealed record JiraSearchResult(int Returned, bool HasMore, JiraIssueSummary[] Issues);

/// <summary>Thin Jira Cloud REST v3 client. Credentials are read from <see cref="JiraSettingsStore"/> on every call so UI edits apply immediately.</summary>
public sealed partial class JiraClient(IHttpClientFactory httpFactory, JiraSettingsStore store)
{
    private string? _storyPointsField;
    private string? _storyPointsFieldFor;

    public bool AllowWrites => store.Get().AllowWrites;

    public async Task<JiraUser> GetMyselfAsync(CancellationToken ct = default)
    {
        var me = await SendAsync(HttpMethod.Get, "/rest/api/3/myself", null, ct);
        return new JiraUser((string)me!["accountId"]!, (string?)me["displayName"] ?? "", (string?)me["emailAddress"]);
    }

    public async Task<JiraSearchResult> SearchAsync(string jql, int maxResults, CancellationToken ct = default)
    {
        maxResults = Math.Clamp(maxResults, 1, 100);
        var spField = await GetStoryPointsFieldAsync(ct);
        var fields = "summary,status,issuetype,priority,assignee,reporter,created,updated" + (spField is null ? "" : "," + spField);

        var issues = new List<JiraIssueSummary>();
        string? token = null;
        bool more;
        do
        {
            var url = $"/rest/api/3/search/jql?jql={Uri.EscapeDataString(jql)}&maxResults={maxResults - issues.Count}&fields={fields}";
            if (token is not null) url += "&nextPageToken=" + Uri.EscapeDataString(token);
            var page = await SendAsync(HttpMethod.Get, url, null, ct);
            foreach (var i in page!["issues"]!.AsArray()) issues.Add(ToSummary(i!, spField));
            token = (string?)page["nextPageToken"];
            more = token is not null;
        } while (more && issues.Count < maxResults);

        return new JiraSearchResult(issues.Count, more, [.. issues]);
    }

    public async Task<JiraIssueDetail> GetIssueAsync(string key, CancellationToken ct = default)
    {
        var spField = await GetStoryPointsFieldAsync(ct);
        var fields = "summary,description,status,issuetype,priority,assignee,reporter,created,updated,labels,parent,comment" + (spField is null ? "" : "," + spField);
        var issue = await SendAsync(HttpMethod.Get, $"/rest/api/3/issue/{Uri.EscapeDataString(key)}?fields={fields}", null, ct);
        var f = issue!["fields"]!;

        var comments = (f["comment"]?["comments"]?.AsArray() ?? [])
            .TakeLast(5)
            .Select(c => new JiraComment((string?)c!["author"]?["displayName"] ?? "", (string?)c["created"] ?? "", AdfConverter.ToText(c["body"])))
            .ToArray();

        return new JiraIssueDetail(
            ToSummary(issue, spField),
            AdfConverter.ToText(f["description"]),
            (f["labels"]?.AsArray() ?? []).Select(l => (string)l!).ToArray(),
            (string?)f["parent"]?["key"],
            comments);
    }

    public async Task UpdateIssueAsync(string key, string? summary, string? description, double? storyPoints, string[]? labels, CancellationToken ct = default)
    {
        EnsureWrites();
        var fields = new JsonObject();
        if (summary is not null) fields["summary"] = summary;
        if (description is not null) fields["description"] = description.Length == 0 ? null : AdfConverter.ToAdf(description);
        if (labels is not null) fields["labels"] = new JsonArray([.. labels.Select(l => (JsonNode)JsonValue.Create(l)!)]);
        if (storyPoints is not null)
        {
            var sp = await GetStoryPointsFieldAsync(ct)
                ?? throw new JiraException("No 'Story point estimate' / 'Story Points' field found. Set the field id in settings.");
            fields[sp] = storyPoints;
        }
        if (fields.Count == 0) throw new JiraException("Nothing to update: provide at least one field.");

        await SendAsync(HttpMethod.Put, $"/rest/api/3/issue/{Uri.EscapeDataString(key)}", new JsonObject { ["fields"] = fields }, ct);
    }

    public async Task AddCommentAsync(string key, string body, CancellationToken ct = default)
    {
        EnsureWrites();
        await SendAsync(HttpMethod.Post, $"/rest/api/3/issue/{Uri.EscapeDataString(key)}/comment",
            new JsonObject { ["body"] = AdfConverter.ToAdf(body) }, ct);
    }

    public async Task<JiraTransition[]> GetTransitionsAsync(string key, CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Get, $"/rest/api/3/issue/{Uri.EscapeDataString(key)}/transitions", null, ct);
        return r!["transitions"]!.AsArray()
            .Select(t => new JiraTransition((string)t!["id"]!, (string)t["name"]!, (string?)t["to"]?["name"] ?? ""))
            .ToArray();
    }

    /// <summary>Moves an issue to the given status/transition name (case-insensitive, matches transition name or target status).</summary>
    public async Task<string> TransitionAsync(string key, string statusOrTransition, CancellationToken ct = default)
    {
        EnsureWrites();
        var all = await GetTransitionsAsync(key, ct);
        var match = all.FirstOrDefault(t => t.Name.Equals(statusOrTransition, StringComparison.OrdinalIgnoreCase))
                 ?? all.FirstOrDefault(t => t.ToStatus.Equals(statusOrTransition, StringComparison.OrdinalIgnoreCase))
                 ?? throw new JiraException($"No transition '{statusOrTransition}' available for {key}. Available: {string.Join(", ", all.Select(t => $"{t.Name} -> {t.ToStatus}"))}");
        await SendAsync(HttpMethod.Post, $"/rest/api/3/issue/{Uri.EscapeDataString(key)}/transitions",
            new JsonObject { ["transition"] = new JsonObject { ["id"] = match.Id } }, ct);
        return match.ToStatus;
    }

    public async Task<string> CreateIssueAsync(string projectKey, string issueType, string summary, string? description, double? storyPoints, CancellationToken ct = default)
    {
        EnsureWrites();
        var fields = new JsonObject
        {
            ["project"] = new JsonObject { ["key"] = projectKey },
            ["issuetype"] = new JsonObject { ["name"] = issueType },
            ["summary"] = summary
        };
        if (!string.IsNullOrWhiteSpace(description)) fields["description"] = AdfConverter.ToAdf(description);
        if (storyPoints is not null)
        {
            var sp = await GetStoryPointsFieldAsync(ct);
            if (sp is not null) fields[sp] = storyPoints;
        }
        var r = await SendAsync(HttpMethod.Post, "/rest/api/3/issue", new JsonObject { ["fields"] = fields }, ct);
        return (string)r!["key"]!;
    }

    public async Task<string?> GetStoryPointsFieldAsync(CancellationToken ct = default)
    {
        var s = store.Get();
        if (!string.IsNullOrWhiteSpace(s.StoryPointsFieldId)) return s.StoryPointsFieldId;
        if (_storyPointsFieldFor == s.BaseUrl) return _storyPointsField;

        var fields = await SendAsync(HttpMethod.Get, "/rest/api/3/field", null, ct);
        string? Find(string name) => fields!.AsArray()
            .FirstOrDefault(f => string.Equals((string?)f!["name"], name, StringComparison.OrdinalIgnoreCase))?["id"]?.GetValue<string>();

        _storyPointsField = Find("Story point estimate") ?? Find("Story Points");
        _storyPointsFieldFor = s.BaseUrl;
        return _storyPointsField;
    }

    private void EnsureWrites()
    {
        if (!AllowWrites) throw new JiraException("Write operations are disabled in the JiraMcp settings page.");
    }

    private JiraIssueSummary ToSummary(JsonNode issue, string? spField)
    {
        var f = issue["fields"]!;
        return new JiraIssueSummary(
            (string)issue["key"]!,
            (string?)f["summary"] ?? "",
            (string?)f["status"]?["name"] ?? "",
            (string?)f["issuetype"]?["name"],
            (string?)f["priority"]?["name"],
            (string?)f["assignee"]?["displayName"],
            (string?)f["reporter"]?["displayName"],
            spField is null ? null : f[spField]?.GetValue<double>(),
            (string?)f["created"] ?? "",
            (string?)f["updated"] ?? "",
            $"{store.Get().BaseUrl}/browse/{(string)issue["key"]!}",
            (string?)f["status"]?["statusCategory"]?["key"]);
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        var s = store.Get();
        if (!s.IsConfigured) throw new JiraException("Jira is not configured. Open the JiraMcp web app and save the site URL, email and API token.");

        using var req = new HttpRequestMessage(method, s.BaseUrl + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{s.Email}:{s.ApiToken}")));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var res = await httpFactory.CreateClient("jira").SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new JiraException(Describe(res.StatusCode, text), res.StatusCode);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private static string Describe(HttpStatusCode status, string text)
    {
        string detail = "";
        try
        {
            if (JsonNode.Parse(text) is JsonObject o)
            {
                var msgs = (o["errorMessages"]?.AsArray() ?? []).Select(m => (string?)m).Where(m => !string.IsNullOrEmpty(m)).ToList();
                if (o["errors"] is JsonObject errs) msgs.AddRange(errs.Select(e => $"{e.Key}: {(string?)e.Value}"));
                detail = string.Join("; ", msgs);
            }
        }
        catch (System.Text.Json.JsonException) { }

        return status switch
        {
            HttpStatusCode.Unauthorized => "Jira rejected the credentials (401). Check email and API token.",
            HttpStatusCode.Forbidden => $"Jira denied access (403). {detail}".Trim(),
            HttpStatusCode.NotFound => $"Not found in Jira (404). {detail}".Trim(),
            _ => $"Jira returned {(int)status} {status}. {detail}".Trim()
        };
    }
}
