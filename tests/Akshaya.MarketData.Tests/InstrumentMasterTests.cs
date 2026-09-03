using System.Runtime.CompilerServices;
using Akshaya.Modules.MarketData;
using Akshaya.SharedKernel;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Akshaya.MarketData.Tests;

/// <summary>
/// Caching and load behaviour.
///
/// The bug this component was built to kill: instrument search delegated to a request-scoped
/// connector, so every keystroke of a search box re-downloaded and re-parsed the broker's
/// entire instrument master. <see cref="Loads_once_across_a_burst_of_concurrent_callers"/> is
/// the test that would have caught it, and is the reason the type holds a gate at all.
/// </summary>
public sealed class InstrumentMasterTests
{
    private static readonly Venue Nse = new("XNSE");

    [Fact]
    public async Task Loads_once_and_serves_the_snapshot_to_later_callers()
    {
        var source = new CountingSource(Equity("INFY"));
        var master = Master(new FakeClock(DateTimeOffset.UnixEpoch));

        for (var i = 0; i < 5; i++)
        {
            (await master.GetOrLoadAsync("broker", source.Load)).IsSuccess.Should().BeTrue();
        }

        source.Loads.Should().Be(1, "the master is downloaded once, not once per search");
    }

    [Fact]
    public async Task Loads_once_across_a_burst_of_concurrent_callers()
    {
        // A debounced search box still fires several overlapping requests, and a watchlist
        // page load fans out further. Without single-flighting, each one starts its own
        // multi-hundred-thousand-row download.
        var released = new TaskCompletionSource();
        var source = new CountingSource(released.Task, Equity("INFY"));
        var master = Master(new FakeClock(DateTimeOffset.UnixEpoch));

        var callers = Enumerable.Range(0, 10)
            .Select(_ => master.GetOrLoadAsync("broker", source.Load))
            .ToArray();

        released.SetResult();
        var results = await Task.WhenAll(callers);

        source.Loads.Should().Be(1);
        results.Should().OnlyContain(r => r.IsSuccess);
    }

    [Fact]
    public async Task Reloads_once_the_snapshot_has_aged_past_the_refresh_interval()
    {
        var source = new CountingSource(Equity("INFY"));
        var clock = new FakeClock(new DateTimeOffset(2026, 9, 3, 3, 30, 0, TimeSpan.Zero));
        var master = Master(clock, new InstrumentMasterOptions { RefreshInterval = TimeSpan.FromHours(12) });

        await master.GetOrLoadAsync("broker", source.Load);
        clock.Advance(TimeSpan.FromHours(13));
        await master.GetOrLoadAsync("broker", source.Load);

        source.Loads.Should().Be(2);
    }

    [Fact]
    public async Task Keeps_a_separate_snapshot_per_connector()
    {
        var master = Master(new FakeClock(DateTimeOffset.UnixEpoch));

        await master.GetOrLoadAsync("broker-a", new CountingSource(Equity("INFY")).Load);
        await master.GetOrLoadAsync("broker-b", new CountingSource(Equity("TCS"), Equity("WIPRO")).Load);

        master.TryGetFresh("broker-a", out var a).Should().BeTrue();
        master.TryGetFresh("broker-b", out var b).Should().BeTrue();
        a.Count.Should().Be(1);
        b.Count.Should().Be(2);
    }

    [Fact]
    public async Task A_failed_load_is_not_cached_so_the_next_caller_retries()
    {
        var master = Master(new FakeClock(DateTimeOffset.UnixEpoch));
        var source = new ThrowingSource();

        (await master.GetOrLoadAsync("broker", source.Load)).IsFailure.Should().BeTrue();
        (await master.GetOrLoadAsync("broker", source.Load)).IsFailure.Should().BeTrue();

        source.Attempts.Should().Be(2, "a failure must not be cached as if it were an answer");
        master.TryGetFresh("broker", out _).Should().BeFalse();
    }

