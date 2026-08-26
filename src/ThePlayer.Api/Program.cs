using ThePlayer.Api;
using ThePlayer.Application.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddThePlayer(builder.Configuration, builder.Environment);

var app = builder.Build();

// Before anything is served. A deployment that demands a key and has none configured refuses every
// request, and that is one missing environment variable away at any time - so it fails here, where
// the message is unmissable, rather than as a wall of 401s that read like a client problem.
var apiKeys = app.Services.GetRequiredService<ApiKeyGuard>();

if (!apiKeys.IsUsable)
{
    throw new InvalidOperationException(
        "ApiKey:Required is true but no ApiKey:Keys are configured, so every request would be " +
        "refused. Set ApiKey__Keys__0 in the environment, or turn the requirement off.");
}

if (app.Environment.IsDevelopment())
{
    app.UseCors(ServiceRegistration.DevelopmentCorsPolicy);
}
else
{
    // Outside Development an unhandled exception must never reach the client as a stack trace.
    // Beyond the usual reasons, this system's exception messages can carry an upstream address,
    // and paths, tool names and internals are exactly what an attacker would like to read.
    // Paired with AddProblemDetails(), this returns a plain RFC 7807 body instead.
    app.UseExceptionHandler();
}

// The frame socket. KeepAliveInterval defaults to 30s, which is what stops an idle proxy from
// dropping a connection that is streaming perfectly well.
app.UseWebSockets();

// Ahead of the static files as well as the endpoints, so the order of the two below cannot quietly
// expose anything. What it guards and what it lets past is decided in one place.
app.UseMiddleware<ApiKeyMiddleware>();

// Serves wwwroot/index.html - a minimal WHEP harness used to verify the backend in a real browser.
// The Angular player replaces it as the primary client; this stays as a dependency-free fallback.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapThePlayerEndpoints();

app.Run();

/// <summary>
/// Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can boot the real host in tests. A
/// top-level-statements program has an internal entry point class, and the test project is granted
/// InternalsVisibleTo in the csproj.
/// </summary>
public partial class Program;
