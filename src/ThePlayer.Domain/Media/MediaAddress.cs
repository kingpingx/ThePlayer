using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace ThePlayer.Domain.Media;

/// <summary>Where a piece of media lives. Either a network stream or a file on disk.</summary>
public enum MediaAddressKind
{
    Rtsp,
    File,
}

/// <summary>
/// Where the media is. Validated on construction, and the only type in the system that
/// holds credentials.
/// <para>
/// RTSP addresses routinely arrive with a username and password embedded
/// (<c>rtsp://user:pass@host:554/stream</c>). This type keeps the credentials apart from the
/// display form so a password cannot reach a log line, an API response or an error message by
/// accident. <see cref="ToString"/> is always safe to print; <see cref="ToFFmpegInput"/> is the
/// single deliberate way to obtain the full form, and <see cref="Scrub"/> removes the credentials
/// from text produced elsewhere - notably FFmpeg's stderr, which echoes the input URL on failure.
/// </para>
/// </summary>
public sealed class MediaAddress : IEquatable<MediaAddress>
{
    private const string Redaction = "***";

    /// <summary>The address exactly as supplied. Never logged, never returned to a client.</summary>
    private readonly string _raw;

    /// <summary>
    /// Every rendering of the secret that might appear in text we did not produce: the password
    /// and the whole user-info segment, in both their escaped and unescaped forms. Ordered
    /// longest-first so that scrubbing replaces "user:pass" before it replaces "pass".
    /// </summary>
    private readonly string[] _secrets;

    private MediaAddress(
        MediaAddressKind kind,
        string raw,
        string display,
        string? userName,
        string[] secrets)
    {
        Kind = kind;
        _raw = raw;
        Display = display;
        UserName = userName;
        _secrets = secrets;
    }

    public MediaAddressKind Kind { get; }

    /// <summary>
    /// The address with any password replaced by <c>***</c>. Safe to log, display, or return
    /// from the API.
    /// </summary>
    public string Display { get; }

    /// <summary>The username, if the address carried one. Usernames are not secret; passwords are.</summary>
    public string? UserName { get; }

    public bool HasCredentials => _secrets.Length > 0;

