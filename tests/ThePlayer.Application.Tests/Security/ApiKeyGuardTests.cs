using Microsoft.Extensions.Options;
using ThePlayer.Application.Security;

namespace ThePlayer.Application.Tests.Security;

/// <summary>
/// Who gets in, and the two ways this is easy to get subtly wrong.
/// </summary>
public class ApiKeyGuardTests
{
    private static ApiKeyGuard Guard(bool required, params string[] keys) =>
        new(Options.Create(new ApiKeyOptions { Required = required, Keys = keys }));

    public class WhenNoKeyIsDemanded : ApiKeyGuardTests
    {
        [Fact]
        public void Everything_is_accepted()
        {
            // The development default. A server that interrogates you before it will play the file
            // on your desk is an obstacle rather than a protection.
            var guard = Guard(required: false);

            guard.Accepts(null).Should().BeTrue();
            guard.Accepts("anything").Should().BeTrue();
        }

        [Fact]
        public void The_deployment_is_usable_with_no_keys_configured()
        {
            Guard(required: false).IsUsable.Should().BeTrue();
        }
    }

    public class WhenAKeyIsDemanded : ApiKeyGuardTests
    {
        [Fact]
        public void The_configured_key_is_accepted()
        {
            Guard(required: true, "correct-horse").Accepts("correct-horse").Should().BeTrue();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("wrong")]
        [InlineData("correct-hors")]
        [InlineData("correct-horsee")]
        [InlineData("Correct-Horse")]
        public void Anything_else_is_refused(string? presented)
        {
            // Case included: a key is an opaque secret, not an identifier, and being forgiving
            // about it halves the search space for no benefit anyone asked for.
            Guard(required: true, "correct-horse").Accepts(presented).Should().BeFalse();
        }

        [Fact]
        public void Any_of_several_keys_is_accepted()
        {
            // More than one so a key can be rotated without a window where neither works.
            var guard = Guard(required: true, "old-key", "new-key");

            guard.Accepts("old-key").Should().BeTrue();
            guard.Accepts("new-key").Should().BeTrue();
            guard.Accepts("other").Should().BeFalse();
        }

        [Fact]
        public void Blank_entries_are_ignored_rather_than_matched()
        {
            // An empty string in the list is a configuration slip - an unset environment variable
            // in an array - and accepting an empty key because of it would open the server wide.
            var guard = Guard(required: true, "real-key", "", "   ");

            guard.Accepts("").Should().BeFalse();
            guard.Accepts("   ").Should().BeFalse();
            guard.Accepts("real-key").Should().BeTrue();
        }
    }

    public class Misconfiguration : ApiKeyGuardTests
    {
        [Fact]
        public void Demanding_a_key_with_none_configured_is_not_usable()
        {
            // The whole point of the check: this server would refuse every request, and the cause
            // is one missing environment variable. The host fails to start on it rather than
            // serving a wall of 401s that read like a client problem.
            Guard(required: true).IsUsable.Should().BeFalse();
        }

        [Fact]
        public void A_list_of_blanks_is_the_same_mistake()
        {
            Guard(required: true, "", "  ").IsUsable.Should().BeFalse();
        }

        [Fact]
        public void One_real_key_among_blanks_is_usable()
        {
            Guard(required: true, "", "real-key").IsUsable.Should().BeTrue();
        }
    }
}
