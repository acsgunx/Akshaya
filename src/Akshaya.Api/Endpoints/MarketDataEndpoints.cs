using Akshaya.Api.Infrastructure;
using Akshaya.Connectors.Abstractions;
using Akshaya.Modules.MarketData;
using Akshaya.Modules.Trading.Application;
using Akshaya.SharedKernel;

namespace Akshaya.Api.Endpoints;

/// <summary>
/// Market data, all of it read through a specific broker link.
///
/// Every call here takes a <c>brokerLinkId</c>, and that is not an oversight: two brokers can
/// disagree about a quote or even about what an instrument's tradable hours are, so asking
/// "through which broker" is the honest question. The real-time equivalent of these endpoints
/// is <see cref="Akshaya.Api.Hubs.MarketDataHub"/>.
///
/// INSTRUMENT SEARCH AND RESOLVE ARE SERVED FROM <see cref="InstrumentMaster"/>, not by
/// delegating to the connector on every call. Connectors are request-scoped, so a connector's
/// own instrument cache dies with the request that created it — and for a broker that
/// publishes its master as one large CSV, delegating meant re-downloading a few hundred
/// thousand rows per keystroke. The link is still required, because it is what identifies the
/// connector and supplies the session used to load the master the first time.
/// </summary>
public static class MarketDataEndpoints
{
    public static IEndpointRouteBuilder MapMarketDataEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/market-data").WithTags("Market data");

        group.MapGet("/instruments/search", async (
            string brokerLinkId,
            string query,
            int? limit,
            ICurrentUserAccessor user,
            BrokerLinkResolver linkResolver,
            InstrumentMaster master,
            CancellationToken ct) =>
        {
            var indexResult = await GetIndexAsync(user.TenantId, brokerLinkId, linkResolver, master, ct);
            return indexResult.IsFailure
                ? ProblemDetailsMapper.ToProblem(indexResult.Error)
                : Results.Ok(indexResult.Value.Search(query, Math.Clamp(limit ?? 20, 1, MaxSearchResults)));
        });

        group.MapGet("/instruments/resolve", async (
            string brokerLinkId,
            string instrument,
            ICurrentUserAccessor user,
            BrokerLinkResolver linkResolver,
            InstrumentMaster master,
            CancellationToken ct) =>
        {
            if (!InstrumentKey.TryParse(instrument, out var key))
            {
                return ProblemDetailsMapper.ValidationProblem([$"'{instrument}' is not a valid instrument key."]);
            }

            var indexResult = await GetIndexAsync(user.TenantId, brokerLinkId, linkResolver, master, ct);
            if (indexResult.IsFailure)
            {
                return ProblemDetailsMapper.ToProblem(indexResult.Error);
            }

            return indexResult.Value.TryResolve(key, out var definition)
                ? Results.Ok(definition)
                : ProblemDetailsMapper.ToProblem(ConnectorErrors.InstrumentNotFound(key));
        });

        group.MapGet("/quote", async (
            string brokerLinkId,
            string instrument,
            ICurrentUserAccessor user,
            BrokerLinkResolver linkResolver,
            CancellationToken ct) =>
        {
            if (!InstrumentKey.TryParse(instrument, out var key))
            {
                return ProblemDetailsMapper.ValidationProblem([$"'{instrument}' is not a valid instrument key."]);
            }

            var connectorResult = await linkResolver.ResolveAsync(user.TenantId, brokerLinkId, ct);
            if (connectorResult.IsFailure)
            {
                return ProblemDetailsMapper.ToProblem(connectorResult.Error);
            }

            await using var connector = connectorResult.Value;
            var result = await connector.MarketData.GetQuoteAsync(key, ct);
            return result.ToHttp();
        });

        group.MapPost("/ltp", async (
            string brokerLinkId,
            LtpBatchRequestDto request,
            ICurrentUserAccessor user,
            BrokerLinkResolver linkResolver,
            CancellationToken ct) =>
        {
            var keys = new List<InstrumentKey>(request.Instruments.Count);
            foreach (var raw in request.Instruments)
            {
                if (!InstrumentKey.TryParse(raw, out var key))
                {
                    return ProblemDetailsMapper.ValidationProblem([$"'{raw}' is not a valid instrument key."]);
                }

                keys.Add(key);
            }

            var connectorResult = await linkResolver.ResolveAsync(user.TenantId, brokerLinkId, ct);
            if (connectorResult.IsFailure)
            {
                return ProblemDetailsMapper.ToProblem(connectorResult.Error);
            }

            await using var connector = connectorResult.Value;
            var result = await connector.MarketData.GetLtpAsync(keys, ct);
            return result.ToHttp();
        });

