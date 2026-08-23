using ThePlayer.Domain.Broadcasting;
using ThePlayer.Domain.Hardware;
using ThePlayer.Domain.Media;
using ThePlayer.Domain.Playback;

namespace ThePlayer.Application.Broadcasting;

/// <summary>
/// Decides what the server will do to a stream before sending it, and which modes can be offered.
/// </summary>
/// <remarks>
/// <para>
/// A pure function of <c>(format, mode, client support, hardware)</c>. No I/O, no state, nothing
/// injected - which is what turns the negotiation table into a set of plain unit tests instead of
/// something only observable by pointing at a real camera.
/// </para>
/// <para>
/// Concrete rather than behind a port: there is one implementation and no boundary to cross.
/// </para>
/// </remarks>
public sealed class BroadcastPlanner
{
    /// <summary>
    /// The codec WebRTC can be relied on to carry.
    /// <para>
    /// H.265 over WebRTC works only on Chrome 136+, only on Windows, and only with a capable GPU;
    /// Firefox needs flags. Since SDP negotiation happens before we could intervene, an H.265 feed
    /// heading for a <c>&lt;video&gt;</c> element has to be converted. This constant is where that
    /// judgement lives.
    /// </para>
    /// </summary>
    private const VideoCodec WebRtcSafeCodec = VideoCodec.H264;

    /// <summary>
    /// Works out the delivery plan for one stream.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The requested mode cannot serve this stream on this client. Call <see cref="Availability"/>
    /// first to avoid this - it reports the same conclusion with a reason.
    /// </exception>
    public BroadcastPlan Plan(
        VideoFormat format,
        PlaybackMode mode,
        ClientDecodeSupport clientSupport,
        HardwareCapabilities hardware)
    {
        var availability = Describe(mode, format, clientSupport);
        if (!availability.Available)
        {
            throw new InvalidOperationException(availability.Reason);
        }

        return mode switch
        {
            // The whole point of this mode: if the client can decode what the camera already
            // produces, the server touches nothing and its codec cost is zero.
            PlaybackMode.ClientDecoded when clientSupport.CanDecode(format.Codec) =>
                PassThrough(mode, format.Codec),

            // It cannot, so convert down to the one codec every browser decodes.
            PlaybackMode.ClientDecoded => Convert(mode, WebRtcSafeCodec, hardware),

            PlaybackMode.ServerAssisted when format.Codec == WebRtcSafeCodec =>
                PassThrough(mode, format.Codec),

            PlaybackMode.ServerAssisted => Convert(mode, WebRtcSafeCodec, hardware),

            // Always a full decode - that is the mode's entire purpose.
            PlaybackMode.ServerDecoded => Convert(mode, VideoCodec.Mjpeg, hardware),

            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown playback mode."),
        };
    }

    /// <summary>
    /// Reports every mode and whether it can be offered for this stream on this client.
    /// </summary>
    /// <remarks>
    /// Drives the mode toggle in the UI. Unavailable modes carry a sentence rather than just being
    /// greyed out, because a dead control with no explanation is worse than no control at all.
    /// </remarks>
    public IReadOnlyList<ModeAvailability> Availability(
        VideoFormat format,
        ClientDecodeSupport clientSupport) =>
        Enum.GetValues<PlaybackMode>()
            .Select(mode => Describe(mode, format, clientSupport))
            .ToList();

    private static ModeAvailability Describe(
        PlaybackMode mode,
        VideoFormat format,
        ClientDecodeSupport clientSupport)
    {
        if (format.Codec == VideoCodec.Unknown)
        {
            return ModeAvailability.No(mode, "The codec of this stream could not be identified.");
        }

        // The server-side modes work with anything: whatever the camera produces, the server
        // decodes it and hands the client something ordinary.
        if (mode is PlaybackMode.ServerAssisted or PlaybackMode.ServerDecoded)
        {
            return ModeAvailability.Yes(mode);
        }

        if (!clientSupport.WebCodecsAvailable)
        {
            return ModeAvailability.No(
                mode,
                "This browser does not support the WebCodecs API, so it cannot decode a stream directly.");
        }

        if (clientSupport.CanDecode(format.Codec))
        {
            return ModeAvailability.Yes(mode);
        }

        // It cannot decode the source, but converting to H.264 first would still let it play -
        // at the cost of the server work this mode exists to avoid.
        if (clientSupport.CanDecode(WebRtcSafeCodec))
        {
            return ModeAvailability.Yes(mode);
        }

        return ModeAvailability.No(
            mode,
            $"This browser reports no decoder for {Describe(format.Codec)} or H.264.");
    }

    private static BroadcastPlan PassThrough(PlaybackMode mode, VideoCodec codec) =>
        new(mode, codec, RequiresConversion: false, Acceleration: null);

    private static BroadcastPlan Convert(
        PlaybackMode mode,
        VideoCodec target,
        HardwareCapabilities hardware) =>
        new(mode, target, RequiresConversion: true, hardware.Preferred);

    private static string Describe(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "H.264",
        VideoCodec.H265 => "H.265",
        VideoCodec.Vp8 => "VP8",
        VideoCodec.Vp9 => "VP9",
        VideoCodec.Av1 => "AV1",
        VideoCodec.Mjpeg => "MJPEG",
        _ => "this codec",
    };
}
