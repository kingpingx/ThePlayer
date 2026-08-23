using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThePlayer.Application;
using ThePlayer.Domain.Monitoring;

namespace ThePlayer.Infrastructure.MediaServer;

/// <summary>Where MediaMTX lives, which ports it uses, and whether we own its lifetime.</summary>
public sealed class MediaMtxOptions
{
    public const string SectionName = "MediaMtx";

    /// <summary>
    /// Full path to the binary. When empty the supervisor looks in <c>tools/bin</c> and then on PATH.
    /// </summary>
    public string? BinaryPath { get; set; }

    /// <summary>
    /// Whether to launch and supervise the process. Set false when MediaMTX is run separately -
    /// in a container, or by hand during debugging - and the supervisor should only monitor it.
    /// </summary>
    public bool Supervise { get; set; } = true;

    public string ApiAddress { get; set; } = "127.0.0.1:9997";

    public string RtspAddress { get; set; } = ":8554";

    public string WebRtcAddress { get; set; } = ":8889";

    /// <summary>The UDP port WebRTC media flows over. Must be reachable for playback to work.</summary>
    public string WebRtcLocalUdpAddress { get; set; } = ":8189";

    /// <summary>
    /// Extra hosts to advertise as ICE candidates. Needed for LAN access, where the server's
    /// own view of its address is not the one clients can reach.
    /// </summary>
    public string[] WebRtcAdditionalHosts { get; set; } = [];

    /// <summary>How long to wait for the control API to answer before calling a start failed.</summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How many times to restart after an unexpected exit before giving up. Restarting forever
    /// against a misconfiguration just fills the log.
    /// </summary>
    public int MaxRestarts { get; set; } = 10;

    public string ApiBaseUrl => $"http://{NormaliseHost(ApiAddress)}";

    public string WebRtcBaseUrl => $"http://{NormaliseHost(WebRtcAddress)}";

    /// <summary>A bare ":8889" means "all interfaces"; to *call* it we need a real host.</summary>
    private static string NormaliseHost(string address) =>
        address.StartsWith(':') ? $"127.0.0.1{address}" : address;
}