        group.MapGet("/history", async (
            string brokerLinkId,
            string instrument,
            string timeFrame,
            DateTimeOffset from,
            DateTimeOffset to,
            ICurrentUserAccessor user,
            BrokerLinkResolver linkResolver,
            CancellationToken ct) =>
        {
            if (!InstrumentKey.TryParse(instrument, out var key))
            {
                return ProblemDetailsMapper.ValidationProblem([$"'{instrument}' is not a valid instrument key."]);
            }

            if (!Enum.TryParse<TimeFrame>(timeFrame, ignoreCase: true, out var frame))
            {
                return ProblemDetailsMapper.ValidationProblem(
                    [$"'{timeFrame}' is not a recognised time frame."]);
            }

            var connectorResult = await linkResolver.ResolveAsync(user.TenantId, brokerLinkId, ct);
            if (connectorResult.IsFailure)
            {
                return ProblemDetailsMapper.ToProblem(connectorResult.Error);
            }

            await using var connector = connectorResult.Value;
            var result = await connector.MarketData.GetHistoricalAsync(
                new HistoryRequest { Instrument = key, TimeFrame = frame, From = from, To = to },
                ct);

            return result.ToHttp();
        });

        group.MapGet("/option-chain", async (
            string brokerLinkId,
            string underlying,
            DateOnly expiry,
            ICurrentUserAccessor user,
            BrokerLinkResolver linkResolver,
            CancellationToken ct) =>
        {
            if (!InstrumentKey.TryParse(underlying, out var key))
            {
                return ProblemDetailsMapper.ValidationProblem([$"'{underlying}' is not a valid instrument key."]);
            }

            var connectorResult = await linkResolver.ResolveAsync(user.TenantId, brokerLinkId, ct);
            if (connectorResult.IsFailure)
            {
                return ProblemDetailsMapper.ToProblem(connectorResult.Error);
            }

            await using var connector = connectorResult.Value;
            var result = await connector.MarketData.GetOptionChainAsync(key, expiry, ct);
            return result.ToHttp();
        });

        return app;
    }

    /// <summary>
    /// The instrument master for whichever connector this link belongs to, loading it on first
    /// use.
    ///
    /// The fast path — a warm master, which is every request after the first — resolves the
    /// link only far enough to learn its connector id and never activates a connector at all:
    /// no session decrypt, no decorator chain, no broker round trip. A connector is built only
    /// when the master actually has to be loaded, and it is disposed as soon as it has been.
    /// </summary>
    private static async Task<Result<InstrumentSearchIndex>> GetIndexAsync(
        string tenantId,
        string brokerLinkId,
        BrokerLinkResolver linkResolver,
        InstrumentMaster master,
        CancellationToken ct)
    {
        var linkResult = await linkResolver.GetLinkAsync(tenantId, brokerLinkId, ct);
        if (linkResult.IsFailure)
        {
            return Result<InstrumentSearchIndex>.Failure(linkResult.Error);
        }

        var link = linkResult.Value;
        if (master.TryGetFresh(link.ConnectorId, out var warm))
        {
            return warm;
        }

        var connectorResult = await linkResolver.ConnectAsync(link, ct);
        if (connectorResult.IsFailure)
        {
            return Result<InstrumentSearchIndex>.Failure(connectorResult.Error);
        }

        await using var connector = connectorResult.Value;

        // The connector must outlive the load, which is why this is awaited here rather than
        // handed to the master as a background job: `master` holds no session of its own.
        return await master.GetOrLoadAsync(
            link.ConnectorId,
            token => connector.Reference.GetInstrumentsAsync(ct: token),
            ct);
    }

    /// <summary>
    /// Ceiling on one search response. A search box needs a screenful; anything asking for
    /// tens of thousands of rows wants the instrument master, not this endpoint.
    /// </summary>
    private const int MaxSearchResults = 100;
}

/// <summary>A batch of instruments to price in one round trip, keyed as canonical instrument strings.</summary>
public sealed record LtpBatchRequestDto
{
    public required IReadOnlyList<string> Instruments { get; init; }
}
