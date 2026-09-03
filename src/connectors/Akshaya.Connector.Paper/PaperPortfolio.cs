using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Paper;

/// <summary>
/// Positions, holdings and balances, straight out of the matching engine's books.
///
/// The distinction between a POSITION and a HOLDING is preserved rather than collapsed,
/// because the platform above depends on it: a holding is settled stock that can be pledged
/// and sold on a later day, a position is today's exposure that squares off. A paper connector
/// that reported everything as a position would let a delivery strategy backtest as if it were
/// intraday.
/// </summary>
/// <param name="engine">The simulated venue holding the books.</param>
/// <param name="requireSession">
/// <c>ConnectorBase.RequireSession</c>, so an unbound or expired session fails the same way
/// here as on any other connector.
/// </param>
public sealed class PaperPortfolio(
    MatchingEngine engine,
    Func<Result<BrokerSession>> requireSession) : IConnectorPortfolio
{
    /// <inheritdoc />
    public Task<Result<IReadOnlyList<BrokerPosition>>> GetPositionsAsync(CancellationToken ct = default)
    {
        var session = requireSession();
        return Task.FromResult(session.IsFailure
            ? Result<IReadOnlyList<BrokerPosition>>.Failure(session.Error)
            : Result<IReadOnlyList<BrokerPosition>>.Success(engine.Positions()));
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<BrokerHolding>>> GetHoldingsAsync(CancellationToken ct = default)
    {
        var session = requireSession();
        return Task.FromResult(session.IsFailure
            ? Result<IReadOnlyList<BrokerHolding>>.Failure(session.Error)
            : Result<IReadOnlyList<BrokerHolding>>.Success(engine.Holdings()));
    }

    /// <inheritdoc />
    /// <remarks>
    /// One balance per currency the account has been funded in or has traded, never collapsed
    /// into a single number. A paper account modelled on a Moomoo or IBKR account holds SGD,
    /// USD and HKD simultaneously, and converting them here would need an FX rate this layer
    /// deliberately does not have.
    /// </remarks>
    public Task<Result<IReadOnlyList<BrokerBalance>>> GetBalancesAsync(CancellationToken ct = default)
    {
        var session = requireSession();
        return Task.FromResult(session.IsFailure
            ? Result<IReadOnlyList<BrokerBalance>>.Failure(session.Error)
            : Result<IReadOnlyList<BrokerBalance>>.Success(engine.Balances()));
    }

    /// <summary>
    /// Not simulated. The matching engine models fills and positions, not the margin product
    /// underneath them, so there is nothing here for a conversion to change.
    ///
    /// Worth naming as a REHEARSAL GAP rather than a mere omission: the Paper connector exists
    /// so a strategy can be exercised against the same contract before it touches real money,
    /// and mStock does support conversion. A strategy that converts intraday positions to
    /// delivery therefore cannot be fully rehearsed here, and the manifest says so
    /// (positionConversion: false) so the UI hides the action rather than failing on it.
    /// </summary>
    public Task<Result> ConvertPositionAsync(
        ConvertPositionRequest request,
        CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync("position conversion");
}
