using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ThePlayer.Api;
using ThePlayer.Application;
using ThePlayer.Domain.Hardware;
using ThePlayer.Domain.Media;
using ThePlayer.Domain.Monitoring;

namespace ThePlayer.Api.Tests;

/// <summary>
/// Boots the real host and exercises <c>/api/health</c> over HTTP, with the two ports that reach
/// outside the process replaced by fakes.
/// </summary>
/// <remarks>
/// Faking them is the point rather than a shortcut: it means these tests do not need FFmpeg
/// installed or a MediaMTX binary present, and - more usefully - it lets us assert what the
/// endpoint does when MediaMTX is <em>broken</em>, which is the case that actually matters and
/// which a real server would never reproduce on demand.
/// </remarks>
public class HealthEndpointTests
{
    [Fact]
    public async Task Reports_healthy_when_the_media_server_is_running()
    {
        using var factory = CreateFactory(FakeMediaServer.Running(), FakeHardware.WithNvidia());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var health = await response.Content.ReadFromJsonAsync<HealthResponse>();
        health.Should().NotBeNull();
        health!.Healthy.Should().BeTrue();
        health.MediaServer.State.Should().Be(nameof(MediaServerState.Running));
    }

    [Fact]
    public async Task Reports_503_when_the_media_server_is_not_running()
    {
        // A probe must be able to tell "up but broken" from "up and working" without reading the
        // body, so the status code carries the verdict and the body carries the reason.
        using var factory = CreateFactory(
            FakeMediaServer.Failed("MediaMTX was not found."),
            FakeHardware.WithNvidia());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var health = await response.Content.ReadFromJsonAsync<HealthResponse>();
        health!.Healthy.Should().BeFalse();
        health.MediaServer.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task Reports_the_detected_acceleration_profiles()
    {
        using var factory = CreateFactory(FakeMediaServer.Running(), FakeHardware.WithNvidia());
        using var client = factory.CreateClient();

        var health = await client.GetFromJsonAsync<HealthResponse>("/api/health");

        health!.Hardware.HasHardwareAcceleration.Should().BeTrue();
        health.Hardware.PreferredProfile.Should().Contain("NVIDIA");
        health.Hardware.Profiles.Should().Contain(profile => profile.Encoder == "h264_nvenc");
        health.Hardware.DecodableCodecs.Should().Contain(nameof(VideoCodec.H265));
    }

    [Fact]
    public async Task Software_only_machines_are_still_healthy()
    {
        // No GPU is a performance characteristic, not a fault. A machine that can only encode with
        // libx264 still plays video, and reporting it as unhealthy would page someone for nothing.
        using var factory = CreateFactory(FakeMediaServer.Running(), FakeHardware.SoftwareOnly());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var health = await response.Content.ReadFromJsonAsync<HealthResponse>();
        health!.Healthy.Should().BeTrue();
        health.Hardware.HasHardwareAcceleration.Should().BeFalse();
    }

    [Fact]
    public async Task Reports_the_active_environment()
    {
        using var factory = CreateFactory(FakeMediaServer.Running(), FakeHardware.SoftwareOnly());
        using var client = factory.CreateClient();

        var health = await client.GetFromJsonAsync<HealthResponse>("/api/health");

        health!.Environment.Should().Be(Environments.Development);
    }

    /// <summary>
    /// Builds a host with the outward-facing ports swapped, and the MediaMTX hosted service
    /// removed so no child process is launched during a test run.
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory(
        IMediaServerSupervisor mediaServer,
        IHardwareInspector hardware) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IMediaServerSupervisor>();
                services.RemoveAll<IHardwareInspector>();

                services.AddSingleton(mediaServer);
                services.AddSingleton(hardware);
            });
        });

    private sealed class FakeMediaServer(MediaServerStatus status) : IMediaServerSupervisor
    {
        public MediaServerStatus Status { get; } = status;

        public static FakeMediaServer Running() =>
            new(new MediaServerStatus(MediaServerState.Running, "1.9.3", RestartCount: 0, LastError: null));

        public static FakeMediaServer Failed(string reason) =>
            new(new MediaServerStatus(MediaServerState.Failed, null, RestartCount: 0, LastError: reason));
    }

    private sealed class FakeHardware(HardwareCapabilities capabilities) : IHardwareInspector
    {
        public Task<HardwareCapabilities> InspectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(capabilities);

        public static FakeHardware WithNvidia() => new(new HardwareCapabilities(
            "8.0",
            [
                AccelerationProfile.Known.Single(profile => profile.Kind == AccelerationKind.Nvidia),
                AccelerationProfile.Software,
            ],
            [VideoCodec.H264, VideoCodec.H265]));

        public static FakeHardware SoftwareOnly() => new(new HardwareCapabilities(
            "8.0",
            [AccelerationProfile.Software],
            [VideoCodec.H264]));
    }
}
