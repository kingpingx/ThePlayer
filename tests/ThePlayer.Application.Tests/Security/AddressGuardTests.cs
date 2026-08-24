using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ThePlayer.Application.Security;
using ThePlayer.Domain.Media;

namespace ThePlayer.Application.Tests.Security;

/// <summary>
/// What a deployment will and will not dial.
/// </summary>
/// <remarks>
/// This is the control that stops a public instance being used to probe the network it sits in, so
/// the cases that matter are the ones where an attacker gets to choose the input: a literal private
/// address, a name that resolves to one, and a name that resolves to both a public address and a
/// private one.
/// </remarks>
public class AddressGuardTests
{
    private readonly IAddressResolver _resolver = Substitute.For<IAddressResolver>();

    private AddressGuard Guard(bool allowPrivate = false, params string[] fileRoots) =>
        new(
            _resolver,
            Options.Create(new AddressPolicyOptions
            {
                AllowPrivateNetworks = allowPrivate,
                AllowedFileRoots = fileRoots,
            }),
            NullLogger<AddressGuard>.Instance);

    private void Resolves(string host, params string[] addresses) =>
        _resolver.ResolveAsync(host, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<IPAddress>)addresses.Select(IPAddress.Parse).ToList());

    public class WhenPrivateNetworksAreBlocked : AddressGuardTests
    {
        [Theory]
        [InlineData("10.0.0.5")]
        [InlineData("172.16.4.1")]
        [InlineData("172.31.255.254")]
        [InlineData("192.168.1.64")]
        [InlineData("127.0.0.1")]
        [InlineData("169.254.169.254")]   // the cloud metadata endpoint, the classic target
        [InlineData("100.64.0.1")]        // carrier-grade NAT
        [InlineData("0.0.0.0")]
        public async Task A_literal_private_address_is_refused_without_a_lookup(string host)
        {
            var act = async () => await Guard().EnsureAllowedAsync(
                MediaAddress.Parse($"rtsp://{host}:554/stream"));

            await act.Should().ThrowAsync<AddressNotAllowedException>();

            // Never resolved, so a hostile literal does not even reach the resolver.
            await _resolver.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
        }

        [Theory]
        [InlineData("8.8.8.8")]
        [InlineData("172.15.0.1")]        // just outside 172.16/12
        [InlineData("172.32.0.1")]        // just outside the other end
        [InlineData("192.167.1.1")]       // adjacent to 192.168/16, and public
        public async Task A_literal_public_address_is_allowed(string host)
        {
            var act = async () => await Guard().EnsureAllowedAsync(
                MediaAddress.Parse($"rtsp://{host}:554/stream"));

            await act.Should().NotThrowAsync();
        }

        [Fact]
        public async Task A_name_that_resolves_to_a_private_address_is_refused()
        {
            Resolves("camera.internal", "10.1.2.3");

            var act = async () => await Guard().EnsureAllowedAsync(
                MediaAddress.Parse("rtsp://camera.internal/stream"));

            await act.Should().ThrowAsync<AddressNotAllowedException>();
        }

        [Fact]
        public async Task A_name_that_resolves_to_both_is_refused()
        {
            // The shape an attacker would actually choose: one answer that passes a naive check and
            // one that is the real target.
            Resolves("mixed.example.com", "93.184.216.34", "192.168.0.10");

            var act = async () => await Guard().EnsureAllowedAsync(
                MediaAddress.Parse("rtsp://mixed.example.com/stream"));

            await act.Should().ThrowAsync<AddressNotAllowedException>();
        }

        [Fact]
        public async Task An_IPv4_address_wearing_an_IPv6_hat_is_still_private()
        {
            Resolves("sneaky.example.com", "::ffff:10.0.0.1");

            var act = async () => await Guard().EnsureAllowedAsync(
                MediaAddress.Parse("rtsp://sneaky.example.com/stream"));

            await act.Should().ThrowAsync<AddressNotAllowedException>();
        }

        [Theory]
        [InlineData("::1")]
        [InlineData("fe80::1")]
        [InlineData("fd00::1")]
        public async Task Private_IPv6_ranges_are_refused(string address)
        {
            Resolves("six.example.com", address);

            var act = async () => await Guard().EnsureAllowedAsync(
                MediaAddress.Parse("rtsp://six.example.com/stream"));

            await act.Should().ThrowAsync<AddressNotAllowedException>();
        }

        [Fact]
        public async Task A_name_that_resolves_to_nothing_is_refused()
        {
            _resolver.ResolveAsync("void.example.com", Arg.Any<CancellationToken>())
                .Returns((IReadOnlyList<IPAddress>)[]);

            var act = async () => await Guard().EnsureAllowedAsync(
                MediaAddress.Parse("rtsp://void.example.com/stream"));

            await act.Should().ThrowAsync<AddressNotAllowedException>();
        }

        [Fact]
        public async Task A_lookup_that_fails_is_refused_rather_than_waved_through()
        {
            _resolver.ResolveAsync("broken.example.com", Arg.Any<CancellationToken>())
                .Returns<IReadOnlyList<IPAddress>>(_ => throw new InvalidOperationException("dns is down"));

            var act = async () => await Guard().EnsureAllowedAsync(
                MediaAddress.Parse("rtsp://broken.example.com/stream"));

            // Failing open would make the whole control a formality: break DNS, reach anything.
            await act.Should().ThrowAsync<AddressNotAllowedException>();
        }

        [Fact]
        public async Task The_refusal_never_says_which_internal_address_it_found()
        {
            Resolves("camera.internal", "10.1.2.3");

            var act = async () => await Guard().EnsureAllowedAsync(
                MediaAddress.Parse("rtsp://camera.internal/stream"));

            // Saying "that maps to 10.1.2.3" would answer the exact question the probe was asking.
            (await act.Should().ThrowAsync<AddressNotAllowedException>())
                .Which.Message.Should().NotContain("10.1.2.3");
        }

        [Fact]
        public async Task The_refusal_never_leaks_the_password()
        {
            var act = async () => await Guard().EnsureAllowedAsync(
                MediaAddress.Parse("rtsp://admin:hunter2@10.0.0.5:554/stream"));

            (await act.Should().ThrowAsync<AddressNotAllowedException>())
                .Which.Message.Should().NotContain("hunter2");
        }
    }

    public class WhenPrivateNetworksAreAllowed : AddressGuardTests
    {
        [Fact]
        public async Task The_camera_on_your_desk_still_works()
        {
            // The development default. The whole point on a laptop is to point it at a local camera.
            var act = async () => await Guard(allowPrivate: true).EnsureAllowedAsync(
                MediaAddress.Parse("rtsp://192.168.1.64:554/stream"));

            await act.Should().NotThrowAsync();
            await _resolver.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
        }
    }

    public class FilePaths : AddressGuardTests
    {
        private static string Root => OperatingSystem.IsWindows() ? @"D:\samples" : "/srv/samples";

        private static string Under(string name) => Path.Combine(Root, name);

        [Fact]
        public async Task Any_path_is_allowed_when_no_roots_are_configured()
        {
            var act = async () => await Guard(allowPrivate: true).EnsureAllowedAsync(
                MediaAddress.Parse(Under("clip.mp4")));

            await act.Should().NotThrowAsync();
        }

        [Fact]
        public async Task A_path_inside_a_configured_root_is_allowed()
        {
            var act = async () => await Guard(false, Root).EnsureAllowedAsync(
                MediaAddress.Parse(Under("clip.mp4")));

            await act.Should().NotThrowAsync();
        }

        [Fact]
        public async Task A_path_outside_every_root_is_refused()
        {
            var elsewhere = OperatingSystem.IsWindows() ? @"D:\secrets\keys.mp4" : "/etc/passwd";

            var act = async () => await Guard(false, Root).EnsureAllowedAsync(
                MediaAddress.Parse(elsewhere));

            await act.Should().ThrowAsync<AddressNotAllowedException>();
        }

        [Fact]
        public async Task A_sibling_directory_sharing_a_prefix_does_not_pass()
        {
            // "/srv/samples-private" starts with "/srv/samples" as a string but is not inside it.
            var sibling = OperatingSystem.IsWindows()
                ? @"D:\samples-private\clip.mp4"
                : "/srv/samples-private/clip.mp4";

            var act = async () => await Guard(false, Root).EnsureAllowedAsync(
                MediaAddress.Parse(sibling));

            await act.Should().ThrowAsync<AddressNotAllowedException>();
        }

        [Fact]
        public async Task Climbing_out_of_a_root_does_not_pass()
        {
            var climb = Path.Combine(Root, "..", "secrets", "clip.mp4");

            var act = async () => await Guard(false, Root).EnsureAllowedAsync(
                MediaAddress.Parse(climb));

            await act.Should().ThrowAsync<AddressNotAllowedException>();
        }
    }
}
