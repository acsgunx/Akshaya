using System.Globalization;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Tiger;

/// <summary>
/// The host's entry point into this connector, found by <see cref="IConnectorPlugin"/> and never by name. Holds no
/// per-session state: the manifest is parsed once and every <see cref="Create"/> builds a fresh connector.
///
/// Settings (<c>Connectors:Settings:tiger</c>): <c>serverUrl</c> and <c>quoteServerUrl</c> override the endpoints the
/// licence would choose, <c>requestTimeoutSeconds</c>, <c>deviceId</c>, <c>language</c>, and
/// <c>verifyResponseSignature</c>, which should stay on.
/// </summary>
public sealed class TigerPlugin : IConnectorPlugin
{
    private static readonly ConnectorManifest CachedManifest = LoadManifest();

    /// <inheritdoc />
    public ConnectorManifest Manifest => CachedManifest;

    /// <inheritdoc />
    public Result<IBrokerConnector> Create(ConnectorActivationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var connector = new TigerConnector(
            context.Manifest,
            context.Session,
            BindOptions(context.Settings),
            context.LoggerFactory.CreateLogger<TigerConnector>(),
            context.Clock);

        return Result<IBrokerConnector>.Success(connector);
    }

    internal static TigerOptions BindOptions(IReadOnlyDictionary<string, string> settings)
    {
        var options = new TigerOptions();

        if (settings.TryGetValue("serverUrl", out var serverUrl) && Uri.TryCreate(serverUrl, UriKind.Absolute, out var parsedServer))
        {
            options = options with { ServerUrl = parsedServer };
        }

        if (settings.TryGetValue("quoteServerUrl", out var quoteUrl) && Uri.TryCreate(quoteUrl, UriKind.Absolute, out var parsedQuote))
        {
            options = options with { QuoteServerUrl = parsedQuote };
        }

        if (settings.TryGetValue("requestTimeoutSeconds", out var timeout)
            && double.TryParse(timeout, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0)
        {
            options = options with { RequestTimeout = TimeSpan.FromSeconds(seconds) };
        }

        if (settings.TryGetValue("deviceId", out var deviceId) && !string.IsNullOrWhiteSpace(deviceId))
        {
            options = options with { DeviceId = deviceId.Trim() };
        }

        if (settings.TryGetValue("language", out var language) && !string.IsNullOrWhiteSpace(language))
        {
            options = options with { Language = language.Trim() };
        }

        if (settings.TryGetValue("verifyResponseSignature", out var verify) && bool.TryParse(verify, out var shouldVerify))
        {
            options = options with { VerifyResponseSignature = shouldVerify };
        }

        return options;
    }

    private static ConnectorManifest LoadManifest()
    {
        var assembly = typeof(TigerPlugin).Assembly;
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
