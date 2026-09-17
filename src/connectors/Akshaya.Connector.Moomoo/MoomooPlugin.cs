using System.Globalization;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Moomoo;

/// <summary>
/// The host's entry point into this connector, found by <see cref="IConnectorPlugin"/> and never by name.
/// Holds no per-session state: the manifest is parsed once and every <see cref="Create"/> builds a fresh
/// connector around the gateway address the host resolved.
/// </summary>
public sealed class MoomooPlugin : IConnectorPlugin
{
    private static readonly ConnectorManifest CachedManifest = LoadManifest();

    /// <inheritdoc />
    public ConnectorManifest Manifest => CachedManifest;

    /// <inheritdoc />
    public Result<IBrokerConnector> Create(ConnectorActivationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var connector = new MoomooConnector(
            context.Manifest,
            context.Session,
            context.Gateway,
            BindOptions(context.Settings),
            context.LoggerFactory.CreateLogger<MoomooConnector>(),
            context.Clock);

        return Result<IBrokerConnector>.Success(connector);
    }

    private static MoomooOptions BindOptions(IReadOnlyDictionary<string, string> settings)
    {
        var options = new MoomooOptions();

        if (settings.TryGetValue("requestTimeoutSeconds", out var timeout)
            && double.TryParse(timeout, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0)
        {
            options = options with { RequestTimeout = TimeSpan.FromSeconds(seconds) };
        }

        // Larger moomoo accounts are given a larger subscription quota; this lets an operator say so.
        if (settings.TryGetValue("subscriptionQuota", out var quota)
            && int.TryParse(quota, NumberStyles.Integer, CultureInfo.InvariantCulture, out var units)
            && units > 0)
        {
            options = options with { SubscriptionQuota = units };
        }

        return options;
    }

    private static ConnectorManifest LoadManifest()
    {
        var assembly = typeof(MoomooPlugin).Assembly;
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
