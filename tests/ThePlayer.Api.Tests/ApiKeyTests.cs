using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ThePlayer.Application;
using ThePlayer.Domain.Hardware;
using ThePlayer.Domain.Media;
using ThePlayer.Domain.Monitoring;

namespace ThePlayer.Api.Tests;

/// <summary>
/// What a deployment with a key lets through, over real HTTP against the real pipeline.
/// </summary>
/// <remarks>
/// Exercised through the host rather than against <c>ApiKeyGuard</c> directly, because the thing
/// most likely to be wrong is not the comparison but <em>which paths are guarded</em> — and that
/// only exists once middleware and routing are both in play.
/// </remarks>
public class ApiKeyTests
{
    private const string Key = "a-real-key";

    [Fact]
    public async Task A_guarded_endpoint_refuses_a_request_with_no_key()
    {
        using var client = Guarded().CreateClient();

        var response = await client.GetAsync("/api/broadcasts");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_refusal_says_how_to_authenticate_without_saying_more()
    {
        using var client = Guarded().CreateClient();

        var response = await client.GetAsync("/api/broadcasts");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("X-Api-Key");
        body.Should().NotContain(Key, "a refusal must never confirm any part of the real key");
    }

    [Fact]
    public async Task The_key_in_a_header_is_accepted()
    {
        using var client = Guarded().CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", Key);

        var response = await client.GetAsync("/api/broadcasts");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_key_in_the_query_string_is_accepted()
    {
        // The concession to EventSource, which cannot set a header at all. Weaker, and used only
        // where there is no alternative - but it has to actually work.
        using var client = Guarded().CreateClient();

        var response = await client.GetAsync($"/api/broadcasts?key={Key}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_wrong_key_is_refused()
    {
        using var client = Guarded().CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "not-the-key");

        var response = await client.GetAsync("/api/broadcasts");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Health_still_answers_a_probe_that_carries_nothing()
    {
        // A liveness check that needs a secret is a liveness check that ends up switched off.
        using var client = Guarded().CreateClient();

        var response = await client.GetAsync("/api/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_tells_an_anonymous_probe_nothing_about_the_machine()
    {
        using var client = Guarded().CreateClient();

        var response = await client.GetAsync("/api/health");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("healthy");
        body.Should().NotContain("h264_nvenc", "the hardware inventory is not for anonymous callers");
        body.Should().NotContain("ffmpegVersion");
    }

    [Fact]
    public async Task Health_tells_an_authorised_caller_everything()
    {
        using var client = Guarded().CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", Key);

        var health = await client.GetFromJsonAsync<HealthResponse>("/api/health");

        health.Should().NotBeNull();
        health!.Hardware.Profiles.Should().NotBeEmpty();
    }

    [Fact]
    public async Task An_unguarded_deployment_answers_everyone()
    {
        // The development default, and the reason the panel in the player waits to be told.
        using var client = Unguarded().CreateClient();

        var response = await client.GetAsync("/api/broadcasts");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_player_itself_is_served_without_a_key()
    {
        // Otherwise a visitor gets a 401 in place of the page that could have asked them for one.
        using var client = Guarded().CreateClient();

        var response = await client.GetAsync("/");

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void Demanding_a_key_with_none_configured_refuses_to_start()
    {
        // One missing environment variable away at any time, and the symptom - every request
        // refused - looks like a client problem from every angle except this one.
        var factory = Factory(required: true, keys: []);

        var act = () => factory.CreateClient();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no ApiKey:Keys*");

        factory.Dispose();
    }

    private static WebApplicationFactory<Program> Guarded() => Factory(required: true, keys: [Key]);

    private static WebApplicationFactory<Program> Unguarded() => Factory(required: false, keys: []);

    private static WebApplicationFactory<Program> Factory(bool required, string[] keys)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ApiKey:Required"] = required.ToString(),
        };

        for (var i = 0; i < keys.Length; i++)
        {
            settings[$"ApiKey:Keys:{i}"] = keys[i];
        }

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(settings));

            builder.ConfigureServices(services =>
            {
                // The same substitution the health tests make, and for the same reason: these
                // tests need no FFmpeg on PATH and no MediaMTX binary present.
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IMediaServerSupervisor>();
                services.RemoveAll<IHardwareInspector>();

                services.AddSingleton<IMediaServerSupervisor>(new FakeMediaServer());
                services.AddSingleton<IHardwareInspector>(new FakeHardware());
            });
        });
    }

    private sealed class FakeMediaServer : IMediaServerSupervisor
    {
        public MediaServerStatus Status { get; } =
            new(MediaServerState.Running, "1.9.3", RestartCount: 0, LastError: null);
    }

    private sealed class FakeHardware : IHardwareInspector
    {
        public Task<HardwareCapabilities> InspectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new HardwareCapabilities(
                "8.0",
                [
                    AccelerationProfile.Known.Single(profile => profile.Kind == AccelerationKind.Nvidia),
                    AccelerationProfile.Software,
                ],
                [VideoCodec.H264, VideoCodec.H265]));
    }
}
