using Akshaya.Modules.Portfolio;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Options;

namespace Akshaya.Api.Endpoints;

/// <summary>
/// The portfolio dashboard's endpoints: positions, holdings and balances individually — so a
/// widget that only needs one does not pay for a broker round trip on the other two — plus the
/// full blended, multi-currency snapshot for the main view.
/// </summary>
public static class PortfolioEndpoints
{
    public static IEndpointRouteBuilder MapPortfolioEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/portfolio").WithTags("Portfolio");

        group.MapGet("/", async (
            string? currency,
            ICurrentUserAccessor user,
            BlendedPortfolioService portfolio,
            IOptions<PortfolioOptions> options,
            CancellationToken ct) =>
        {
            var snapshot = await portfolio.GetSnapshotAsync(
                user.TenantId, user.UserId, ResolveCurrency(currency, options), PortfolioParts.All, ct);

            return Results.Ok(snapshot);
        });

        group.MapGet("/positions", async (
            string? currency,
            ICurrentUserAccessor user,
            BlendedPortfolioService portfolio,
            IOptions<PortfolioOptions> options,
            CancellationToken ct) =>
        {
            var snapshot = await portfolio.GetSnapshotAsync(
                user.TenantId, user.UserId, ResolveCurrency(currency, options), PortfolioParts.Positions, ct);

            return Results.Ok(new
            {
                snapshot.AsOf,
                snapshot.Positions,
                snapshot.Sources,
                snapshot.IsPartial,
            });
        });

        group.MapGet("/holdings", async (
            string? currency,
            ICurrentUserAccessor user,
            BlendedPortfolioService portfolio,
            IOptions<PortfolioOptions> options,
            CancellationToken ct) =>
        {
            var snapshot = await portfolio.GetSnapshotAsync(
                user.TenantId, user.UserId, ResolveCurrency(currency, options), PortfolioParts.Holdings, ct);

            return Results.Ok(new
            {
                snapshot.AsOf,
                snapshot.Holdings,
                snapshot.Sources,
                snapshot.IsPartial,
            });
        });

        group.MapGet("/balances", async (
            string? currency,
            ICurrentUserAccessor user,
            BlendedPortfolioService portfolio,
            IOptions<PortfolioOptions> options,
            CancellationToken ct) =>
        {
            var snapshot = await portfolio.GetSnapshotAsync(
                user.TenantId, user.UserId, ResolveCurrency(currency, options), PortfolioParts.Balances, ct);

            return Results.Ok(new
            {
                snapshot.AsOf,
                snapshot.Balances,
                snapshot.Sources,
                snapshot.IsPartial,
            });
        });

        return app;
    }

    /// <summary>The caller's requested display currency, falling back to the tenant's configured default.</summary>
    private static Currency ResolveCurrency(string? requested, IOptions<PortfolioOptions> options) =>
        requested is { Length: > 0 }
            ? new Currency(requested)
            : new Currency(options.Value.DefaultDisplayCurrency);
}
