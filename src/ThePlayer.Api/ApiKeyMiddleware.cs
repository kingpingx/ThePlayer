using ThePlayer.Application.Security;

namespace ThePlayer.Api;

/// <summary>
/// Refuses requests that do not carry the key, where this deployment demands one.
/// </summary>
/// <remarks>
/// <para>
/// Middleware rather than a filter on each endpoint, because the failure mode of the per-endpoint
/// version is silent: a route added later without the attribute is open, and nothing says so. Here
/// the default is closed and the exceptions are a short list that has to be read on the way past.
/// </para>
/// <para>
/// The exceptions, and why each one is safe:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>The player itself</b> - static files. Refusing them would leave a visitor with a 401 in place
/// of the page that could ask them for a key.
/// </item>
/// <item>
/// <b><c>GET /api/health</c></b>, in reduced form. Container and uptime probes cannot easily carry
/// a secret, and a liveness check that needs one tends to end up disabled. Unauthenticated callers
/// get the verdict and nothing else - see <see cref="Endpoints"/>.
/// </item>
/// <item>
/// <b>The frame socket</b> - a browser cannot put a header on a <c>WebSocket</c>, and the viewer id
/// in its path is already a capability only an authorised watch call can mint. Guarding it here
/// would mean putting the key in a URL for no gain.
/// </item>
/// </list>
/// </remarks>
public sealed class ApiKeyMiddleware(RequestDelegate next, ApiKeyGuard guard, ILogger<ApiKeyMiddleware> logger)
{
    /// <summary>
    /// Paths that are never challenged. Prefixes, matched case-insensitively.
    /// </summary>
    /// <remarks>
    /// Deliberately a literal list rather than a convention. A rule like "anything under /public"
    /// invites the next person to widen it by moving a file.
    /// </remarks>
    private static readonly string[] Unguarded =
    [
        "/api/health",
        "/ws/frames/",
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        if (!guard.Required || !IsGuarded(context.Request.Path))
        {
            await next(context);
            return;
        }

        if (guard.Accepts(Presented(context.Request)))
        {
            await next(context);
            return;
        }

        // No detail about what was wrong with the key, or whether one was recognised. "Which of
        // these is nearly right" is the only question the caller is asking.
        logger.LogWarning(
            "Refused an unauthorised {Method} {Path}.",
            context.Request.Method,
            context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = $"{ApiKeyGuard.HeaderName} realm=\"ThePlayer\"";

        await context.Response.WriteAsJsonAsync(new
        {
            title = "A key is required",
            status = StatusCodes.Status401Unauthorized,
            detail = $"Send this deployment's key in the {ApiKeyGuard.HeaderName} header.",
        });
    }

    /// <summary>
    /// Everything under <c>/api</c> and <c>/ws</c> except the listed exceptions.
    /// </summary>
    /// <remarks>
    /// Static files fall outside both prefixes and are served unchallenged, which is what lets the
    /// page load and ask for a key rather than replacing itself with a 401.
    /// </remarks>
    private static bool IsGuarded(PathString path)
    {
        var value = path.Value ?? string.Empty;

        if (!value.StartsWith("/api", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("/ws", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !Unguarded.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The key the caller offered, from the header or - where a browser has no header to offer it
    /// in - the query string.
    /// </summary>
    private static string? Presented(HttpRequest request)
    {
        if (request.Headers.TryGetValue(ApiKeyGuard.HeaderName, out var header) &&
            header.Count > 0 &&
            !string.IsNullOrWhiteSpace(header[0]))
        {
            return header[0];
        }

        return request.Query.TryGetValue(ApiKeyGuard.QueryName, out var query) && query.Count > 0
            ? query[0]
            : null;
    }
}
