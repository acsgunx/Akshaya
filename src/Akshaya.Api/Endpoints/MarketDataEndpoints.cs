using Akshaya.Api.Infrastructure;
using Akshaya.Connectors.Abstractions;
using Akshaya.Modules.Trading.Application;
using Akshaya.SharedKernel;

namespace Akshaya.Api.Endpoints;

/// <summary>
/// Market data, all of it read through a specific broker link.
///
/// THERE IS NO STANDALONE INSTRUMENT MASTER YET, so every call here takes a
/// <c>brokerLinkId</c> and answers through that broker's own reference and market-data facets.
/// This is a real, temporary constraint rather than an oversight: two brokers can disagree about
/// a quote or even about what an instrument's tradable hours are, and until a canonical
/// instrument master exists, asking "through which broker" is the honest question. The
/// real-time equivalent of these endpoints is <see cref="Akshaya.Api.Hubs.MarketDataHub"/>.
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
            CancellationToken ct) =>
        {
            var connectorResult = await linkResolver.ResolveAsync(user.TenantId, brokerLinkId, ct);
            if (connectorResult.IsFailure)
            {
                return ProblemDetailsMapper.ToProblem(connectorResult.Error);
            }

            await using var connector = connectorResult.Value;
            var result = await connector.Reference.SearchAsync(query, limit ?? 20, ct);
            return result.ToHttp();
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
}

/// <summary>A batch of instruments to price in one round trip, keyed as canonical instrument strings.</summary>
public sealed record LtpBatchRequestDto
{
    public required IReadOnlyList<string> Instruments { get; init; }
}
