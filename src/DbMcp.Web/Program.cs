using System.Net;
using System.Security.Cryptography;
using System.Text;
using DbMcp.Web.Components;
using DbMcp.Web.Services;
using DbMcp.Web.Tools;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<DbSettingsStore>();
builder.Services.AddSingleton<DbEngine>();
builder.Services.AddSingleton<DbToolCatalog>();
builder.Services.AddScoped<ChatSession>();

builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<DbTools>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found");

// This app holds database connection strings and can run SQL, so it only answers requests from this machine.
app.Use(async (ctx, next) =>
{
    var remote = ctx.Connection.RemoteIpAddress;
    if (remote is not null && !IPAddress.IsLoopback(remote))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsync("DbMcp only accepts requests from the local machine.");
        return;
    }
    await next();
});

// /mcp is guarded by the bearer key shown on the home page, so a website open in the browser can't drive the databases through it.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/mcp"))
    {
        var expected = ctx.RequestServices.GetRequiredService<DbSettingsStore>().Get().McpKey;
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