    /// <summary>
    /// A stable, non-reversible identifier for this exact address, safe to use as a dictionary
    /// key or to print in diagnostics. Two equal addresses share a fingerprint; the fingerprint
    /// reveals nothing about the credentials.
    /// </summary>
    public string Fingerprint
    {
        get
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(_raw));
            return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }
    }

    /// <summary>
    /// Parses an RTSP URL or a file path.
    /// </summary>
    /// <exception cref="FormatException">The address is not usable. The message never contains the password.</exception>
    public static MediaAddress Parse(string? input)
    {
        if (!TryParse(input, out var address, out var error))
        {
            throw new FormatException(error);
        }

        return address;
    }

    /// <summary>
    /// Parses an RTSP URL or a file path, reporting why it failed rather than throwing.
    /// </summary>
    /// <remarks>
    /// The <paramref name="error"/> message is written to be safe to show a user: it describes the
    /// shape of the problem and never echoes the input, because the input may contain a password.
    /// </remarks>
    public static bool TryParse(
        string? input,
        [NotNullWhen(true)] out MediaAddress? address,
        [NotNullWhen(false)] out string? error)
    {
        address = null;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "An address is required.";
            return false;
        }

        var trimmed = input.Trim();

        if (LooksLikeRtsp(trimmed))
        {
            return TryParseRtsp(trimmed, out address, out error);
        }

        if (LooksLikeUnsupportedScheme(trimmed, out var scheme))
        {
            error = $"'{scheme}' addresses are not supported. Use an rtsp:// URL or a path to a video file.";
            return false;
        }

        return TryParseFile(trimmed, out address, out error);
    }

    /// <summary>
    /// The full address, credentials included, for handing to FFmpeg.
    /// <para>
    /// This is the one deliberate way out. It returns the address verbatim rather than
    /// re-serialising a parsed URL, because re-serialising risks changing the percent-encoding
    /// and handing FFmpeg a subtly different password than the one that was typed.
    /// </para>
    /// </summary>
    public string ToFFmpegInput() => _raw;

    /// <summary>
    /// Removes this address's credentials from arbitrary text.
    /// <para>
    /// FFmpeg echoes the input URL in its error output, so a failed connection would otherwise put
    /// the password straight into the log - and a <em>wrong</em> password produces a log line
    /// containing the <em>right</em> one. Every line of process output passes through here before
    /// it is logged or surfaced.
    /// </para>
    /// </summary>
    /// <returns>The text with every occurrence of the credentials replaced by <c>***</c>.</returns>
    public string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text) || _secrets.Length == 0)
        {
            return text ?? string.Empty;
        }

        var scrubbed = text;
        foreach (var secret in _secrets)
        {
            scrubbed = scrubbed.Replace(secret, Redaction, StringComparison.Ordinal);
        }

        return scrubbed;
    }

    public override string ToString() => Display;

    public bool Equals(MediaAddress? other) =>
        other is not null && string.Equals(_raw, other._raw, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as MediaAddress);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_raw);

    private static bool LooksLikeRtsp(string value) =>
        value.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeUnsupportedScheme(string value, out string scheme)
    {
        scheme = string.Empty;
        var separator = value.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        var candidate = value[..separator];

        // file:// is handled as a file path, not rejected as an unsupported scheme.
        if (candidate.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        scheme = candidate.ToLowerInvariant();
        return true;
    }

    private static bool TryParseRtsp(
        string input,
        [NotNullWhen(true)] out MediaAddress? address,
        [NotNullWhen(false)] out string? error)
    {
        address = null;

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            error = "That is not a valid RTSP URL.";
            return false;
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            error = "The RTSP URL is missing a host.";
            return false;
        }

        // Uri.UserInfo is the escaped "user:password" segment, or empty. The first colon
        // separates them; a password may itself contain colons, so only the first one splits.
        var userInfoEscaped = uri.UserInfo;
        string? userName = null;
        var secrets = Array.Empty<string>();
        var credentialsForDisplay = string.Empty;

        if (!string.IsNullOrEmpty(userInfoEscaped))
        {
            var colon = userInfoEscaped.IndexOf(':');
            var userEscaped = colon >= 0 ? userInfoEscaped[..colon] : userInfoEscaped;
            var passwordEscaped = colon >= 0 ? userInfoEscaped[(colon + 1)..] : string.Empty;

            userName = Uri.UnescapeDataString(userEscaped);

            if (passwordEscaped.Length > 0)
            {
                var passwordPlain = Uri.UnescapeDataString(passwordEscaped);

                // Both forms are scrubbed. FFmpeg may echo the URL exactly as given (escaped),
                // while other tooling may print a decoded value.
                secrets = BuildSecretList(userInfoEscaped, passwordEscaped, passwordPlain);
                credentialsForDisplay = $"{userName}:{Redaction}@";
            }
            else
            {
                credentialsForDisplay = $"{userName}@";
            }
        }

        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        var display = $"{uri.Scheme}://{credentialsForDisplay}{uri.Host}{port}{uri.PathAndQuery}";

        address = new MediaAddress(MediaAddressKind.Rtsp, input, display, userName, secrets);
        error = null;
        return true;
    }

    /// <summary>
    /// Assembles the strings to scrub, longest first so the whole user-info segment is replaced
    /// before the bare password would match inside it. Duplicates are dropped, which is the common
    /// case: a password needing no escaping has identical escaped and plain forms.
    /// </summary>
    private static string[] BuildSecretList(string userInfoEscaped, string passwordEscaped, string passwordPlain)
    {
        var candidates = new List<string>(3) { userInfoEscaped, passwordEscaped, passwordPlain };

        return candidates
            .Where(candidate => candidate.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(candidate => candidate.Length)
            .ToArray();
    }

    private static bool TryParseFile(
        string input,
        [NotNullWhen(true)] out MediaAddress? address,
        [NotNullWhen(false)] out string? error)
    {
        address = null;
        var path = input;

        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(path, UriKind.Absolute, out var fileUri))
            {
                error = "That is not a valid file:// URL.";
                return false;
            }

            path = fileUri.LocalPath;
        }

        if (path.AsSpan().IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            error = "The file path contains characters that are not valid in a path.";
            return false;
        }

        // Existence is deliberately not checked here. Domain performs no I/O; whether the file is
        // readable is discovered when the media inspector runs against it.
        if (!Path.IsPathFullyQualified(path))
        {
            error = "Use a full path to the video file, not a relative one.";
            return false;
        }

        var normalised = Path.GetFullPath(path);

        // A file path holds no credentials, so the display form is the path itself.
        address = new MediaAddress(
            MediaAddressKind.File,
            normalised,
            normalised,
            userName: null,
            secrets: Array.Empty<string>());
        error = null;
        return true;
    }
}
