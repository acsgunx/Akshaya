using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.MStock;

/// <summary>
/// The host's entry point into this connector.
///
/// The host finds this by looking for <see cref="IConnectorPlugin"/> — never by naming a
/// concrete type — so the composition root stays broker-agnostic. Everything vendor-specific
/// is behind here.
///
/// One instance serves every session, so this type holds no per-session state: the manifest is
/// parsed once and each <see cref="Create"/> call builds a fresh <see cref="MStockConnector"/>.
/// </summary>
public sealed class MStockPlugin : IConnectorPlugin
{
    private static readonly ConnectorManifest CachedManifest = LoadManifest();

    /// <inheritdoc />
    public ConnectorManifest Manifest => CachedManifest;

    /// <inheritdoc />
    public Result<IBrokerConnector> Create(ConnectorActivationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Endpoints and timeouts come from the host's per-connector settings; anything not
        // supplied falls back to the documented mStock production values baked into the record.
        var options = BindOptions(context.Settings);
        var logger = context.LoggerFactory.CreateLogger<MStockConnector>();

        var connector = context.Session is null
            ? MStockConnector.CreateUnauthenticated(context.Manifest, options, logger, context.Clock)
            : new MStockConnector(context.Manifest, context.Session, options, logger, context.Clock);

        return Result<IBrokerConnector>.Success(connector);
    }

    private static MStockOptions BindOptions(IReadOnlyDictionary<string, string> settings)
    {
        var options = new MStockOptions();

        if (settings.TryGetValue("baseUrl", out var baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBase))
        {
            options = options with { BaseUrl = parsedBase };
        }

        if (settings.TryGetValue("streamUrl", out var streamUrl) && Uri.TryCreate(streamUrl, UriKind.Absolute, out var parsedStream))
        {
            options = options with { StreamUrl = parsedStream };
        }

        if (settings.TryGetValue("apiVersion", out var apiVersion) && !string.IsNullOrWhiteSpace(apiVersion))
        {
            options = options with { ApiVersion = apiVersion };
        }

        return options;
    }

    private static ConnectorManifest LoadManifest()
    {
        var assembly = typeof(MStockPlugin).Assembly;
        using var stream = assembly.GetManifestResourceStream(ManifestLoader.FileName)
            ?? throw new InvalidOperationException(
                $"The embedded {ManifestLoader.FileName} was not found in {assembly.FullName}.");

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();

        var result = ManifestLoader.Parse(json, $"embedded:{assembly.GetName().Name}");
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"The embedded manifest failed validation: {result.Error}");
        }

        return result.Value;
    }
}
