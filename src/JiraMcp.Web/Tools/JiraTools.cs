using System.ComponentModel;
using JiraMcp.Web.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace JiraMcp.Web.Tools;

[McpServerToolType]
public sealed partial class JiraTools(JiraClient jira)
{
    [McpServerTool(Name = "jira_whoami", ReadOnly = true), Description("Show which Jira user the server is authenticated as.")]
    public Task<JiraUser> WhoAmI(CancellationToken ct) => Run(() => jira.GetMyselfAsync(ct));

    [McpServerTool(Name = "jira_list_my_issues", ReadOnly = true),
     Description("List Jira issues created (reported) by the current user, newest first. Optionally narrow by project, status, or include only unresolved issues.")]
    public Task<JiraSearchResult> ListMyIssues(
        [Description("Project key, e.g. ABC. Omit for all projects.")] string? project = null,
        [Description("Exact status name, e.g. 'In Progress'. Omit for any status.")] string? status = null,
        [Description("Only issues that are not Done/resolved.")] bool unresolvedOnly = false,
        [Description("Maximum issues to return (1-100).")] int maxResults = 50,
        CancellationToken ct = default)
    {
        var clauses = new List<string> { "reporter = currentUser()" };
        if (!string.IsNullOrWhiteSpace(project)) clauses.Add($"project = \"{Escape(project)}\"");
        if (!string.IsNullOrWhiteSpace(status)) clauses.Add($"status = \"{Escape(status)}\"");
        if (unresolvedOnly) clauses.Add("resolution = Unresolved");
        return Run(() => jira.SearchAsync(string.Join(" AND ", clauses) + " ORDER BY created DESC", maxResults, ct));
    }

    [McpServerTool(Name = "jira_list_assigned_to_me", ReadOnly = true),
     Description("List Jira issues currently assigned to the current user, most recently updated first.")]
    public Task<JiraSearchResult> ListAssignedToMe(
        [Description("Include resolved issues too.")] bool includeResolved = false,
        [Description("Maximum issues to return (1-100).")] int maxResults = 50,
        CancellationToken ct = default)
    {
        var jql = "assignee = currentUser()" + (includeResolved ? "" : " AND resolution = Unresolved") + " ORDER BY updated DESC";
        return Run(() => jira.SearchAsync(jql, maxResults, ct));
    }

    [McpServerTool(Name = "jira_search", ReadOnly = true), Description("Search Jira issues with a JQL query.")]
    public Task<JiraSearchResult> Search(
        [Description("JQL, e.g. project = ABC AND sprint in openSprints() ORDER BY rank")] string jql,
        [Description("Maximum issues to return (1-100).")] int maxResults = 50,
        CancellationToken ct = default)
        => Run(() => jira.SearchAsync(jql, maxResults, ct));

    [McpServerTool(Name = "jira_get_issue", ReadOnly = true), Description("Get one issue with its description, labels, parent and the last 5 comments.")]
    public Task<JiraIssueDetail> GetIssue([Description("Issue key, e.g. ABC-123")] string key, CancellationToken ct)
        => Run(() => jira.GetIssueAsync(key, ct));

    [McpServerTool(Name = "jira_update_issue", Destructive = false, Idempotent = true),
     Description("Update an issue's title (summary), description, story points and/or labels. Only the provided fields change. Pass an empty string as description to clear it.")]
    public Task<string> UpdateIssue(
        [Description("Issue key, e.g. ABC-123")] string key,
        [Description("New title.")] string? summary = null,
        [Description("New description as plain text; blank line = new paragraph. REPLACES the existing description.")] string? description = null,
        [Description("Story point estimate, e.g. 3 or 0.5.")] double? storyPoints = null,
        [Description("Full replacement label list.")] string[]? labels = null,
        CancellationToken ct = default)
        => Run(async () =>
        {
            await jira.UpdateIssueAsync(key, summary, description, storyPoints, labels, ct);
            return $"Updated {key}.";
        });

    [McpServerTool(Name = "jira_add_comment"), Description("Add a comment to an issue (plain text).")]
    public Task<string> AddComment(
        [Description("Issue key, e.g. ABC-123")] string key,
        [Description("Comment text.")] string body,
        CancellationToken ct)
        => Run(async () => { await jira.AddCommentAsync(key, body, ct); return $"Comment added to {key}."; });

    [McpServerTool(Name = "jira_list_transitions", ReadOnly = true), Description("List the status transitions currently available for an issue.")]
    public Task<JiraTransition[]> ListTransitions([Description("Issue key")] string key, CancellationToken ct)
        => Run(() => jira.GetTransitionsAsync(key, ct));

    [McpServerTool(Name = "jira_transition_issue", Destructive = false),
     Description("Move an issue to another status, e.g. 'In Progress' or 'Done'. Use jira_list_transitions to see valid names.")]
    public Task<string> TransitionIssue(
        [Description("Issue key")] string key,
        [Description("Transition name or target status name.")] string status,
        CancellationToken ct)
        => Run(async () => $"{key} is now '{await jira.TransitionAsync(key, status, ct)}'.");

    [McpServerTool(Name = "jira_create_issue", Destructive = false), Description("Create a new issue.")]
    public Task<string> CreateIssue(
        [Description("Project key, e.g. ABC")] string project,
        [Description("Issue type name, e.g. Story, Task, Bug.")] string issueType,
        [Description("Title.")] string summary,
        [Description("Description as plain text.")] string? description = null,
        [Description("Story point estimate.")] double? storyPoints = null,
        CancellationToken ct = default)
        => Run(async () => $"Created {await jira.CreateIssueAsync(project, issueType, summary, description, storyPoints, ct)}.");

    private JiraClient Jira => jira; // primary-constructor parameters are not visible from other partial parts

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // The SDK hides arbitrary exception text from the model; McpException messages are passed through.
    private static async Task<T> Run<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (JiraException ex) { throw new McpException(ex.Message, ex); }
    }
}
