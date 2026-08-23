using FluentAssertions;
using ThePlayer.Domain.Media;

namespace ThePlayer.Domain.Tests.Media;

/// <summary>
/// <see cref="MediaAddress"/> is the only type that holds a password, so these tests are the
/// guarantee that it never leaks one. The redaction cases matter more than the parsing cases.
/// </summary>
public class MediaAddressTests
{
    private const string Password = "hunter2";
    private const string CredentialledUrl = $"rtsp://admin:{Password}@192.168.1.64:554/Streaming/Channels/101";

    /// <summary>A path that is fully qualified on whichever OS the tests are running on.</summary>
    private static string SampleFilePath =>
        OperatingSystem.IsWindows() ? @"D:\videos\clip.mp4" : "/videos/clip.mp4";

    public class Parsing
    {
        [Fact]
        public void Recognises_an_rtsp_url()
        {
            var address = MediaAddress.Parse(CredentialledUrl);

            address.Kind.Should().Be(MediaAddressKind.Rtsp);
            address.UserName.Should().Be("admin");
            address.HasCredentials.Should().BeTrue();
        }

        [Fact]
        public void Recognises_rtsps()
        {
            MediaAddress.Parse("rtsps://camera.local/stream").Kind.Should().Be(MediaAddressKind.Rtsp);
        }

        [Fact]
        public void Recognises_a_file_path()
        {
            var address = MediaAddress.Parse(SampleFilePath);

            address.Kind.Should().Be(MediaAddressKind.File);
            address.HasCredentials.Should().BeFalse();
            address.UserName.Should().BeNull();
        }

        [Fact]
        public void Recognises_a_file_url()
        {
            var url = OperatingSystem.IsWindows() ? "file:///D:/videos/clip.mp4" : "file:///videos/clip.mp4";

            var address = MediaAddress.Parse(url);

            address.Kind.Should().Be(MediaAddressKind.File);
            address.Display.Should().EndWith("clip.mp4");
        }

        [Fact]
        public void Trims_surrounding_whitespace()
        {
            // Addresses arrive pasted from a UI, so stray whitespace is the normal case.
            MediaAddress.Parse("  rtsp://camera.local/stream  ").Display
                .Should().Be("rtsp://camera.local/stream");
        }

        [Fact]
        public void Keeps_a_non_default_port_but_drops_the_default_one()
        {
            MediaAddress.Parse("rtsp://camera.local:8554/stream").Display
                .Should().Contain(":8554");

            MediaAddress.Parse("rtsp://camera.local:554/stream").Display
                .Should().Be("rtsp://camera.local:554/stream");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Rejects_an_empty_address(string? input)
        {
            MediaAddress.TryParse(input, out _, out var error).Should().BeFalse();
            error.Should().Contain("required");
        }

        [Theory]
        [InlineData("http://example.com/video.mp4", "http")]
        [InlineData("ftp://example.com/video.mp4", "ftp")]
        [InlineData("HTTPS://example.com/video.mp4", "https")]
        public void Rejects_an_unsupported_scheme_and_names_it(string input, string expectedScheme)
        {
            MediaAddress.TryParse(input, out _, out var error).Should().BeFalse();
            error.Should().Contain(expectedScheme);
        }

        [Fact]
        public void Rejects_a_relative_path()
        {
            MediaAddress.TryParse("videos/clip.mp4", out _, out var error).Should().BeFalse();
            error.Should().Contain("full path");
        }

        [Fact]
        public void Rejects_an_rtsp_url_with_no_host()
        {
            MediaAddress.TryParse("rtsp:///stream", out _, out var error).Should().BeFalse();
            error.Should().Contain("host");
        }

        [Fact]
        public void Parse_throws_with_the_reason_TryParse_would_have_given()
        {
            var act = () => MediaAddress.Parse("videos/clip.mp4");

            act.Should().Throw<FormatException>().WithMessage("*full path*");
        }
    }

    public class Redaction
    {
        [Fact]
        public void Display_hides_the_password_but_keeps_everything_else()
        {
            var address = MediaAddress.Parse(CredentialledUrl);

            address.Display.Should().Be("rtsp://admin:***@192.168.1.64:554/Streaming/Channels/101");
            address.Display.Should().NotContain(Password);
        }

        [Fact]
        public void ToString_is_the_display_form()
        {
            var address = MediaAddress.Parse(CredentialledUrl);

            // Anything that interpolates the address - a log template, a string concat - gets the
            // safe form without having to remember to ask for it.
            $"connecting to {address}".Should().NotContain(Password);
            address.ToString().Should().Be(address.Display);
        }

        [Fact]
        public void A_username_without_a_password_is_shown_in_full()
        {
            // Usernames are not secret; only passwords are.
            MediaAddress.Parse("rtsp://admin@camera.local/stream").Display
                .Should().Be("rtsp://admin@camera.local/stream");
        }

        [Fact]
        public void ToFFmpegInput_returns_the_address_verbatim()
        {
            // Verbatim rather than re-serialised: re-encoding a URL risks handing FFmpeg a
            // subtly different password than the one that was typed.
            MediaAddress.Parse(CredentialledUrl).ToFFmpegInput().Should().Be(CredentialledUrl);
        }

