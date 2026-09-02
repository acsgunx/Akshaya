using System.Reflection;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace Akshaya.Architecture.Tests;

/// <summary>
/// Layering rules. The connector contract is a public API consumed by people who do not work
/// here — third parties writing connectors, and out-of-process connectors in other languages.
/// Every dependency it carries is one those authors inherit, and one more chance of a version
/// conflict inside a plugin load context.
/// </summary>
public sealed class DependencyRules
{
    private static readonly Assembly SharedKernel = typeof(Result<>).Assembly;
    private static readonly Assembly Abstractions = typeof(IBrokerConnector).Assembly;

    [Fact]
    public void SharedKernel_depends_on_nothing_but_the_framework()
    {
        // If the vocabulary types need a package, every connector author needs it too.
        var referenced = SharedKernel.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => !name.StartsWith("System", StringComparison.Ordinal)
                           && !name.StartsWith("netstandard", StringComparison.Ordinal)
                           && name != "mscorlib")
            .ToList();

        referenced.Should().BeEmpty(
            "Akshaya.SharedKernel is the shared vocabulary and must stay dependency-free");
    }

    [Fact]
    public void SharedKernel_csproj_declares_no_package_references()
    {
        // Belt and braces: the assembly check above can be defeated by a package that happens
        // to contain only source generators or analyzers. Read the project file too.
        var csproj = Path.Combine(
            RepoRoot.Value.FullName, "src/Akshaya.SharedKernel/Akshaya.SharedKernel.csproj");

        File.ReadAllText(csproj).Should().NotContain(
            "PackageReference",
            "the SharedKernel must have zero third-party dependencies");
    }

    [Fact]
    public void Abstractions_depends_only_on_the_SharedKernel()
    {
        var referenced = Abstractions.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => !name.StartsWith("System", StringComparison.Ordinal)
                           && !name.StartsWith("netstandard", StringComparison.Ordinal)
                           && name != "mscorlib")
            .ToList();

        referenced.Should().BeEquivalentTo(
            ["Akshaya.SharedKernel"],
            "the connector contract is implemented by third parties and by processes in other "
            + "languages; every dependency here is inherited by all of them");
    }

    [Fact]
    public void Abstractions_contains_no_http_or_serialisation_types()
    {
        // A transport type in the contract means every connector must use that transport.
        // Several target brokers speak protobuf over TCP or gRPC, not HTTP.
        var result = Types.InAssembly(Abstractions)
            .Should()
            .NotHaveDependencyOnAny("System.Net.Http", "System.Net.WebSockets")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "the contract must not assume a transport: "
            + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Trading_and_Portfolio_modules_do_not_reference_each_other()
    {
        // Modules communicate through integration events, not by reaching into each other's
        // domain. Direct references turn a modular monolith into a regular one.
        var tradingCsproj = Path.Combine(
            RepoRoot.Value.FullName, "src/Modules/Trading/Akshaya.Modules.Trading.csproj");
        var portfolioCsproj = Path.Combine(
            RepoRoot.Value.FullName, "src/Modules/Portfolio/Akshaya.Modules.Portfolio.csproj");

        File.ReadAllText(tradingCsproj).Should().NotContain("Akshaya.Modules.Portfolio");
        File.ReadAllText(portfolioCsproj).Should().NotContain("Akshaya.Modules.Trading");
    }

    [Fact]
    public void No_project_reaches_a_connector_implementation_except_the_host_and_the_api()
    {
        // The API references connector projects only to register them for local development.
        // Anything else doing so has bound itself to a specific broker at compile time.
        var allowed = new[]
        {
            "src/Akshaya.Api/Akshaya.Api.csproj",
            "src/Akshaya.Connectors.Host/Akshaya.Connectors.Host.csproj",
        };

        var violations = new List<string>();

        foreach (var csproj in RepoRoot.Value.EnumerateFiles("*.csproj", SearchOption.AllDirectories))
        {
            var relative = RepoRoot.RelativePath(csproj);
            if (relative.Contains("/bin/", StringComparison.Ordinal)
                || relative.StartsWith("src/connectors/", StringComparison.Ordinal)
                || relative.StartsWith("tests/", StringComparison.Ordinal)
                || allowed.Contains(relative))
            {
                continue;
            }

            if (File.ReadAllText(csproj.FullName).Contains("Akshaya.Connector.", StringComparison.Ordinal))
            {
                violations.Add(relative);
            }
        }

        violations.Should().BeEmpty(
            "only the host and the API composition root may reference a concrete connector");
    }
}
