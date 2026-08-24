using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThePlayer.Domain.Media;

namespace ThePlayer.Application.Security;

/// <summary>What this deployment is willing to connect to.</summary>
/// <remarks>
/// The defaults are the development ones - anything goes - because on a laptop the whole point is
/// to point it at the camera on your desk. A hosted deployment turns them on.
/// </remarks>
public sealed class AddressPolicyOptions
{
    public const string SectionName = "AddressPolicy";

    /// <summary>
    /// Whether an address may resolve to a private, loopback or link-local network.
    /// </summary>
    /// <remarks>
    /// The one setting that matters on a public host. A server that dials whatever address it is
    /// handed is a request-forgery surface: without this, anyone who finds the URL can make it
    /// probe the network it sits inside and read the results back out of the error messages.
    /// </remarks>
    public bool AllowPrivateNetworks { get; set; } = true;

    /// <summary>
    /// Directories a file address may live under. Empty means anywhere on disk.
    /// </summary>
    /// <remarks>
    /// A hosted deployment sets this to the directory its bundled samples are in. Otherwise the
    /// address bar is a way to ask the server to open arbitrary paths and report what it found.
    /// </remarks>
    public string[] AllowedFileRoots { get; set; } = [];
}

/// <summary>Raised when an address is one this deployment refuses to connect to.</summary>
/// <remarks>The message is written to be shown to a user and names no internal detail.</remarks>
public sealed class AddressNotAllowedException(string message) : Exception(message);

/// <summary>
/// Decides whether this deployment will connect to an address at all, before anything tries to.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="BroadcastPlanner"/> on purpose: that decides how to deliver something
/// we have agreed to fetch, this decides whether to fetch it. Running first means a refused address
/// never reaches ffprobe, so nothing about the target - not even how long it took to fail - leaks
/// back to whoever asked.
/// </para>
/// <para>
/// <b>Known limitation.</b> The name is resolved here and resolved again by FFmpeg, so a name that
/// answers with a public address now and a private one a moment later would slip through. Pinning
/// the resolved address and handing FFmpeg that instead is the fix, and it is not built - see
/// docs/SECURITY.md.
/// </para>
/// </remarks>
public sealed class AddressGuard(
    IAddressResolver resolver,
    IOptions<AddressPolicyOptions> options,
    ILogger<AddressGuard> logger)
{
    private readonly AddressPolicyOptions _options = options.Value;

    /// <exception cref="AddressNotAllowedException">Policy refuses this address.</exception>
    public async Task EnsureAllowedAsync(MediaAddress address, CancellationToken cancellationToken = default)
    {
        if (address.Kind == MediaAddressKind.File)
        {
            EnsureFileAllowed(address);
            return;
        }

        if (_options.AllowPrivateNetworks)
        {
            return;
        }

        var host = address.Host;
        if (string.IsNullOrEmpty(host))
        {
            throw new AddressNotAllowedException("That address has no host.");
        }

        // A literal address needs no lookup, and checking it first means a hostile literal never
        // reaches the resolver either.
        if (IPAddress.TryParse(host, out var literal))
        {
            EnsurePublic(literal, address);
            return;
        }

        IReadOnlyList<IPAddress> resolved;

        try
        {
            resolved = await resolver.ResolveAsync(host, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AddressNotAllowedException($"Could not look up {address.Display}.");
        }

        if (resolved.Count == 0)
        {
            throw new AddressNotAllowedException($"Could not look up {address.Display}.");
        }

        // Every answer must be acceptable. A name that returns one public address and one private
        // one is exactly the shape an attacker would choose.
        foreach (var candidate in resolved)
        {
            EnsurePublic(candidate, address);
        }
    }

    private void EnsureFileAllowed(MediaAddress address)
    {
        if (_options.AllowedFileRoots.Length == 0)
        {
            return;
        }

        var path = Path.GetFullPath(address.ToFFmpegInput());

        foreach (var root in _options.AllowedFileRoots)
        {
            var full = Path.GetFullPath(root);

            // TrailingSeparator so that "/srv/media-secret" does not pass a "/srv/media" root.
            var prefix = full.EndsWith(Path.DirectorySeparatorChar)
                ? full
                : full + Path.DirectorySeparatorChar;

            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        logger.LogWarning("Refused a file address outside the allowed roots.");
        throw new AddressNotAllowedException(
            "This server only plays the sample files bundled with it. Paste an RTSP URL instead.");
    }

    private void EnsurePublic(IPAddress candidate, MediaAddress address)
    {
        if (!IsPrivate(candidate))
        {
            return;
        }

        // The address is logged only as its display form, which is already redacted, and the
        // resolved IP is not echoed to the caller - saying which internal address it mapped to
        // would answer the question the probe was asking.
        logger.LogWarning("Refused {Address}: it resolves to a private network.", address);

        throw new AddressNotAllowedException(
            $"{address.Display} points at a private network, which this server will not connect to.");
    }

    /// <summary>
    /// Whether an address belongs to a range that is not reachable from the public internet, and
    /// so cannot be a legitimate target for a hosted deployment.
    /// </summary>
    private static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                // ::ffff:10.0.0.1 is 10.0.0.1 wearing a hat.
                return IsPrivate(address.MapToIPv4());
            }

            return IPAddress.IPv6Loopback.Equals(address) ||
                   IPAddress.IPv6Any.Equals(address) ||
                   address.IsIPv6LinkLocal ||
                   address.IsIPv6SiteLocal ||
                   address.IsIPv6UniqueLocal;
        }

        var octets = address.GetAddressBytes();

        return octets[0] switch
        {
            0 => true,                                      // "this network"
            10 => true,                                     // RFC 1918
            127 => true,                                    // loopback
            169 when octets[1] == 254 => true,              // link-local, and cloud metadata
            172 when octets[1] >= 16 && octets[1] <= 31 => true,  // RFC 1918
            192 when octets[1] == 168 => true,              // RFC 1918
            100 when octets[1] >= 64 && octets[1] <= 127 => true, // carrier-grade NAT
            >= 224 => true,                                 // multicast and reserved
            _ => false,
        };
    }
}
