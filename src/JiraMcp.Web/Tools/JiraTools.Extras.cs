using System.ComponentModel;
using JiraMcp.Web.Services;
using ModelContextProtocol.Server;

namespace JiraMcp.Web.Tools;

// Boards/sprints, epics, people, links and work logs.
public sealed partial class JiraTools
{
    // ---- boards & sprints ----

    [McpServerTool(Name = "jira_list_boards", ReadOnly = true), Description("List Jira Software boards (scrum/kanban). Use this to find a board id for the sprint and backlog tools.")]
    public Task<JiraBoard[]> ListBoards(
        [Description("Project key to filter by, e.g. ABC.")] string? project = null,
        [Description("Part of the board name.")] string? name = null,
        [Description("'scrum' or 'kanban'.")] string? type = null,
        CancellationToken ct = default)
        => Run(() => Jira.ListBoardsAsync(project, name, type, ct));

    [McpServerTool(Name = "jira_list_sprints", ReadOnly = true), Description("List the sprints of a scrum board (scrum boards only). Closed sprints: the 30 most recent.")]
    public Task<JiraSprint[]> ListSprints(
        [Description("Board id from jira_list_boards.")] long boardId,
        [Description("Comma separated states: active, future, closed.")] string state = "active,future",
        CancellationToken ct = default)
        => Run(() => Jira.ListSprintsAsync(boardId, state, ct));

    [McpServerTool(Name = "jira_get_sprint", ReadOnly = true),
     Description("Sprint view: the sprint's dates and goal, its issues, issue counts by status and story points done vs total. Give sprintId, or boardId to use that board's active sprint.")]
    public Task<JiraSprintView> GetSprint(
        [Description("Sprint id from jira_list_sprints.")] long? sprintId = null,
        [Description("Board id; used to find the active sprint when sprintId is omitted.")] long? boardId = null,
        CancellationToken ct = default)
        => Run(() => Jira.GetSprintViewAsync(sprintId, boardId, ct));

    [McpServerTool(Name = "jira_get_backlog", ReadOnly = true), Description("List the backlog issues of a board in rank order (issues not in any active sprint).")]
    public Task<JiraBacklog> GetBacklog(
        [Description("Board id from jira_list_boards.")] long boardId,
        [Description("Maximum issues to return (1-200).")] int maxResults = 50,
        CancellationToken ct = default)
        => Run(() => Jira.GetBacklogAsync(boardId, maxResults, ct));

    [McpServerTool(Name = "jira_move_to_sprint", Destructive = false), Description("Move issues into a sprint (active or future).")]
    public Task<string> MoveToSprint(
        [Description("Sprint id from jira_list_sprints.")] long sprintId,
        [Description("Issue keys, 1-50.")] string[] issueKeys,
        CancellationToken ct = default)
        => Run(async () =>
        {
            await Jira.MoveToSprintAsync(sprintId, issueKeys, ct);
            return $"Moved {string.Join(", ", issueKeys)} to sprint {sprintId}.";
        });

    [McpServerTool(Name = "jira_move_to_backlog", Destructive = false), Description("Remove issues from their sprint and put them back in the backlog.")]
    public Task<string> MoveToBacklog([Description("Issue keys, 1-50.")] string[] issueKeys, CancellationToken ct = default)
        => Run(async () =>
        {
            await Jira.MoveToBacklogAsync(issueKeys, ct);
            return $"Moved {string.Join(", ", issueKeys)} to the backlog.";
        });

    // ---- epics ----

    [McpServerTool(Name = "jira_list_epics", ReadOnly = true), Description("List epics, most recently updated first.")]
    public Task<JiraSearchResult> ListEpics(
        [Description("Project key, e.g. ABC. Omit for all projects.")] string? project = null,
        [Description("Include epics that are already Done.")] bool includeDone = false,
        [Description("Maximum epics to return (1-100).")] int maxResults = 50,
        CancellationToken ct = default)
        => Run(() => Jira.ListEpicsAsync(project, includeDone, maxResults, ct));

    [McpServerTool(Name = "jira_get_epic", ReadOnly = true), Description("An epic with all of its child issues, how many are done, and story points done vs total.")]
    public Task<JiraEpicView> GetEpic([Description("Epic key, e.g. ABC-10")] string epicKey, CancellationToken ct = default)
        => Run(() => Jira.GetEpicAsync(epicKey, ct));

    // ---- people ----

    [McpServerTool(Name = "jira_find_users", ReadOnly = true), Description("Find Jira users by name or email. With issueKey, only users who can be assigned that issue are returned.")]
    public Task<JiraUserMatch[]> FindUsers(
        [Description("Name or email fragment.")] string query,
        [Description("Optional issue key to limit to assignable users.")] string? issueKey = null,
        CancellationToken ct = default)
        => Run(() => Jira.FindUsersAsync(query, issueKey, ct));

    [McpServerTool(Name = "jira_assign_issue", Destructive = false, Idempotent = true),
     Description("Assign an issue to someone, or unassign it. The assignee can be 'me', 'unassigned', a name, an email, or an account id.")]
    public Task<string> AssignIssue(
        [Description("Issue key, e.g. ABC-123")] string key,
        [Description("'me', 'unassigned', a display name, an email, or an account id.")] string assignee,
        CancellationToken ct = default)
        => Run(() => Jira.AssignAsync(key, assignee, ct));

    // ---- links ----

    [McpServerTool(Name = "jira_list_link_types", ReadOnly = true), Description("List the issue link types available on this Jira site, with their wording (e.g. 'blocks' / 'is blocked by').")]
    public Task<JiraLinkType[]> ListLinkTypes(CancellationToken ct = default)
        => Run(() => Jira.GetLinkTypesAsync(ct));

    [McpServerTool(Name = "jira_link_issues", Destructive = false),
     Description("Link two issues. Reads as '<fromKey> <linkType> <toKey>', e.g. fromKey=ABC-1, linkType='blocks', toKey=ABC-2 means ABC-1 blocks ABC-2. Use jira_list_link_types for valid wording.")]
    public Task<string> LinkIssues(
        [Description("The issue the phrase is about, e.g. the blocker.")] string fromKey,
        [Description("The other issue.")] string toKey,
        [Description("Link type name or phrase, e.g. 'Blocks', 'blocks', 'is blocked by', 'relates to', 'duplicates'.")] string linkType,
        CancellationToken ct = default)
        => Run(() => Jira.LinkAsync(fromKey, toKey, linkType, ct));

    // ---- work logs ----

    [McpServerTool(Name = "jira_log_work", Destructive = false), Description("Log time spent on an issue (a work log entry).")]
    public Task<string> LogWork(
        [Description("Issue key, e.g. ABC-123")] string key,
        [Description("Time spent in Jira format: '2h 30m', '1d', '45m'. 1d = 8h, 1w = 5d.")] string timeSpent,
        [Description("What was done (plain text).")] string? comment = null,
        [Description("When the work started, ISO date/time like 2026-10-28T09:30:00+05:30. Defaults to now.")] string? started = null,
        CancellationToken ct = default)
        => Run(() => Jira.LogWorkAsync(key, timeSpent, comment, started, ct));

    [McpServerTool(Name = "jira_get_worklogs", ReadOnly = true), Description("List the work log entries on an issue.")]
    public Task<JiraWorklog[]> GetWorklogs([Description("Issue key, e.g. ABC-123")] string key, CancellationToken ct = default)
        => Run(() => Jira.GetWorklogsAsync(key, ct));
}
