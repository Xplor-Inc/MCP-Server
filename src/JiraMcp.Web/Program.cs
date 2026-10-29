using System.Security.Cryptography;
using System.Text;
using JiraMcp.Web.Components;
using JiraMcp.Web.Services;
using JiraMcp.Web.Tools;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpClient("jira", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<JiraSettingsStore>();
builder.Services.AddSingleton<JiraClient>();
builder.Services.AddSingleton<JiraToolCatalog>();
builder.Services.AddScoped<ChatSession>();

builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<JiraTools>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found");

// /mcp is guarded by the bearer key shown on the home page, so a website open in the browser can't drive Jira through it.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/mcp"))
    {
        var expected = ctx.RequestServices.GetRequiredService<JiraSettingsStore>().Get().McpKey;
        var header = ctx.Request.Headers.Authorization.ToString();
        var supplied = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : "";
        if (expected.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected)))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsync("Missing or invalid MCP key."); // non-empty body so the status-code page doesn't re-execute
            return;
        }
    }
    await next();
});

app.UseAntiforgery();

app.UseStaticFiles();
app.MapMcp("/mcp");
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
