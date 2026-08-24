using ThePlayer.Application;
using ThePlayer.Application.Broadcasting;
using ThePlayer.Application.Monitoring;
using ThePlayer.Infrastructure.FFmpeg;
using ThePlayer.Application.Security;
using ThePlayer.Infrastructure.MediaServer;
using ThePlayer.Infrastructure.Networking;

namespace ThePlayer.Api;

/// <summary>
/// The composition root. Every concrete type is chosen here and nowhere else, which is what lets
/// the layers below stay unaware of each other's implementations.
/// </summary>
public static class ServiceRegistration
{
    public const string DevelopmentCorsPolicy = "AngularDevServer";

    public static IServiceCollection AddThePlayer(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddOptionsWithValidation<FFmpegOptions>(configuration, FFmpegOptions.SectionName);
        services.AddOptionsWithValidation<MediaMtxOptions>(configuration, MediaMtxOptions.SectionName);
        services.AddOptionsWithValidation<BroadcastOptions>(configuration, BroadcastOptions.SectionName);
        services.AddOptionsWithValidation<AddressPolicyOptions>(configuration, AddressPolicyOptions.SectionName);

        services.AddHttpClient();

        // Makes UseExceptionHandler emit RFC 7807 bodies, and gives the endpoints' explicit
        // Results.Problem calls the same shape as anything that escapes unhandled.
        services.AddProblemDetails();

        // Injected rather than calling DateTimeOffset.UtcNow directly, so linger and cache
        // expiry can be tested by advancing a fake clock instead of sleeping.
        services.AddSingleton(TimeProvider.System);

        // Singletons because both cache: the inspector memoises its FFmpeg probe, and the
        // supervisor owns a child process for the lifetime of the app.
        services.AddSingleton<FFmpegCommandRunner>();
        services.AddSingleton<IHardwareInspector, FFmpegHardwareInspector>();
        services.AddSingleton<IMediaInspector, FFmpegMediaInspector>();
        services.AddSingleton<IMediaServer, MediaMtxPaths>();

        // Stateless - it spawns a process per call and hands back ownership of it - so the
        // lifetime here is about avoiding needless allocation, not about shared state.
        services.AddSingleton<IFramePipeline, FFmpegStreamingPipeline>();

        // Concrete, not behind ports: pure in-process logic with nothing to substitute. The
        // coordinator is a singleton because it *is* the registry of what is currently live.
        services.AddSingleton<IAddressResolver, DnsAddressResolver>();
        services.AddSingleton<AddressGuard>();

        services.AddSingleton<BroadcastPlanner>();
        services.AddSingleton<BroadcastCoordinator>();
        services.AddHostedService<BroadcastSweeper>();

        // One instance playing three roles: the hosted service that runs it, and the port that
        // the health endpoint reads. Resolving the same object for both is what makes the status
        // the supervisor writes the status the endpoint sees.
        services.AddSingleton<MediaMtxSupervisor>();
        services.AddSingleton<IMediaServerSupervisor>(provider =>
            provider.GetRequiredService<MediaMtxSupervisor>());
        services.AddHostedService(provider => provider.GetRequiredService<MediaMtxSupervisor>());

        services.AddSingleton(provider => new HealthReporter(
            provider.GetRequiredService<IMediaServerSupervisor>(),
            provider.GetRequiredService<IHardwareInspector>(),
            environment.EnvironmentName));

        if (environment.IsDevelopment())
        {
            // The Angular dev server runs on a different origin. Only in Development - Staging and
            // Production serve the built player from the same origin and need no exception.
            services.AddCors(cors => cors.AddPolicy(
                DevelopmentCorsPolicy,
                policy => policy
                    .WithOrigins("http://localhost:4200", "https://localhost:4200")
                    .AllowAnyHeader()
                    .AllowAnyMethod()));
        }

        return services;
    }

    /// <summary>
    /// Binds a configuration section and validates it at startup rather than on first use, so a
    /// bad setting fails immediately with a clear message instead of halfway through playing
    /// something.
    /// </summary>
    private static void AddOptionsWithValidation<TOptions>(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName)
        where TOptions : class
    {
        services.AddOptions<TOptions>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }
}