        [Fact]
        public void An_escaped_password_survives_the_round_trip_to_ffmpeg()
        {
            const string url = "rtsp://admin:p%40ssw%3Ard@camera.local/stream";

            var address = MediaAddress.Parse(url);

            address.ToFFmpegInput().Should().Be(url);
            address.Display.Should().Be("rtsp://admin:***@camera.local/stream");
        }

        [Fact]
        public void A_password_containing_a_colon_splits_on_the_first_one_only()
        {
            var address = MediaAddress.Parse("rtsp://admin:pass:with:colons@camera.local/stream");

            address.UserName.Should().Be("admin");
            address.Scrub("pass:with:colons").Should().Be("***");
        }
    }

    public class Scrubbing
    {
        [Fact]
        public void Removes_the_password_from_arbitrary_text()
        {
            var address = MediaAddress.Parse(CredentialledUrl);

            address.Scrub($"failed to open {CredentialledUrl}: 401 Unauthorized")
                .Should().NotContain(Password);
        }

        [Fact]
        public void Removes_a_bare_password_even_outside_a_url()
        {
            MediaAddress.Parse(CredentialledUrl).Scrub($"the password is {Password}")
                .Should().Be("the password is ***");
        }

        [Fact]
        public void Scrubs_an_ffmpeg_error_line_that_echoes_the_input()
        {
            // The leak this whole mechanism exists for: FFmpeg prints the input URL on failure, so
            // a WRONG password produces a log line containing the RIGHT one.
            var address = MediaAddress.Parse(CredentialledUrl);
            const string ffmpegStderr =
                $"[rtsp @ 0x55f1c0] method DESCRIBE failed: 401 Unauthorized\n{CredentialledUrl}: Server returned 401";

            var scrubbed = address.Scrub(ffmpegStderr);

            scrubbed.Should().NotContain(Password);
            scrubbed.Should().Contain("401 Unauthorized", "the diagnostic value of the message must survive");
        }

        [Fact]
        public void Removes_both_the_escaped_and_decoded_forms_of_a_password()
        {
            // FFmpeg may echo the URL exactly as given, while other tooling prints a decoded value.
            var address = MediaAddress.Parse("rtsp://admin:p%40ss@camera.local/stream");

            address.Scrub("escaped p%40ss and decoded p@ss").Should().Be("escaped *** and decoded ***");
        }

        [Fact]
        public void Replaces_the_whole_user_info_segment_rather_than_leaving_a_username_glued_to_it()
        {
            // Longest-first ordering: "admin:hunter2" is replaced as a unit, not as "admin:***".
            MediaAddress.Parse(CredentialledUrl).Scrub("admin:hunter2")
                .Should().Be("***");
        }

        [Fact]
        public void Leaves_text_alone_when_there_are_no_credentials()
        {
            var address = MediaAddress.Parse("rtsp://camera.local/stream");

            address.Scrub("nothing secret here").Should().Be("nothing secret here");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Handles_empty_input(string? text)
        {
            MediaAddress.Parse(CredentialledUrl).Scrub(text).Should().BeEmpty();
        }
    }

    public class Fingerprinting
    {
        [Fact]
        public void Is_stable_for_the_same_address()
        {
            MediaAddress.Parse(CredentialledUrl).Fingerprint
                .Should().Be(MediaAddress.Parse(CredentialledUrl).Fingerprint);
        }

        [Fact]
        public void Differs_between_addresses()
        {
            MediaAddress.Parse("rtsp://a.local/stream").Fingerprint
                .Should().NotBe(MediaAddress.Parse("rtsp://b.local/stream").Fingerprint);
        }

        [Fact]
        public void Reveals_nothing_about_the_credentials()
        {
            // The fingerprint is used as a dedupe key and printed in diagnostics, so it must be
            // safe even though it is derived from an address containing a password.
            var fingerprint = MediaAddress.Parse(CredentialledUrl).Fingerprint;

            fingerprint.Should().NotContain(Password);
            fingerprint.Should().NotContain("admin");
            fingerprint.Should().MatchRegex("^[0-9a-f]{16}$");
        }
    }

    public class Equality
    {
        [Fact]
        public void Two_identical_addresses_are_equal()
        {
            MediaAddress.Parse(CredentialledUrl).Should().Be(MediaAddress.Parse(CredentialledUrl));
        }

        [Fact]
        public void Addresses_differing_only_by_password_are_not_equal()
        {
            MediaAddress.Parse("rtsp://admin:one@camera.local/s")
                .Should().NotBe(MediaAddress.Parse("rtsp://admin:two@camera.local/s"));
        }

        [Fact]
        public void Works_as_a_dictionary_key()
        {
            var map = new Dictionary<MediaAddress, int> { [MediaAddress.Parse(CredentialledUrl)] = 1 };

            map.ContainsKey(MediaAddress.Parse(CredentialledUrl)).Should().BeTrue();
        }
    }

    /// <summary>
    /// A failed parse must not echo the input, because the input may be a URL with a password in
    /// it. This is easy to regress by adding a helpful-looking "'{input}' is not valid" message.
    /// </summary>
    public class ErrorMessages
    {
        [Theory]
        [InlineData($"http://admin:{Password}@example.com/video")]
        [InlineData($"ftp://admin:{Password}@example.com/video")]
        [InlineData($"relative/path/{Password}.mp4")]
        public void Never_contain_the_input(string input)
        {
            MediaAddress.TryParse(input, out _, out var error).Should().BeFalse();

            error.Should().NotContain(Password);
            error.Should().NotContain("admin");
        }
    }
}
