using System.Globalization;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Longbridge;

/// <summary>
/// The host's entry point into this connector, found by <see cref="IConnectorPlugin"/> and never by name. Holds no
/// per-session state: the manifest is parsed once and every <see cref="Create"/> builds a fresh connector.
///
/// Settings: <c>region</c> — <c>global</c> (the default) or <c>cn</c>, for the mainland-China endpoints, which differ
/// only in their domain — and <c>requestTimeoutSeconds</c>.
/// </summary>
public sealed class LongbridgePlugin : IConnectorPlugin
{
    private static readonly ConnectorManifest CachedManifest = LoadManifest();

    /// <inheritdoc />
    public ConnectorManifest Manifest => CachedManifest;

    /// <inheritdoc />
    public Result<IBrokerConnector> Create(ConnectorActivationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = BindOptions(context.Settings);
        if (options.IsFailure)
        {
            return Result<IBrokerConnector>.Failure(options.Error);
        }

        var connector = new LongbridgeConnector(
            context.Manifest,
            context.Session,
            options.Value,
            context.LoggerFactory.CreateLogger<LongbridgeConnector>(),
            context.Clock,
            context.HttpClientFactory);

        return Result<IBrokerConnector>.Success(connector);
    }

    internal static Result<LongbridgeOptions> BindOptions(IReadOnlyDictionary<string, string> settings)
    {
        var options = new LongbridgeOptions();

        if (settings.TryGetValue("region", out var region)
            && !string.IsNullOrWhiteSpace(region)
            && !string.Equals(region.Trim(), "global", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(region.Trim(), "cn", StringComparison.OrdinalIgnoreCase))
            {
                return Result<LongbridgeOptions>.Failure(new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    $"The Longbridge 'region' setting is '{region}'; it must be global or cn."));
            }

            options = options with
            {
                HttpBaseUrl = new Uri("https://openapi.longbridge.cn"),
                OAuthBaseUrl = new Uri("https://openapi.longbridge.cn/oauth2/"),
                QuoteSocketUrl = new Uri("wss://openapi-quote.longbridge.cn/v2"),
                TradeSocketUrl = new Uri("wss://openapi-trade.longbridge.cn/v2"),
            };
        }

        if (settings.TryGetValue("requestTimeoutSeconds", out var timeout)
            && double.TryParse(timeout, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0)
        {
            options = options with
            {
                RequestTimeout = TimeSpan.FromSeconds(seconds),
                SocketRequestTimeout = TimeSpan.FromSeconds(seconds),
            };
        }

        return options;
    }

    private static ConnectorManifest LoadManifest()
    {
        var assembly = typeof(LongbridgePlugin).Assembly;
        using var stream = assembly.GetManifestResourceStream(ManifestLoader.FileName)
            ?? throw new InvalidOperationException(
                $"The embedded {ManifestLoader.FileName} was not found in {assembly.FullName}.");

        using var reader = new StreamReader(stream);
        var result = ManifestLoader.Parse(reader.ReadToEnd(), $"embedded:{assembly.GetName().Name}");

        return result.IsFailure
            ? throw new InvalidOperationException($"The embedded manifest failed validation: {result.Error}")
            : result.Value;
    }
}