    [Fact]
    public async Task An_empty_master_is_a_failure_rather_than_a_broker_that_lists_nothing()
    {
        // Caching "this broker has no instruments" would leave search silently dead until the
        // next refresh — a far worse outcome than one failed request.
        var master = Master(new FakeClock(DateTimeOffset.UnixEpoch));

        var result = await master.GetOrLoadAsync("broker", new CountingSource().Load);

        result.IsFailure.Should().BeTrue();
        master.TryGetFresh("broker", out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_stale_snapshot_is_served_when_a_refresh_fails()
    {
        // Yesterday's instrument list is enormously more useful than an error page, and the
        // rows a trader is searching for were almost certainly already in it.
        var clock = new FakeClock(new DateTimeOffset(2026, 9, 3, 3, 30, 0, TimeSpan.Zero));
        var master = Master(clock, new InstrumentMasterOptions { RefreshInterval = TimeSpan.FromHours(12) });

        await master.GetOrLoadAsync("broker", new CountingSource(Equity("INFY")).Load);
        clock.Advance(TimeSpan.FromHours(13));

        var result = await master.GetOrLoadAsync("broker", new ThrowingSource().Load);

        result.IsSuccess.Should().BeTrue();
        result.Value.Search("INFY", 5).Should().ContainSingle();
    }

    [Fact]
    public async Task A_failed_refresh_is_surfaced_when_serving_stale_is_switched_off()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 9, 3, 3, 30, 0, TimeSpan.Zero));
        var master = Master(clock, new InstrumentMasterOptions
        {
            RefreshInterval = TimeSpan.FromHours(12),
            ServeStaleOnRefreshFailure = false,
        });

        await master.GetOrLoadAsync("broker", new CountingSource(Equity("INFY")).Load);
        clock.Advance(TimeSpan.FromHours(13));

        (await master.GetOrLoadAsync("broker", new ThrowingSource().Load)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task TryGetFresh_reports_no_snapshot_before_the_first_load()
    {
        // This is what lets the endpoint skip activating a connector on the warm path, so it
        // must never claim a snapshot it does not have.
        var master = Master(new FakeClock(DateTimeOffset.UnixEpoch));

        master.TryGetFresh("broker", out _).Should().BeFalse();
        await master.GetOrLoadAsync("broker", new CountingSource(Equity("INFY")).Load);
        master.TryGetFresh("broker", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Invalidate_forces_the_next_caller_to_reload()
    {
        var source = new CountingSource(Equity("INFY"));
        var master = Master(new FakeClock(DateTimeOffset.UnixEpoch));

        await master.GetOrLoadAsync("broker", source.Load);
        master.Invalidate("broker");
        await master.GetOrLoadAsync("broker", source.Load);

        source.Loads.Should().Be(2);
    }

    private static InstrumentMaster Master(IClock clock, InstrumentMasterOptions? options = null) =>
        new(Options.Create(options ?? new InstrumentMasterOptions()), clock, NullLogger<InstrumentMaster>.Instance);

    private static InstrumentDefinition Equity(string symbol) => new()
    {
        Key = new InstrumentKey(Nse, symbol, AssetClass.Equity),
        Name = symbol,
        Currency = Currency.Inr,
    };

    /// <summary>An instrument source that counts how many times it was actually enumerated.</summary>
    private sealed class CountingSource(Task? releasedWhen, params InstrumentDefinition[] instruments)
    {
        private readonly Task _released = releasedWhen ?? Task.CompletedTask;
        private int _loads;

        public CountingSource(params InstrumentDefinition[] instruments)
            : this(releasedWhen: null, instruments)
        {
        }

        public int Loads => Volatile.Read(ref _loads);

        public async IAsyncEnumerable<InstrumentDefinition> Load(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            // Counted on ENUMERATION, not on construction: an async iterator body does not run
            // until the first MoveNext, and "how many downloads happened" is the question.
            Interlocked.Increment(ref _loads);
            await _released.WaitAsync(ct);

            foreach (var instrument in instruments)
            {
                yield return instrument;
            }
        }
    }

    /// <summary>Stands in for a broker whose instrument download fails.</summary>
    private sealed class ThrowingSource
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public async IAsyncEnumerable<InstrumentDefinition> Load(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Interlocked.Increment(ref _attempts);
            await Task.Yield();

            // Connectors signal a master download failure by throwing: `GetInstrumentsAsync`
            // is a bare IAsyncEnumerable and has no Result to fail with.
            throw new InvalidOperationException("the broker is down");

#pragma warning disable CS0162 // Unreachable, but required to make this method an iterator.
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
