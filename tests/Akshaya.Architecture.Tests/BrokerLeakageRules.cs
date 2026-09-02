using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Akshaya.Architecture.Tests;

/// <summary>
/// The single most important rule in this repository.
///
/// The platform's entire value proposition is that a new broker costs one connector project
/// and one manifest. That property does not decay in one dramatic commit — it decays one
/// innocent special case at a time. Someone adds "if this broker, skip the margin call" to a
/// handler because it is Friday and the release is Monday. Six months later there are forty of
/// them, adding a broker means touching thirty files, and nobody can say when it happened.
///
/// This test is the tripwire. It fails the build the first time.
/// </summary>
public sealed class BrokerLeakageRules
{
    /// <summary>
    /// Names that must not appear outside the connector projects. Deliberately includes
    /// alternate spellings and product names, because "Kite" leaks as easily as "Zerodha".
    /// </summary>
    private static readonly string[] BrokerNames =
    [
        "mstock", "mirae",
        "zerodha", "kite",
        "moomoo", "futu", "opend",
        "ibkr", "tws",
        "fyers", "upstox", "dhan", "angelone", "smartapi",
        "saxo", "tigerbrokers",
    ];

    /// <summary>
    /// Matches a broker name as a whole word. Without the boundaries "futu" matches "future"
    /// and "futures", which appear constantly in a system that trades derivatives, and the rule
    /// produces so much noise that people start ignoring it — which is worse than not having it.
    /// </summary>
    private static readonly Regex NamePattern = new(
        @"\b(" + string.Join('|', BrokerNames.Select(Regex.Escape)) + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Comment lines are exempt.
    ///
    /// A doc comment explaining "this shape exists because one broker reports business failures
    /// as HTTP 200" is the documentation that keeps the abstraction comprehensible, and banning
    /// it would make the codebase worse, not better. What must never exist is a broker name in a
    /// CONDITIONAL or a STRING LITERAL — and those live on executable lines.
    /// </summary>
    private static readonly Regex CommentLine = new(
        @"^\s*(//|///|\*|/\*|<!--)", RegexOptions.Compiled);

    private static bool IsViolation(string line, out string name)
    {
        name = string.Empty;

        if (CommentLine.IsMatch(line))
        {
            return false;
        }

        var match = NamePattern.Match(line);
        if (!match.Success)
        {
            return false;
        }

        name = match.Value;
        return true;
    }

    /// <summary>
    /// Directories where broker names are not merely allowed but expected. Everything else in
    /// the repository must be able to compile with every connector project deleted.
    /// </summary>
    private static readonly string[] CoreDirectories =
    [
        "src/Akshaya.SharedKernel",
        "src/Akshaya.Connectors.Abstractions",
        "src/Akshaya.Connectors.Sdk",
        "src/Modules",
        "src/Akshaya.Api",
    ];

    [Fact]
    public void Core_and_api_must_not_mention_any_broker_by_name()
    {
        var violations = new List<string>();

        foreach (var file in RepoRoot.SourceFiles(CoreDirectories))
        {
            var relative = RepoRoot.RelativePath(file);

            var lines = File.ReadAllLines(file.FullName);
            for (var i = 0; i < lines.Length; i++)
            {
                if (IsViolation(lines[i], out var name))
                {
                    violations.Add($"{relative}:{i + 1} mentions '{name}'");
                }
            }
        }

        violations.Should().BeEmpty(
            "the core and the API must be broker-agnostic. If you need behaviour that differs "
            + "per broker, add a field to ConnectorManifest and read it — that is what the "
            + "manifest is for. A conditional on a broker's name is the thing this whole "
            + "architecture exists to prevent.");
    }

    [Fact]
    public void Frontend_must_not_mention_any_broker_by_name()
    {
        // The order ticket and the link wizard are ONE component each, rendering from the
        // manifest. A broker name in the Angular app means someone forked a component, and the
        // next broker will cost a UI change instead of a config change.
        var webDir = new DirectoryInfo(Path.Combine(RepoRoot.Value.FullName, "apps/web/src"));
        if (!webDir.Exists)
        {
            return;
        }

        var violations = new List<string>();

        var sourceExtensions = new[] { ".ts", ".html", ".scss" };
        foreach (var file in webDir.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (!sourceExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var lines = File.ReadAllLines(file.FullName);
            for (var i = 0; i < lines.Length; i++)
            {
                if (IsViolation(lines[i], out var name))
                {
                    violations.Add($"{RepoRoot.RelativePath(file)}:{i + 1} mentions '{name}'");
                }
            }
        }

        violations.Should().BeEmpty(
            "the frontend renders from connector manifests; it must never know a broker's name.");
    }

    [Fact]
    public void Every_connector_project_ships_a_manifest()
    {
        // A connector without a manifest is invisible to the host, and the failure mode is
        // silent: the app starts, the broker simply is not in the list, and nobody knows why.
        var connectors = new DirectoryInfo(Path.Combine(RepoRoot.Value.FullName, "src/connectors"));
        connectors.Exists.Should().BeTrue("src/connectors is where connector projects live");

        foreach (var project in connectors.EnumerateDirectories())
        {
            project.EnumerateFiles("connector.manifest.json").Should().NotBeEmpty(
                $"{project.Name} must ship a connector.manifest.json or the host cannot load it");
        }
    }
}
