using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace JiraMcp.Web.Services;

public sealed record JiraBoard(long Id, string Name, string Type, string? Project);

public sealed record JiraSprint(long Id, string Name, string State, string? Start, string? End, string? Goal);

public sealed record JiraSprintView(
    JiraSprint Sprint, int TotalIssues, int DoneIssues, double TotalPoints, double DonePoints,
    Dictionary<string, int> IssuesByStatus, JiraIssueSummary[] Issues, bool Truncated);

public sealed record JiraEpicView(
    JiraIssueSummary Epic, int TotalChildren, int DoneChildren, double TotalPoints, double DonePoints,
    JiraIssueSummary[] Children, bool Truncated);

public sealed record JiraBacklog(int Returned, bool Truncated, JiraIssueSummary[] Issues);

public sealed record JiraUserMatch(string AccountId, string DisplayName, string? Email);

public sealed record JiraLinkType(string Name, string Inward, string Outward);

public sealed record JiraWorklog(string Author, string Started, string TimeSpent, int Seconds, string Comment);

/// <summary>Boards/sprints (Jira Software agile API), assignment, issue links, work logs and epics.</summary>
public sealed partial class JiraClient
{
    private static readonly Regex KeyPattern = new(@"^[A-Za-z][A-Za-z0-9_]*-\d+$", RegexOptions.Compiled);
    private static readonly Regex AccountIdPattern = new(@"^([0-9a-f]{24}|\d+:[0-9a-f-]{36})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Which side Jira treats as the "from" issue when creating a link is learned once, then reused (see LinkAsync).
    private bool _linkSwapped;
    private bool _linkVerified;

    private static bool Eq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string RequireKey(string key)
        => KeyPattern.IsMatch(key.Trim()) ? key.Trim().ToUpperInvariant() : throw new JiraException($"'{key}' is not a valid issue key (expected like ABC-123).");

    private async Task<(string Fields, string? StoryPoints)> SummaryFieldsAsync(CancellationToken ct)
    {
        var sp = await GetStoryPointsFieldAsync(ct);
        return ("summary,status,issuetype,priority,assignee,reporter,created,updated" + (sp is null ? "" : "," + sp), sp);
    }

    // ---- boards & sprints -------------------------------------------------------------------------------------

    public async Task<JiraBoard[]> ListBoardsAsync(string? project, string? name, string? type, CancellationToken ct = default)
    {
        var q = new List<string> { "maxResults=50" };
        if (!string.IsNullOrWhiteSpace(project)) q.Add("projectKeyOrId=" + Uri.EscapeDataString(project.Trim()));
        if (!string.IsNullOrWhiteSpace(name)) q.Add("name=" + Uri.EscapeDataString(name.Trim()));
        if (!string.IsNullOrWhiteSpace(type)) q.Add("type=" + Uri.EscapeDataString(type.Trim().ToLowerInvariant()));

        var r = await SendAsync(HttpMethod.Get, "/rest/agile/1.0/board?" + string.Join("&", q), null, ct);
        return r!["values"]!.AsArray()
            .Select(b => new JiraBoard((long)b!["id"]!, (string?)b["name"] ?? "", (string?)b["type"] ?? "", (string?)b["location"]?["projectKey"]))
            .ToArray();
    }

    /// <param name="state">Comma separated: active, future, closed.</param>
    public async Task<JiraSprint[]> ListSprintsAsync(long boardId, string state, CancellationToken ct = default)
    {
        state = Regex.Replace(state ?? "", "[^a-z,]", "", RegexOptions.IgnoreCase);
        if (state.Length == 0) state = "active,future";

        var sprints = new List<JiraSprint>();
        var startAt = 0;
        while (true)
        {
            var r = await SendAsync(HttpMethod.Get, $"/rest/agile/1.0/board/{boardId}/sprint?state={state}&startAt={startAt}&maxResults=50", null, ct);
            var values = r!["values"]!.AsArray();
            sprints.AddRange(values.Select(v => ToSprint(v!)));
            startAt += values.Count;
            if (values.Count == 0 || (r["isLast"]?.GetValue<bool>() ?? true) || startAt >= 500) break;
        }
        // Jira lists oldest first; for a long history the recent ones are what people want.
        return [.. sprints.TakeLast(30)];
    }

    public async Task<JiraSprintView> GetSprintViewAsync(long? sprintId, long? boardId, CancellationToken ct = default)
    {
        if (sprintId is null)
        {
            if (boardId is null) throw new JiraException("Provide sprintId, or boardId to use that board's active sprint.");
            var active = await ListSprintsAsync(boardId.Value, "active", ct);
            sprintId = active.FirstOrDefault()?.Id ?? throw new JiraException($"Board {boardId} has no active sprint.");
        }

        var sprint = ToSprint((await SendAsync(HttpMethod.Get, $"/rest/agile/1.0/sprint/{sprintId}", null, ct))!);
        var (issues, more) = await AgileIssuesAsync($"/rest/agile/1.0/sprint/{sprintId}/issue", 200, ct);
        var (total, done, pts, donePts) = Totals(issues);
        return new JiraSprintView(sprint, total, done, pts, donePts,
            issues.GroupBy(i => i.Status).ToDictionary(g => g.Key, g => g.Count()), [.. issues], more);
    }

    public async Task<JiraBacklog> GetBacklogAsync(long boardId, int maxResults, CancellationToken ct = default)
    {
        var (issues, more) = await AgileIssuesAsync($"/rest/agile/1.0/board/{boardId}/backlog", Math.Clamp(maxResults, 1, 200), ct);
        return new JiraBacklog(issues.Count, more, [.. issues]);
    }

    public async Task MoveToSprintAsync(long sprintId, string[] issueKeys, CancellationToken ct = default)
    {
        EnsureWrites();
        await SendAsync(HttpMethod.Post, $"/rest/agile/1.0/sprint/{sprintId}/issue", KeysBody(issueKeys), ct);
    }

    public async Task MoveToBacklogAsync(string[] issueKeys, CancellationToken ct = default)
    {
        EnsureWrites();
        await SendAsync(HttpMethod.Post, "/rest/agile/1.0/backlog/issue", KeysBody(issueKeys), ct);
    }

    private static JsonObject KeysBody(string[] issueKeys)
    {
        if (issueKeys.Length is 0 or > 50) throw new JiraException("Provide between 1 and 50 issue keys.");
        return new JsonObject { ["issues"] = new JsonArray([.. issueKeys.Select(k => (JsonNode)JsonValue.Create(RequireKey(k))!)]) };
    }

    private async Task<(List<JiraIssueSummary> Issues, bool More)> AgileIssuesAsync(string path, int max, CancellationToken ct)
    {
        var (fields, sp) = await SummaryFieldsAsync(ct);
        var list = new List<JiraIssueSummary>();
        var startAt = 0;
        var more = false;
        while (true)
        {
            var size = Math.Min(50, max - list.Count);
            var page = await SendAsync(HttpMethod.Get, $"{path}?startAt={startAt}&maxResults={size}&fields={fields}", null, ct);
            var arr = page!["issues"]!.AsArray();
            list.AddRange(arr.Select(i => ToSummary(i!, sp)));
            startAt += arr.Count;
            if (arr.Count == 0 || startAt >= ((int?)page["total"] ?? 0)) break;
            if (list.Count >= max) { more = true; break; }
        }
        return (list, more);
    }

    private static JiraSprint ToSprint(JsonNode s) => new(
        (long)s["id"]!, (string?)s["name"] ?? "", (string?)s["state"] ?? "",
        (string?)s["startDate"], (string?)s["endDate"], (string?)s["goal"]);

    private static (int Total, int Done, double Points, double DonePoints) Totals(IReadOnlyCollection<JiraIssueSummary> issues)
    {
        var done = issues.Where(i => i.StatusCategory == "done").ToList();
        return (issues.Count, done.Count, issues.Sum(i => i.StoryPoints ?? 0), done.Sum(i => i.StoryPoints ?? 0));
    }

    // ---- epics ------------------------------------------------------------------------------------------------

    public Task<JiraSearchResult> ListEpicsAsync(string? project, bool includeDone, int maxResults, CancellationToken ct = default)
    {
        var clauses = new List<string> { "issuetype = Epic" };
        if (!string.IsNullOrWhiteSpace(project)) clauses.Add($"project = \"{project.Trim().Replace("\"", "")}\"");
        if (!includeDone) clauses.Add("statusCategory != Done");
        return SearchAsync(string.Join(" AND ", clauses) + " ORDER BY updated DESC", maxResults, ct);
    }

    public async Task<JiraEpicView> GetEpicAsync(string epicKey, CancellationToken ct = default)
    {
        var key = RequireKey(epicKey);
        var epic = (await SearchAsync($"key = {key}", 1, ct)).Issues.FirstOrDefault()
                   ?? throw new JiraException($"{key} was not found.");

        var children = await SearchAsync($"parent = {key} ORDER BY created", 100, ct);
        if (children.Issues.Length == 0)
        {
            // Older company-managed projects link children through the "Epic Link" field instead of parent.
            try { children = await SearchAsync($"\"Epic Link\" = {key} ORDER BY created", 100, ct); }
            catch (JiraException) { /* field not present on this site */ }
        }

        var (total, done, pts, donePts) = Totals(children.Issues);
        return new JiraEpicView(epic, total, done, pts, donePts, children.Issues, children.HasMore);
    }

    // ---- assignment -------------------------------------------------------------------------------------------

    /// <param name="issueKey">When given, only users who can be assigned that issue are returned.</param>
    public async Task<JiraUserMatch[]> FindUsersAsync(string query, string? issueKey, CancellationToken ct = default)
    {
        var q = Uri.EscapeDataString(query.Trim());
        var url = string.IsNullOrWhiteSpace(issueKey)
            ? $"/rest/api/3/user/search?query={q}&maxResults=10"
            : $"/rest/api/3/user/assignable/search?query={q}&issueKey={Uri.EscapeDataString(RequireKey(issueKey))}&maxResults=10";
        var r = await SendAsync(HttpMethod.Get, url, null, ct);
        return r!.AsArray()
            .Where(u => (u!["active"]?.GetValue<bool>() ?? true) && ((string?)u["accountType"] is null or "atlassian"))
            .Select(u => new JiraUserMatch((string)u!["accountId"]!, (string?)u["displayName"] ?? "", (string?)u["emailAddress"]))
            .ToArray();
    }

    /// <param name="assignee">"me", "unassigned", an account id, or a name/email to look up.</param>
    public async Task<string> AssignAsync(string key, string assignee, CancellationToken ct = default)
    {
        EnsureWrites();
        key = RequireKey(key);
        var a = assignee.Trim();
        string? accountId;
        string label;

        if (a.Length == 0 || Eq(a, "unassigned") || Eq(a, "none"))
        {
            (accountId, label) = (null, "Unassigned");
        }
        else if (Eq(a, "me") || Eq(a, "myself"))
        {
            var me = await GetMyselfAsync(ct);
            (accountId, label) = (me.AccountId, me.DisplayName);
        }
        else if (AccountIdPattern.IsMatch(a))
        {
            (accountId, label) = (a, a);
        }
        else
        {
            var matches = await FindUsersAsync(a, key, ct);
            var exact = matches.Where(m => Eq(m.DisplayName, a) || Eq(m.Email, a)).ToArray();
            var pick = exact.Length == 1 ? exact[0] : matches.Length == 1 ? matches[0] : null;
            if (pick is null)
                throw new JiraException(matches.Length == 0
                    ? $"No user who can be assigned {key} matches '{a}'."
                    : $"'{a}' matches several users: {string.Join("; ", matches.Select(m => $"{m.DisplayName} ({m.Email ?? m.AccountId})"))}. Use a fuller name or the email.");
            (accountId, label) = (pick.AccountId, pick.DisplayName);
        }

        await SendAsync(HttpMethod.Put, $"/rest/api/3/issue/{key}/assignee", new JsonObject { ["accountId"] = accountId }, ct);
        return $"{key} is now assigned to: {label}.";
    }

    // ---- links ------------------------------------------------------------------------------------------------

    public async Task<JiraLinkType[]> GetLinkTypesAsync(CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Get, "/rest/api/3/issueLinkType", null, ct);
        return r!["issueLinkTypes"]!.AsArray()
            .Select(t => new JiraLinkType((string)t!["name"]!, (string?)t["inward"] ?? "", (string?)t["outward"] ?? ""))
            .ToArray();
    }

    /// <param name="linkType">A link type name ("Blocks") or either of its phrases ("blocks", "is blocked by").</param>
    /// <returns>A sentence describing the link as Jira stored it, e.g. "ABC-1 blocks ABC-2".</returns>
    public async Task<string> LinkAsync(string fromKey, string toKey, string linkType, CancellationToken ct = default)
    {
        EnsureWrites();
        var (src, dst) = (RequireKey(fromKey), RequireKey(toKey));
        var types = await GetLinkTypesAsync(ct);

        var type = types.FirstOrDefault(t => Eq(t.Name, linkType)) ?? types.FirstOrDefault(t => Eq(t.Outward, linkType));
        if (type is null)
        {
            type = types.FirstOrDefault(t => Eq(t.Inward, linkType));
            if (type is not null) (src, dst) = (dst, src); // "A is blocked by B" is stored as "B blocks A"
        }
        if (type is null)
            throw new JiraException($"Unknown link type '{linkType}'. Available: {string.Join(", ", types.Select(t => $"{t.Name} ('{t.Outward}' / '{t.Inward}')"))}");

        await PostLinkAsync(type.Name, src, dst, _linkSwapped, ct);

        // Jira's inward/outward naming is easy to get backwards, so the first link is read back and corrected if needed.
        if (!_linkVerified)
        {
            var found = await FindLinkAsync(src, dst, type.Name, ct);
            if (found is { } f)
            {
                if (!f.SrcIsOutward)
                {
                    await SendAsync(HttpMethod.Delete, $"/rest/api/3/issueLink/{f.Id}", null, ct);
                    _linkSwapped = !_linkSwapped;
                    await PostLinkAsync(type.Name, src, dst, _linkSwapped, ct);
                }
                _linkVerified = true;
            }
        }
        return $"{src} {type.Outward} {dst}";
    }

    private Task PostLinkAsync(string typeName, string src, string dst, bool swapped, CancellationToken ct)
    {
        var (outward, inward) = swapped ? (dst, src) : (src, dst);
        return SendAsync(HttpMethod.Post, "/rest/api/3/issueLink", new JsonObject
        {
            ["type"] = new JsonObject { ["name"] = typeName },
            ["outwardIssue"] = new JsonObject { ["key"] = outward },
            ["inwardIssue"] = new JsonObject { ["key"] = inward },
        }, ct);
    }

    private async Task<(string Id, bool SrcIsOutward)?> FindLinkAsync(string src, string dst, string typeName, CancellationToken ct)
    {
        var issue = await SendAsync(HttpMethod.Get, $"/rest/api/3/issue/{src}?fields=issuelinks", null, ct);
        var links = issue!["fields"]?["issuelinks"]?.AsArray();
        if (links is null) return null;

        foreach (var l in links.Reverse())
        {
            if (!Eq((string?)l!["type"]?["name"], typeName)) continue;
            // In src's own issue, "outwardIssue" is the other issue when src carries the outward phrase.
            if (Eq((string?)l["outwardIssue"]?["key"], dst)) return ((string)l["id"]!, true);
            if (Eq((string?)l["inwardIssue"]?["key"], dst)) return ((string)l["id"]!, false);
        }
        return null;
    }

    // ---- work logs --------------------------------------------------------------------------------------------

    /// <param name="timeSpent">Jira duration, e.g. "2h 30m", "1d", "45m".</param>
    /// <param name="started">Optional ISO date/time when the work started; defaults to now.</param>
    public async Task<string> LogWorkAsync(string key, string timeSpent, string? comment, string? started, CancellationToken ct = default)
    {
        EnsureWrites();
        key = RequireKey(key);
        if (!Regex.IsMatch(timeSpent.Trim(), @"^(\d+(\.\d+)?\s*[wdhm]\s*)+$", RegexOptions.IgnoreCase))
            throw new JiraException($"'{timeSpent}' is not a valid duration. Use Jira format such as '2h 30m', '1d' or '45m'.");

        var body = new JsonObject { ["timeSpent"] = timeSpent.Trim() };
        if (!string.IsNullOrWhiteSpace(comment)) body["comment"] = AdfConverter.ToAdf(comment);
        if (!string.IsNullOrWhiteSpace(started))
        {
            if (!DateTimeOffset.TryParse(started, out var when))
                throw new JiraException($"Could not read '{started}' as a date/time. Use e.g. 2026-10-28T09:30:00+05:30.");
            // Jira wants +0530, not +05:30.
            body["started"] = when.ToString("yyyy-MM-dd'T'HH:mm:ss.fff") + when.ToString("zzz").Replace(":", "");
        }

        var r = await SendAsync(HttpMethod.Post, $"/rest/api/3/issue/{key}/worklog", body, ct);
        return $"Logged {(string?)r?["timeSpent"] ?? timeSpent.Trim()} on {key}.";
    }

    public async Task<JiraWorklog[]> GetWorklogsAsync(string key, CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Get, $"/rest/api/3/issue/{RequireKey(key)}/worklog", null, ct);
        return (r!["worklogs"]?.AsArray() ?? [])
            .Select(w => new JiraWorklog(
                (string?)w!["author"]?["displayName"] ?? "", (string?)w["started"] ?? "",
                (string?)w["timeSpent"] ?? "", (int?)w["timeSpentSeconds"] ?? 0, AdfConverter.ToText(w["comment"])))
            .ToArray();
    }
}
