using System.Reflection;
using System.Text.RegularExpressions;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using FluentAssertions;
using Xunit;

namespace Akshaya.Architecture.Tests;

/// <summary>
/// Two rules that exist because of two specific, expensive classes of bug in cross-border
/// trading systems: money that forgot its currency, and time that came from the wall clock.
/// </summary>
public sealed class MoneyAndTimeRules
{
    private static readonly Assembly Abstractions = typeof(IBrokerConnector).Assembly;

    [Fact]
    public void Contract_types_never_expose_a_bare_decimal_as_money()
    {
        // A `decimal Price` on a contract type is an invitation to add an SGD balance to an
        // INR one. Money carries its currency and refuses to be added to a different one, so
        // the mistake becomes a compile error instead of a wrong number on a dashboard.
        var suspiciousSuffixes = new[] { "Price", "Amount", "Value", "Balance", "Pnl", "Charge", "Fee" };

        // Genuinely dimensionless numbers. Strike is a price expressed in the contract's own
        // currency by definition and is matched against instrument reference data, and the
        // OHLC fields on a Candle are always in the series' declared currency.
        var allowed = new HashSet<string>
        {
            $"{nameof(InstrumentKey)}.Strike",
            $"{nameof(OptionChainRow)}.Strike",
            $"{nameof(InstrumentDefinition)}.LotSize",
            $"{nameof(InstrumentDefinition)}.TickSize",
            $"{nameof(InstrumentDefinition)}.Multiplier",
            $"{nameof(Quantity)}.Value",
        };

        var violations = new List<string>();

        foreach (var type in Abstractions.GetTypes().Where(t => t.IsPublic))
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var isDecimal = property.PropertyType == typeof(decimal)
                                || property.PropertyType == typeof(decimal?);

                if (!isDecimal)
                {
                    continue;
                }

                if (!suspiciousSuffixes.Any(s => property.Name.EndsWith(s, StringComparison.Ordinal)))
                {
                    continue;
                }

                var key = $"{type.Name}.{property.Name}";
                if (allowed.Contains(key))
                {
                    continue;
                }

                violations.Add(key);
            }
        }

        violations.Should().BeEmpty(
            "monetary values on contract types must be Money, which carries its currency. "
            + "If one of these is genuinely dimensionless, add it to the allow-list above with "
            + "a comment saying why.");
    }

    [Fact]
    public void Nothing_outside_the_clock_reads_the_ambient_time()
    {
        // The backtester replays history; it must be able to say what "now" is. A single
        // DateTime.UtcNow buried in a risk rule makes a backtest silently wrong — the rule
        // evaluates against today's market hours while the data is from 2019.
        var forbidden = new[]
        {
            "DateTime.Now", "DateTime.UtcNow", "DateTime.Today",
            "DateTimeOffset.Now", "DateTimeOffset.UtcNow",
        };

        var allowedFiles = new[] { "src/Akshaya.SharedKernel/Clock.cs" };

        var commentLine = new Regex(@"^\s*(//|///|\*|/\*)", RegexOptions.Compiled);
        var violations = new List<string>();

        foreach (var file in RepoRoot.SourceFiles("src"))
        {
            var relative = RepoRoot.RelativePath(file);
            if (allowedFiles.Contains(relative))
            {
                continue;
            }

            var lines = File.ReadAllLines(file.FullName);
            for (var i = 0; i < lines.Length; i++)
            {
                if (commentLine.IsMatch(lines[i]))
                {
                    continue;
                }

                foreach (var token in forbidden)
                {
                    if (lines[i].Contains(token, StringComparison.Ordinal))
                    {
                        violations.Add($"{relative}:{i + 1} uses {token}");
                    }
                }
            }
        }

        violations.Should().BeEmpty(
            "inject IClock instead. SystemClock.Instance is the production implementation; "
            + "ManualClock is what makes deterministic backtests and expiry tests possible.");
    }

    [Fact]
    public void Connector_facet_methods_return_Result_rather_than_throwing()
    {
        // A rejected order, an expired session and a closed market are outcomes, not
        // exceptions. If a facet can throw for those, every caller needs a try/catch and the
        // canonical error codes stop being the single failure vocabulary.
        var facets = new[]
        {
            typeof(IConnectorAuth), typeof(IConnectorOrders), typeof(IConnectorPortfolio),
            typeof(IConnectorMarketData), typeof(IConnectorStream),
        };

        var violations = new List<string>();

        foreach (var facet in facets)
        {
            foreach (var method in facet.GetMethods())
            {
                var returnType = method.ReturnType;
                var name = returnType.IsGenericType
                    ? returnType.GetGenericTypeDefinition().Name
                    : returnType.Name;

                var isResultShaped =
                    name.StartsWith("Result", StringComparison.Ordinal)
                    || name.StartsWith("Task", StringComparison.Ordinal)
                    || name.StartsWith("ValueTask", StringComparison.Ordinal)
                    || name.StartsWith("IAsyncEnumerable", StringComparison.Ordinal);

                if (!isResultShaped)
                {
                    violations.Add($"{facet.Name}.{method.Name} returns {returnType.Name}");
                }
            }
        }

        violations.Should().BeEmpty(
            "connector facets communicate failure through Result<T>, never exceptions");
    }
}