/// <summary>
/// Owns the MediaMTX child process: finds the binary, writes its config, starts it, waits for its
/// control API to answer, and brings it back if it exits.
/// </summary>
/// <remarks>
/// <para>
/// A missing binary is reported, not thrown. The API must still start and <c>/api/health</c> must
/// still answer, because "MediaMTX is not installed, run tools/fetch-mediamtx" is exactly the
/// diagnosis the health endpoint exists to deliver. Crashing at startup would hide it.
/// </para>
/// <para>
/// Paths are registered over the control API rather than written into the config file, so camera
/// credentials stay in MediaMTX's memory instead of landing on disk in a file that might be
/// committed or baked into a container image.
/// </para>
/// </remarks>
public sealed class MediaMtxSupervisor(
    IOptions<MediaMtxOptions> options,
    IHttpClientFactory httpClientFactory,
    ILogger<MediaMtxSupervisor> logger) : BackgroundService, IMediaServerSupervisor
{
    /// <summary>
    /// MediaMTX announces itself on startup with a line like
    /// <c>INF MediaMTX v1.20.1, windows, amd64</c>. That banner is the only place it states its
    /// version - the v3 control API does not expose one - so the log pump captures it here.
    /// </summary>
    private static readonly Regex VersionBanner = new(
        @"MediaMTX\s+(v[\w.\-]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly MediaMtxOptions _options = options.Value;
    private MediaServerStatus _status = MediaServerStatus.NotStarted;
    private string? _reportedVersion;

    /// <summary>Read from request threads while the supervisor loop writes it.</summary>
    public MediaServerStatus Status => Volatile.Read(ref _status);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Supervise)
        {
            await MonitorExternalServerAsync(stoppingToken);
            return;
        }

        var binary = ResolveBinaryPath();
        if (binary is null)
        {
            SetStatus(MediaServerState.Failed, error:
                "MediaMTX was not found. Run tools/fetch-mediamtx.ps1 (Windows) or " +
                "tools/fetch-mediamtx.sh (Linux), or set MediaMtx:BinaryPath in configuration.");
            logger.LogError("{Error}", Status.LastError);
            return;
        }

        // A force-kill or crash of this process leaves MediaMTX running and holding the RTSP,
        // WebRTC and API ports, so the next start would launch a second copy that fails to bind
        // and then restart-loops against a confusing error. Adopting the survivor is both more
        // useful and more honest than fighting it.
        //
        // Killing children on a hard parent exit needs a Windows Job Object or PR_SET_PDEATHSIG
        // on Linux; that lands in Phase 6 with the rest of the shutdown hardening.
        if (await IsApiRespondingAsync(stoppingToken))
        {
            logger.LogWarning(
                "MediaMTX is already running on {ApiUrl} - most likely left over from a previous " +
                "run that did not shut down cleanly. Adopting it instead of starting another.",
                _options.ApiBaseUrl);

            await MonitorExternalServerAsync(stoppingToken);
            return;
        }

        var configPath = await WriteConfigFileAsync(stoppingToken);
        var restarts = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(binary, configPath, restarts, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MediaMTX supervision failed.");
                SetStatus(MediaServerState.Failed, error: ex.Message, restartCount: restarts);
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            restarts++;
            if (restarts > _options.MaxRestarts)
            {
                SetStatus(
                    MediaServerState.Failed,
                    error: $"MediaMTX exited {restarts} times; not restarting again.",
                    restartCount: restarts);
                logger.LogError("{Error}", Status.LastError);
                return;
            }

            // Back off so a misconfiguration does not spin. Capped so recovery from a transient
            // failure does not take minutes.
            var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, restarts), 30));
            SetStatus(MediaServerState.Restarting, restartCount: restarts);
            logger.LogWarning("MediaMTX exited. Restart {Count} in {Delay}.", restarts, delay);

            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task RunOnceAsync(
        string binary,
        string configPath,
        int restarts,
        CancellationToken stoppingToken)
    {
        SetStatus(MediaServerState.Starting, restartCount: restarts);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = binary,
                Arguments = $"\"{configPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(configPath)!,
            },
        };

        process.Start();
        logger.LogInformation("Started MediaMTX (pid {Pid}) from {Binary}.", process.Id, binary);

        // MediaMTX is chatty, and an undrained pipe eventually blocks it. Forwarding its output
        // into our log is also the only way its own startup errors are ever visible.
        var pumping = Task.WhenAll(
            PumpAsync(process.StandardOutput, LogLevel.Debug, stoppingToken),
            PumpAsync(process.StandardError, LogLevel.Warning, stoppingToken));

        try
        {
            if (await WaitForApiAsync(stoppingToken))
            {
                SetStatus(
                    MediaServerState.Running,
                    version: Volatile.Read(ref _reportedVersion),
                    restartCount: restarts);
                logger.LogInformation("MediaMTX is ready on {ApiUrl}.", _options.ApiBaseUrl);
            }
            else
            {
                SetStatus(
                    MediaServerState.Failed,
                    error: $"MediaMTX did not answer its control API within {_options.StartTimeout.TotalSeconds:0}s.",
                    restartCount: restarts);
            }

            await process.WaitForExitAsync(stoppingToken);
            logger.LogWarning("MediaMTX exited with code {ExitCode}.", process.ExitCode);
        }
        finally
        {
            await StopProcessAsync(process);
            await pumping.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
                .ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// When we do not own the process, the only thing that can be reported is whether it answers.
    /// </summary>
    private async Task MonitorExternalServerAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Not supervising MediaMTX; monitoring {ApiUrl} instead.", _options.ApiBaseUrl);

        while (!stoppingToken.IsCancellationRequested)
        {
            var responding = await IsApiRespondingAsync(stoppingToken);

            // No version here: we never saw this process start, so its banner went somewhere else.
            SetStatus(
                responding ? MediaServerState.Running : MediaServerState.Failed,
                error: responding ? null : $"No MediaMTX answering on {_options.ApiBaseUrl}.");

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    /// <summary>
    /// Looks in the configured location, then the tools directory beside the app, then PATH.
    /// The executable suffix differs by OS, which is the sort of assumption that only shows up
    /// when someone first deploys to Linux.
    /// </summary>
    private string? ResolveBinaryPath()
    {
        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "mediamtx.exe" : "mediamtx";

        if (!string.IsNullOrWhiteSpace(_options.BinaryPath))
        {
            return File.Exists(_options.BinaryPath) ? _options.BinaryPath : null;
        }

        // Walk up from the app directory looking for tools/bin. During development the binary sits
        // at the repository root while the app runs from bin/Debug/net8.0.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tools", "bin", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return FindOnPath(fileName);
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        return path
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim(), fileName))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Writes a minimal config next to the app. Deliberately declares no paths: those are added
    /// over the control API at runtime so credentials never touch disk.
    /// </summary>
    private async Task<string> WriteConfigFileAsync(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "mediamtx-runtime");
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "mediamtx.yml");

        var additionalHosts = _options.WebRtcAdditionalHosts.Length == 0
            ? "[]"
            : $"[{string.Join(", ", _options.WebRtcAdditionalHosts)}]";

        // $$ so that the empty "paths: {}" map stays literal; interpolations use {{ }}.
        var config = $$"""
            # Generated by ThePlayer at startup. Edits here are overwritten.
            # Stream paths are registered over the control API, never written to this file,
            # so that camera credentials are never persisted to disk.
            logLevel: info
            logDestinations: [stdout]

            api: yes
            apiAddress: {{_options.ApiAddress}}

            rtsp: yes
            rtspAddress: {{_options.RtspAddress}}

            webrtc: yes
            webrtcAddress: {{_options.WebRtcAddress}}
            webrtcLocalUDPAddress: {{_options.WebRtcLocalUdpAddress}}
            webrtcAdditionalHosts: {{additionalHosts}}

            hls: no
            rtmp: no
            srt: no

            paths: {}

            """;

        await File.WriteAllTextAsync(configPath, config, cancellationToken);
        return configPath;
    }

    private async Task<bool> WaitForApiAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.StartTimeout);

        while (!timeout.IsCancellationRequested)
        {
            if (await IsApiRespondingAsync(timeout.Token))
            {
                return true;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return false;
    }

    /// <summary>
    /// Probes the control API. Answering at all is the readiness signal - the v3 API exposes no
    /// version of its own, so the version comes from the process banner instead
    /// (see <see cref="_reportedVersion"/>).
    /// </summary>
    /// <returns>
    /// <c>true</c> when MediaMTX responds. A non-response is normal during startup, not an error
    /// worth logging.
    /// </returns>
    private async Task<bool> IsApiRespondingAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(nameof(MediaMtxSupervisor));
            client.Timeout = TimeSpan.FromSeconds(2);

            using var response = await client.GetAsync(
                $"{_options.ApiBaseUrl}/v3/config/global/get",
                cancellationToken);

            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task PumpAsync(StreamReader reader, LogLevel level, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                if (_reportedVersion is null && VersionBanner.Match(line) is { Success: true } match)
                {
                    Volatile.Write(ref _reportedVersion, match.Groups[1].Value);
                }

                logger.Log(level, "[mediamtx] {Line}", line);
            }
        }
        catch (Exception)
        {
            // The pipe closes when the process exits. Nothing useful to report.
        }
    }

    private async Task StopProcessAsync(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not stop MediaMTX cleanly.");
        }
    }

    private void SetStatus(
        MediaServerState state,
        string? version = null,
        string? error = null,
        int restartCount = 0) =>
        Volatile.Write(ref _status, new MediaServerStatus(state, version, restartCount, error));

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        SetStatus(MediaServerState.NotStarted);
    }
}
