using ThePlayer.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddThePlayer(builder.Configuration, builder.Environment);

var app = builder.Build();

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
