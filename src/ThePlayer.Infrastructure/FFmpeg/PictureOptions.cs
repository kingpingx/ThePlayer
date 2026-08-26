namespace ThePlayer.Infrastructure.FFmpeg;

/// <summary>
/// Quality and size for the mode that sends pictures rather than video.
/// </summary>
/// <remarks>
/// Configurable because the tradeoff genuinely changes with the network. MJPEG has no inter-frame
/// compression, so a stream of it runs five to ten times the bandwidth of H.264 at comparable
/// quality. On a LAN that buys a client which decodes nothing at all; over a WAN it is the reason
/// the mode is unusable, and the two knobs here are what make it usable again.
/// </remarks>
public sealed class PictureOptions
{
    public const string SectionName = "Pictures";

    /// <summary>
    /// FFmpeg's <c>-q:v</c> for the JPEG encoder: 2 is near-lossless, 31 is the worst it will do.
    /// </summary>
    /// <remarks>
    /// 5 is a deliberately conservative default. The point of this mode is to show what full server
    /// decoding costs, and quietly shipping soft pictures would understate the bandwidth half of
    /// that answer.
    /// </remarks>
    public int Quality { get; set; } = 5;

    /// <summary>
    /// Scale pictures down to this width before encoding, or <c>0</c> to send them at source size.
    /// </summary>
    /// <remarks>
    /// Off by default for the same reason the quality is high: the honest measurement first, and
    /// the mitigation only when a deployment asks for it.
    /// </remarks>
    public int MaxWidth { get; set; }
}
