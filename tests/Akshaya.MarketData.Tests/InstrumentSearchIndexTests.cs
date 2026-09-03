using Akshaya.Modules.MarketData;
using Akshaya.SharedKernel;
using FluentAssertions;
using Xunit;

namespace Akshaya.MarketData.Tests;

/// <summary>
/// Search ranking.
///
/// The scenario every one of these is drawn from is real: an Indian equity master lists one
/// INFY share, a handful of INFY futures and several thousand INFY options, all of which
/// legitimately match the string "INFY". Which twenty of them come back is the entire user
/// experience of the search box.
/// </summary>
public sealed class InstrumentSearchIndexTests
{
    private static readonly Venue Nse = new("XNSE");
    private static readonly DateTimeOffset AsOf = new(2026, 9, 3, 3, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Exact_symbol_match_outranks_a_longer_symbol_that_merely_starts_with_it()
    {
        var index = Index(
            Equity("INFYBEES", "Nippon India ETF Infrastructure"),
            Equity("INFY", "Infosys Limited"));

        var results = index.Search("INFY", limit: 2);

        results[0].Key.Symbol.Should().Be("INFY", "an exact symbol match is the thing the trader typed");
    }

    [Fact]
    public void The_cash_scrip_outranks_its_own_derivatives()
    {
        // The failure this prevents: twenty rows of option strikes and no share.
        var index = Index(
            Option("INFY", new DateOnly(2026, 9, 24), 1500m, OptionRight.Call),
            Option("INFY", new DateOnly(2026, 9, 24), 1600m, OptionRight.Call),
            Future("INFY", new DateOnly(2026, 9, 24)),
            Equity("INFY", "Infosys Limited"));

        var results = index.Search("INFY", limit: 4);

        results[0].Key.AssetClass.Should().Be(AssetClass.Equity);
    }

    [Fact]
    public void Among_contracts_the_nearest_expiry_comes_first()
    {
        var index = Index(
            Future("NIFTY", new DateOnly(2026, 12, 31)),
            Future("NIFTY", new DateOnly(2026, 9, 24)),
            Future("NIFTY", new DateOnly(2026, 10, 29)));

        var results = index.Search("NIFTY", limit: 3);

        results.Select(r => r.Key.Expiry).Should().ContainInOrder(
            new DateOnly(2026, 9, 24),
            new DateOnly(2026, 10, 29),
            new DateOnly(2026, 12, 31));
    }

    [Fact]
    public void An_expired_contract_ranks_below_everything_still_tradable()
    {
        var index = Index(
            Future("NIFTY", new DateOnly(2026, 8, 27)),  // expired relative to AsOf
            Future("NIFTY", new DateOnly(2026, 9, 24)));

        var results = index.Search("NIFTY", limit: 2);

        results[0].Key.Expiry.Should().Be(new DateOnly(2026, 9, 24));
        results[1].Key.Expiry.Should().Be(new DateOnly(2026, 8, 27));
    }

    [Fact]
    public void A_name_match_is_found_when_the_symbol_does_not_match_at_all()
    {
        var index = Index(Equity("RELIANCE", "Reliance Industries Limited"));

        index.Search("industries", limit: 5).Should().ContainSingle()
            .Which.Key.Symbol.Should().Be("RELIANCE");
    }

    [Fact]
    public void A_word_start_in_the_name_outranks_a_match_buried_inside_a_word()
    {
        // Symbols deliberately match neither, so this isolates the name tiers: "IND" starts
        // the word "Industries", but only sits inside "Mahindra".
        var index = Index(
            Equity("AAAA", "Rama Mahindra Holdings"),
            Equity("BBBB", "Reliance Industries"));

        var results = index.Search("ind", limit: 2);

        results[0].Name.Should().Be("Reliance Industries");
        results[1].Name.Should().Be("Rama Mahindra Holdings");
    }

    [Fact]
    public void Search_is_case_insensitive_and_ignores_surrounding_whitespace()
    {
        var index = Index(Equity("INFY", "Infosys Limited"));

        index.Search("  iNfY  ", limit: 5).Should().ContainSingle();
    }

    [Fact]
    public void An_empty_or_whitespace_query_returns_nothing_rather_than_everything()
    {
        var index = Index(Equity("INFY", "Infosys Limited"));

        index.Search("", limit: 5).Should().BeEmpty();
        index.Search("   ", limit: 5).Should().BeEmpty();
    }

    [Fact]
    public void The_limit_is_honoured_even_when_far_more_rows_match()
    {
        var contracts = Enumerable.Range(0, 500)
            .Select(i => Option("NIFTY", new DateOnly(2026, 9, 24), 20000m + i, OptionRight.Call))
            .ToArray();

        var index = Index(contracts);

        index.Search("NIFTY", limit: 20).Should().HaveCount(20);
    }

    [Fact]
    public void The_best_matches_survive_the_limit_no_matter_what_order_the_master_lists_them_in()
    {
        // The bounded heap is the part that could plausibly get this wrong: the one row that
        // matters is listed LAST, after five hundred rows have already filled the heap.
        var rows = Enumerable.Range(0, 500)
            .Select(i => Option("NIFTY", new DateOnly(2026, 9, 24), 20000m + i, OptionRight.Call))
            .Append(Equity("NIFTY", "Nifty 50 Index Fund"))
            .ToArray();

        var results = Index(rows).Search("NIFTY", limit: 5);

        results[0].Key.AssetClass.Should().Be(AssetClass.Equity);
    }

    [Fact]
    public void Resolve_returns_the_definition_for_a_canonical_key()
    {
        var infy = Equity("INFY", "Infosys Limited");
        var index = Index(infy, Equity("TCS", "Tata Consultancy Services"));

        index.TryResolve(infy.Key, out var found).Should().BeTrue();
        found!.Name.Should().Be("Infosys Limited");

        index.TryResolve(new InstrumentKey(Nse, "NOPE", AssetClass.Equity), out _).Should().BeFalse();
    }

    private static InstrumentSearchIndex Index(params InstrumentDefinition[] instruments) =>
        InstrumentSearchIndex.Build(instruments, AsOf);

    private static InstrumentDefinition Equity(string symbol, string name) => new()
    {
        Key = new InstrumentKey(Nse, symbol, AssetClass.Equity),
        Name = name,
        Currency = Currency.Inr,
    };

    private static InstrumentDefinition Future(string symbol, DateOnly expiry) => new()
    {
        Key = new InstrumentKey(Nse, symbol, AssetClass.Future, expiry),
        Name = $"{symbol} {expiry:MMM} Future",
        Currency = Currency.Inr,
    };

    private static InstrumentDefinition Option(string symbol, DateOnly expiry, decimal strike, OptionRight right) => new()
    {
        Key = new InstrumentKey(Nse, symbol, AssetClass.Option, expiry, strike, right),
        Name = $"{symbol} {expiry:MMM} {strike} {right}",
        Currency = Currency.Inr,
    };
}
