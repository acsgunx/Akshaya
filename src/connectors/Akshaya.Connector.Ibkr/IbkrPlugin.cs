using System.Globalization;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Ibkr;

/// <summary>
/// The host's entry point into this connector, found by <see cref="IConnectorPlugin"/> and never by name. Holds no
/// per-session state: the manifest is parsed once and every <see cref="Create"/> builds a fresh connector around the
/// gateway address the host resolved.
///
/// Settings (<c>Connectors:Settings:ibkr</c>): <c>requestTimeoutSeconds</c>, <c>allowUntrustedCertificate</c>,
/// <c>autoConfirmMessageIds</c> (comma-separated order reply message ids) and <c>maxChainStrikes</c>.
/// </summary>
public sealed class IbkrPlugin : IConnectorPlugin
{
    private static readonly ConnectorManifest CachedManifest = LoadManifest();

    /// <inheritdoc />
    public ConnectorManifest Manifest => CachedManifest;

    /// <inheritdoc />
    public Result<IBrokerConnector> Create(ConnectorActivationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var connector = new IbkrConnector(
            context.Manifest,
            context.Session,
            context.Gateway,
            BindOptions(context.Settings),
            context.LoggerFactory.CreateLogger<IbkrConnector>(),
            context.Clock);

        return Result<IBrokerConnector>.Success(connector);
    }

    internal static IbkrOptions BindOptions(IReadOnlyDictionary<string, string> settings)
    {
        var options = new IbkrOptions();

        if (settings.TryGetValue("requestTimeoutSeconds", out var timeout)
            && double.TryParse(timeout, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0)
        {
            options = options with { RequestTimeout = TimeSpan.FromSeconds(seconds) };
        }

        if (settings.TryGetValue("allowUntrustedCertificate", out var untrusted) && bool.TryParse(untrusted, out var allow))
        {
            options = options with { AllowUntrustedCertificate = allow };
        }

        if (settings.TryGetValue("autoConfirmMessageIds", out var ids) && !string.IsNullOrWhiteSpace(ids))
        {
            options = options with
            {
                AutoConfirmMessageIds = [.. ids.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)],
            };
        }

        if (settings.TryGetValue("maxChainStrikes", out var strikes)
            && int.TryParse(strikes, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            && count > 0)
        {
            options = options with { MaxChainStrikes = count };
        }

        return options;
    }

    private static ConnectorManifest LoadManifest()
    {
        var assembly = typeof(IbkrPlugin).Assembly;
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
