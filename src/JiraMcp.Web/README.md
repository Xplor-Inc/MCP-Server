# Jira MCP

Blazor Web App (.NET 8) that hosts an MCP server at `/mcp` so GitHub Copilot (VS Code agent mode) can work with Jira Cloud.

## Run
    dotnet run --project src/JiraMcp.Web --urls http://localhost:5289

Open http://localhost:5289, enter the Jira site URL, email and an API token (https://id.atlassian.com/manage-profile/security/api-tokens), click **Test connection**, then copy the `mcp.json` snippet into `.vscode/mcp.json`.

**Jira Data Center / Server** (own domain such as `jira.company.com`, including behind SSO): tick *Jira Data Center / Server*, enter the base URL and a Personal Access Token (Profile > Personal Access Tokens). No email is needed. The app then uses Bearer auth and REST API v2, and users are identified by username.

## Tools
- **Issues:** jira_whoami, jira_list_my_issues (reporter = me), jira_list_assigned_to_me, jira_search (JQL), jira_get_issue, jira_update_issue (title / description / story points / labels), jira_add_comment, jira_list_transitions, jira_transition_issue, jira_create_issue
- **Boards & sprints:** jira_list_boards, jira_list_sprints, jira_get_sprint (issues, status counts, points done vs total), jira_get_backlog, jira_move_to_sprint, jira_move_to_backlog
- **Epics:** jira_list_epics, jira_get_epic (children + progress)
- **People:** jira_find_users, jira_assign_issue (me / unassigned / name / email)
- **Links:** jira_list_link_types, jira_link_issues ("ABC-1 blocks ABC-2")
- **Time:** jira_log_work, jira_get_worklogs
Untick "Allow changes" on the home page for a read-only server. `/mcp` requires `Authorization: Bearer <key>`; keep the app bound to localhost.

## Chat page (Claude)
`/chat` is a chat window inside the app. It calls the Anthropic API (key + model are set on the home page, default `claude-opus-5-5`) and gives Claude the same Jira tools as the MCP endpoint. Read-only tools run immediately; anything that changes Jira (update, comment, transition, create) shows an Approve / Decline card first.

Tool definitions are generated from `Tools/JiraTools.cs`, so the MCP endpoint and the chat always stay in sync.

