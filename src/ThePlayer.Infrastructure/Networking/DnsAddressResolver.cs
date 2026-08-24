using System.Net;
using ThePlayer.Application;

namespace ThePlayer.Infrastructure.Networking;

/// <summary>
/// Resolves host names through the operating system resolver.
/// </summary>
/// <remarks>
/// Thin on purpose. Everything worth deciding about the answer is policy and lives in
/// <c>AddressGuard</c>; this only crosses the boundary.
/// </remarks>
public sealed class DnsAddressResolver : IAddressResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(
        string host,
        CancellationToken cancellationToken = default) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken);
}
