using System.Reflection;
using NetArchTest.Rules;

namespace ThePlayer.Architecture.Tests;

/// <summary>
/// The dependency rule, enforced by the build rather than by review.
/// <para>
/// Clean Architecture decays quietly: one convenient <c>using</c> in the wrong layer compiles
/// fine and is invisible in a diff, and by the time it is noticed there are twenty more. A red
/// build is a much better guard than good intentions.
/// </para>
/// </summary>
public class DependencyRuleTests
{
    private const string Domain = "ThePlayer.Domain";
    private const string Application = "ThePlayer.Application";
    private const string Infrastructure = "ThePlayer.Infrastructure";
    private const string Api = "ThePlayer.Api";

    // Referencing a type from each assembly forces it to load so NetArchTest can scan it.
    private static Assembly DomainAssembly => typeof(Domain.Media.MediaAddress).Assembly;

    private static Assembly ApplicationAssembly => typeof(global::ThePlayer.Application.IHardwareInspector).Assembly;

    private static Assembly InfrastructureAssembly =>
        typeof(Infrastructure.FFmpeg.FFmpegCommandRunner).Assembly;

    [Fact]
    public void Domain_depends_on_no_other_layer()
    {
        var result = Types.InAssembly(DomainAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(Application, Infrastructure, Api)
            .GetResult();

        result.ShouldBeSuccessful();
    }

    [Fact]
    public void Domain_depends_on_no_third_party_package()
    {
        // The strictest rule in the codebase, and the one most likely to be broken by convenience:
        // a logging abstraction here, a JSON attribute there. Domain stays on the BCL alone so it
        // can be reasoned about, tested instantly, and translated to Go without dragging a
        // dependency graph along.
        var allowedPrefixes = new[] { "System", "netstandard", "mscorlib", "ThePlayer.Domain" };

        var offenders = DomainAssembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => !allowedPrefixes.Any(prefix =>
                name.Equals(prefix, StringComparison.Ordinal) ||
                name.StartsWith($"{prefix}.", StringComparison.Ordinal)))
            .ToList();

        offenders.Should().BeEmpty(
            "ThePlayer.Domain must reference nothing but the base class library");
    }

    [Fact]
    public void Application_does_not_depend_on_Infrastructure_or_Api()
    {
        // This is the inversion that makes the whole structure work: Application declares the
        // ports, Infrastructure implements them. An arrow the other way would make the use cases
        // depend on FFmpeg and MediaMTX directly.
        var result = Types.InAssembly(ApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(Infrastructure, Api)
            .GetResult();

        result.ShouldBeSuccessful();
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_Api()
    {
        var result = Types.InAssembly(InfrastructureAssembly)
            .ShouldNot()
            .HaveDependencyOn(Api)
            .GetResult();

        result.ShouldBeSuccessful();
    }

    [Fact]
    public void Application_does_not_depend_on_web_or_hosting_types()
    {
        // Application orchestrates; it does not know it is being driven by HTTP. Keeping ASP.NET
        // out of it is what allows the same use cases to run under a different host - or a
        // different language - unchanged.
        var result = Types.InAssembly(ApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("Microsoft.AspNetCore", "Microsoft.Extensions.Hosting")
            .GetResult();

        result.ShouldBeSuccessful();
    }
}

internal static class ArchitectureAssertions
{
    /// <summary>
    /// Fails with the list of offending types, because "the architecture test failed" without
    /// naming them is a frustrating thing to be handed by CI.
    /// </summary>
    public static void ShouldBeSuccessful(this TestResult result)
    {
        var offenders = result.FailingTypeNames ?? [];

        result.IsSuccessful.Should().BeTrue(
            "the dependency rule was violated by: {0}",
            string.Join(", ", offenders));
    }
}
