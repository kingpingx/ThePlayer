using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace ThePlayer.Application.Security;

/// <summary>Who may use this deployment at all.</summary>
public sealed class ApiKeyOptions
{
    public const string SectionName = "ApiKey";

    /// <summary>
    /// Whether a key is demanded. Off by default, on in Production.
    /// </summary>
    /// <remarks>
    /// The default is the development one for the same reason <see cref="AddressPolicyOptions"/>
    /// defaults to permissive: on a laptop, a server that interrogates you before it will play the
    /// file on your desk is an obstacle rather than a protection.
    /// </remarks>
    public bool Required { get; set; }

    /// <summary>
    /// The accepted keys. More than one so a key can be rotated without a window where neither the
    /// old nor the new one works.
    /// </summary>
    /// <remarks>
    /// Supplied through configuration, which in a real deployment means an environment variable or
    /// a secret store - never <c>appsettings.json</c>, which is committed.
    /// </remarks>
    public string[] Keys { get; set; } = [];
}

/// <summary>
/// Checks the key on a request, in the several places a browser is capable of putting one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The awkward part is not the comparison, it is the transport.</b> A header is the right place
/// for a credential, and <c>fetch</c> can set one - but <c>EventSource</c> and <c>WebSocket</c>
/// cannot set headers at all. Two of this system's endpoints are exactly those, so a header-only
/// scheme would leave them either unreachable from a browser or unprotected.
/// </para>
/// <para>
/// So there are two accepted forms, and they are not equally good:
/// </para>
/// <list type="bullet">
/// <item>
/// <c>X-Api-Key</c>, for everything a <c>fetch</c> can reach. This is the one to use.
/// </item>
/// <item>
/// <c>?key=</c>, accepted only where a browser has no alternative. A query string is logged by
/// every proxy it passes and lands in browser history, so this is a concession rather than a
/// design. The upgrade path is a short-lived ticket issued by an authenticated request and spent
/// on the stream - noted in docs/SECURITY.md rather than built.
/// </item>
/// </list>
/// <para>
/// The frame socket is deliberately <em>not</em> in either list. It is reached with a viewer id
/// that only an authorised <c>POST /api/watch</c> can produce, which makes the id itself the
/// credential - a capability rather than a shared secret, and a better fit than putting the key in
/// a URL a second time.
/// </para>
/// </remarks>
public sealed class ApiKeyGuard(IOptions<ApiKeyOptions> options)
{
    /// <summary>Where a key may be presented.</summary>
    public const string HeaderName = "X-Api-Key";

    /// <summary>Where a key may be presented when the browser cannot set a header.</summary>
    public const string QueryName = "key";

    private readonly ApiKeyOptions _options = options.Value;

    /// <summary>Whether this deployment demands a key at all.</summary>
    public bool Required => _options.Required;

    /// <summary>
    /// Whether the deployment is configured coherently.
    /// </summary>
    /// <remarks>
    /// "Required, with no keys configured" is a server that refuses everything, and it is a far
    /// more likely mistake than it sounds - one missing environment variable. The host refuses to
    /// start on it rather than serving 401s that look like a client problem.
    /// </remarks>
    public bool IsUsable => !_options.Required || _options.Keys.Any(key => !string.IsNullOrWhiteSpace(key));

    /// <summary>
    /// Whether a presented key is accepted.
    /// </summary>
    /// <remarks>
    /// Every configured key is compared, and the comparison is fixed-time. Returning early on the
    /// first mismatched byte leaks the key one byte at a time to anyone patient enough to measure
    /// it - a real attack against a long-lived shared secret, free to get right here and impossible
    /// to retrofit convincingly.
    /// <para>
    /// The comparison is over SHA-256 digests rather than the keys themselves. Not for secrecy -
    /// both sides are in memory already - but because a fixed-time comparison of different-length
    /// inputs still reveals the length, and hashing makes every comparison 32 bytes.
    /// </para>
    /// </remarks>
    public bool Accepts(string? presented)
    {
        if (!_options.Required)
        {
            return true;
        }

        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var offered = Digest(presented);
        var accepted = false;

        foreach (var candidate in _options.Keys)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            // No short-circuit: every key is compared even once one has matched, so the time taken
            // does not depend on which key was presented or on how many are configured.
            accepted |= CryptographicOperations.FixedTimeEquals(offered, Digest(candidate));
        }

        return accepted;
    }

    private static byte[] Digest(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
