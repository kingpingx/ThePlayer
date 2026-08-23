using ThePlayer.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddThePlayer(builder.Configuration, builder.Environment);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseCors(ServiceRegistration.DevelopmentCorsPolicy);
}

app.MapThePlayerEndpoints();

app.Run();

/// <summary>
/// Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can boot the real host in tests. A
/// top-level-statements program has an internal entry point class, and the test project is granted
/// InternalsVisibleTo in the csproj.
/// </summary>
public partial class Program;
