using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Akshaya.Connectors.Host;

/// <summary>Where a connector came from. Decides how it is activated and whether it can be unloaded.</summary>
public enum ConnectorSource
{
    /// <summary>Compiled into the host and registered in code.</summary>
    InProcess,

    /// <summary>Discovered on disk and loaded into its own <see cref="PluginLoadContext"/>.</summary>
    Plugin,

    /// <summary>A remote process speaking the gRPC contract.</summary>
    OutOfProcess,
}

/// <summary>
/// Optional sidecar beside a plugin's manifest, naming its entry assembly and type.
///
/// Optional because the conventions below cover the normal case; present when a plugin folder
/// contains several assemblies or several <see cref="IConnectorPlugin"/> implementations and
/// the choice must be explicit rather than guessed.
/// </summary>
public sealed record ConnectorPluginDescriptor
{
    /// <summary>File name of the entry assembly, relative to the plugin folder.</summary>
    public string? Assembly { get; init; }

    /// <summary>Assembly-qualified-free full type name implementing <see cref="IConnectorPlugin"/>.</summary>
    public string? EntryType { get; init; }
}

/// <summary>One discovered, validated, ready-to-activate connector.</summary>
public sealed record ConnectorCatalogEntry
{
    public required ConnectorManifest Manifest { get; init; }

    public required ConnectorSource Source { get; init; }

    /// <summary>The plugin instance for in-process and plugin sources; null for out-of-process.</summary>
    public IConnectorPlugin? Plugin { get; init; }

    /// <summary>Factory for connectors registered directly in code without a plugin type.</summary>
    public Func<ConnectorActivationContext, Result<IBrokerConnector>>? Factory { get; init; }

    /// <summary>gRPC address for <see cref="ConnectorSource.OutOfProcess"/>.</summary>
    public Uri? Address { get; init; }

    public string? PluginDirectory { get; init; }

    public string? AssemblyPath { get; init; }

    /// <summary>Held so the plugin can be unloaded. Null for non-plugin sources.</summary>
    public PluginLoadContext? LoadContext { get; init; }
}

/// <summary>A plugin that could not be loaded, kept so health can report it instead of hiding it.</summary>
/// <param name="Location">Directory or registration that failed.</param>
/// <param name="ConnectorId">Id if it got far enough to be known.</param>
/// <param name="Error">Why.</param>
public sealed record ConnectorLoadFailure(string Location, string? ConnectorId, Error Error);

/// <summary>
/// Discovers every connector available to this host and validates what it finds.
///
/// Three sources, one uniform result:
///
///  * IN-PROCESS registrations from <see cref="ConnectorHostOptions.InProcess"/>.
///  * PLUGIN folders under <see cref="ConnectorHostOptions.PluginDirectory"/>, each holding a
///    <c>connector.manifest.json</c> and an assembly, loaded into its own
///    <see cref="PluginLoadContext"/>.
///  * OUT-OF-PROCESS registrations addressed over gRPC.
///
/// Two rules the catalog exists to enforce:
///
///  1. NO CONNECTOR IS AVAILABLE UNTIL ITS MANIFEST VALIDATES. A connector whose manifest is
///     wrong is more dangerous than one that is absent — the UI renders order types the broker
///     will reject and the rate limiter enforces limits that do not exist.
///  2. ONE BAD PLUGIN DOES NOT STOP THE HOST. Failures are collected and reported, not thrown,
///     unless <see cref="ConnectorHostOptions.FailFastOnPluginError"/> says otherwise. A trader
///     linked to five brokers must not lose all five because a sixth plugin was published
///     badly.
/// </summary>
public sealed class ConnectorCatalog
{
    private readonly ConnectorHostOptions _options;
    private readonly ILogger<ConnectorCatalog> _logger;
    private readonly ConcurrentDictionary<string, ConnectorCatalogEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<ConnectorLoadFailure> _failures = [];
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private bool _loaded;

    public ConnectorCatalog(IOptions<ConnectorHostOptions> options, ILogger<ConnectorCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _logger = logger;
    }

    public IReadOnlyCollection<string> ConnectorIds => _entries.Keys.ToArray();

    public IReadOnlyCollection<ConnectorManifest> Manifests =>
        _entries.Values.Select(e => e.Manifest).ToArray();

    /// <summary>Plugins that failed to load. Surfaced in health so a silent absence is impossible.</summary>
    public IReadOnlyCollection<ConnectorLoadFailure> Failures
    {
        get
        {
            lock (_failures)
            {
                return _failures.ToArray();
            }
        }
    }

    /// <summary>
    /// Discovers everything. Idempotent and safe to call concurrently; the second caller waits
    /// for the first rather than scanning again, because scanning creates load contexts and
    /// doing that twice would load every plugin assembly twice.
    /// </summary>
    public async Task<Result> LoadAsync(CancellationToken ct = default)
    {
        await _loadGate.WaitAsync(ct);
        try
        {
            if (_loaded)
            {
                return Result.Success();
            }

            LoadInProcess();
            LoadOutOfProcess();
            await LoadPluginsAsync(ct);

            _loaded = true;

            _logger.LogInformation(
                "Connector catalog loaded: {Count} connector(s) available ({Ids}); {FailureCount} failure(s).",
                _entries.Count,
                string.Join(", ", _entries.Keys.Order(StringComparer.Ordinal)),
                _failures.Count);

            if (_options.FailFastOnPluginError && _failures.Count > 0)
            {
                var first = _failures[0];
                return new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    $"Connector plugin at '{first.Location}' failed to load and "
                    + $"{nameof(ConnectorHostOptions.FailFastOnPluginError)} is set: {first.Error.Message}");
            }

            return Result.Success();
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public Result<ConnectorCatalogEntry> Get(string connectorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        return _entries.TryGetValue(connectorId, out var entry)
            ? Result<ConnectorCatalogEntry>.Success(entry)
            : new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"No connector with id '{connectorId}' is available on this host. "
                + $"Available: {(_entries.IsEmpty ? "(none)" : string.Join(", ", _entries.Keys.Order(StringComparer.Ordinal)))}.");
    }

    public Result<ConnectorManifest> GetManifest(string connectorId) =>
        Get(connectorId).Map(entry => entry.Manifest);

    /// <summary>
    /// Removes a plugin and asks its load context to unload.
    ///
    /// Unloading is COOPERATIVE: it completes only once every reference into the context is
    /// gone, which is why connector instances are request-scoped rather than cached. This
    /// method therefore returns success as soon as the unload is requested, not once it has
    /// finished — waiting would mean blocking on a garbage collection.
    /// </summary>
    public Result Unload(string connectorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        if (!_entries.TryRemove(connectorId, out var entry))
        {
            return new Error(ConnectorErrorCodes.InvalidRequest, $"'{connectorId}' is not loaded.");
        }

        if (entry.LoadContext is null)
        {
            return Result.Success();
        }

        _logger.LogInformation("Unloading connector plugin {ConnectorId}.", connectorId);
        entry.LoadContext.Unload();
        return Result.Success();
    }

    private void LoadInProcess()
    {
        foreach (var registration in _options.InProcess)
        {
            var validation = ManifestLoader.Validate(
                registration.Manifest,
                _options.ContractVersion,
                $"in-process registration '{registration.Manifest.Id}'");

            if (validation.IsFailure)
            {
                RecordFailure($"in-process:{registration.Manifest.Id}", registration.Manifest.Id, validation.Error);
                continue;
            }

            Add(new ConnectorCatalogEntry
            {
                Manifest = registration.Manifest,
                Source = ConnectorSource.InProcess,
                Factory = registration.Factory,
            });
        }
    }

    private void LoadOutOfProcess()
    {
        foreach (var registration in _options.OutOfProcess)
        {
            var validation = ManifestLoader.Validate(
                registration.Manifest,
                _options.ContractVersion,
                $"out-of-process registration '{registration.Manifest.Id}'");

            if (validation.IsFailure)
            {
                RecordFailure(registration.Address.ToString(), registration.Manifest.Id, validation.Error);
                continue;
            }

            if (registration.Manifest.Hosting != ConnectorHosting.OutOfProcess)
            {
                // A mismatch here means the manifest and the deployment disagree about what
                // this connector IS, and the disagreement would surface much later as a
                // confusing activation failure.
                RecordFailure(
                    registration.Address.ToString(),
                    registration.Manifest.Id,
                    new Error(
                        ConnectorErrorCodes.InvalidRequest,
                        $"'{registration.Manifest.Id}' is registered as out-of-process but its manifest "
                        + $"declares hosting '{registration.Manifest.Hosting}'."));
                continue;
            }

            Add(new ConnectorCatalogEntry
            {
                Manifest = registration.Manifest,
                Source = ConnectorSource.OutOfProcess,
                Address = registration.Address,
            });
        }
    }

    private async Task LoadPluginsAsync(CancellationToken ct)
    {
        var root = _options.PluginDirectory;
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        if (!Directory.Exists(root))
        {
            // Not a failure: a deployment with no plugin folder is a normal deployment.
            _logger.LogInformation("Plugin directory '{Root}' does not exist; no plugins scanned.", root);
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            ct.ThrowIfCancellationRequested();
            await LoadPluginAsync(directory, ct);
        }
    }

    private async Task LoadPluginAsync(string directory, CancellationToken ct)
    {
        var manifestPath = Path.Combine(directory, ManifestLoader.FileName);
        if (!File.Exists(manifestPath))
        {
            // A folder without a manifest is not a plugin — it might be a data or log folder.
            // Silently ignoring it is right; warning would train people to ignore warnings.
            return;
        }

        var manifestResult = await ManifestLoader.LoadFromFileAsync(manifestPath, _options.ContractVersion, ct);
        if (manifestResult.IsFailure)
        {
            RecordFailure(directory, null, manifestResult.Error);
            return;
        }

        var manifest = manifestResult.Value;

        if (manifest.Hosting == ConnectorHosting.OutOfProcess)
        {
            // Its manifest may live on disk, but the connector itself is somewhere else and
            // needs an address the folder cannot supply.
            RecordFailure(
                directory,
                manifest.Id,
                new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    $"'{manifest.Id}' declares out-of-process hosting; register it with "
                    + $"{nameof(ConnectorHostOptions.AddOutOfProcess)} and an address instead of dropping it "
                    + "in the plugin directory."));
            return;
        }

        var descriptor = ReadDescriptor(directory);
        var assemblyPathResult = ResolveAssemblyPath(directory, manifest, descriptor);
        if (assemblyPathResult.IsFailure)
        {
            RecordFailure(directory, manifest.Id, assemblyPathResult.Error);
            return;
        }

        var assemblyPath = assemblyPathResult.Value;
        PluginLoadContext? context = null;

        try
        {
            context = new PluginLoadContext(assemblyPath, manifest.Id);
            var assembly = context.LoadFromAssemblyPath(assemblyPath);

            var pluginTypeResult = FindPluginType(assembly, descriptor?.EntryType, manifest.Id);
            if (pluginTypeResult.IsFailure)
            {
                context.Unload();
                RecordFailure(directory, manifest.Id, pluginTypeResult.Error);
                return;
            }

            if (Activator.CreateInstance(pluginTypeResult.Value) is not IConnectorPlugin plugin)
            {
                context.Unload();
                RecordFailure(
                    directory,
                    manifest.Id,
                    new Error(
                        ConnectorErrorCodes.InvalidRequest,
                        $"'{pluginTypeResult.Value.FullName}' could not be activated as an "
                        + $"{nameof(IConnectorPlugin)}. It must have a public parameterless constructor."));
                return;
            }

            // The file manifest wins over the one compiled into the plugin: the file is what CI
            // validated against connector.manifest.schema.json, and it is what an operator can
            // inspect. A disagreement is worth knowing about, though — it usually means a
            // published plugin is stale.
            if (!string.Equals(plugin.Manifest.Id, manifest.Id, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Plugin at '{Directory}' declares id '{PluginId}' in code but '{FileId}' in {File}. "
                    + "Using the file.",
                    directory,
                    plugin.Manifest.Id,
                    manifest.Id,
                    ManifestLoader.FileName);
            }

            Add(new ConnectorCatalogEntry
            {
                Manifest = manifest,
                Source = ConnectorSource.Plugin,
                Plugin = plugin,
                PluginDirectory = directory,
                AssemblyPath = assemblyPath,
                LoadContext = context,
            });

            _logger.LogInformation(
                "Loaded connector plugin {ConnectorId} v{Version} from '{Assembly}'.",
                manifest.Id,
                manifest.ConnectorVersion,
                assemblyPath);
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException
                                       or TypeLoadException or ReflectionTypeLoadException or TargetInvocationException
                                       or MissingMethodException)
        {
            // Every one of these means "this assembly is not what we thought it was". None of
            // them should take the host down with it.
            context?.Unload();
            RecordFailure(
                directory,
                manifest.Id,
                new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    $"Could not load connector plugin '{manifest.Id}' from '{assemblyPath}': {ex.Message}"));
        }
    }

    private ConnectorPluginDescriptor? ReadDescriptor(string directory)
    {
        var path = Path.Combine(directory, "connector.plugin.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ConnectorPluginDescriptor>(
                File.ReadAllText(path),
                ConnectorJson.Default);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Ignoring unreadable connector.plugin.json in '{Directory}'.", directory);
            return null;
        }
    }

    /// <summary>
    /// Resolves the entry assembly, in order: the sidecar's name, then <c>{connectorId}.dll</c>,
    /// then the single <c>Akshaya.Connector.*.dll</c> in the folder. The last convention is
    /// only used when it is unambiguous — guessing between two candidate assemblies is exactly
    /// the kind of cleverness that loads the wrong broker.
    /// </summary>
    private static Result<string> ResolveAssemblyPath(
        string directory,
        ConnectorManifest manifest,
        ConnectorPluginDescriptor? descriptor)
    {
        if (!string.IsNullOrWhiteSpace(descriptor?.Assembly))
        {
            var declared = Path.Combine(directory, descriptor.Assembly);
            return File.Exists(declared)
                ? Result<string>.Success(declared)
                : new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    $"connector.plugin.json names assembly '{descriptor.Assembly}' but it is not in '{directory}'.");
        }

        var byId = Path.Combine(directory, manifest.Id + ".dll");
        if (File.Exists(byId))
        {
            return byId;
        }

        var candidates = Directory
            .EnumerateFiles(directory, "Akshaya.Connector.*.dll", SearchOption.TopDirectoryOnly)
            .ToArray();

        return candidates.Length switch
        {
            1 => candidates[0],
            0 => new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"No entry assembly found in '{directory}'. Expected '{manifest.Id}.dll', a single "
                + "'Akshaya.Connector.*.dll', or a connector.plugin.json naming one."),
            _ => new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"'{directory}' contains {candidates.Length} candidate assemblies "
                + $"({string.Join(", ", candidates.Select(Path.GetFileName))}). Add a connector.plugin.json "
                + "naming the entry assembly."),
        };
    }

    private static Result<Type> FindPluginType(Assembly assembly, string? entryTypeName, string connectorId)
    {
        if (!string.IsNullOrWhiteSpace(entryTypeName))
        {
            var declared = assembly.GetType(entryTypeName, throwOnError: false, ignoreCase: false);
            if (declared is null)
            {
                return new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    $"connector.plugin.json names entry type '{entryTypeName}' but it is not in "
                    + $"'{assembly.GetName().Name}'.");
            }

            return typeof(IConnectorPlugin).IsAssignableFrom(declared)
                ? Result<Type>.Success(declared)
                : new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    $"'{entryTypeName}' does not implement {nameof(IConnectorPlugin)}.");
        }

        var candidates = assembly
            .GetExportedTypes()
            .Where(t => typeof(IConnectorPlugin).IsAssignableFrom(t)
                        && t is { IsAbstract: false, IsInterface: false })
            .ToArray();

        return candidates.Length switch
        {
            1 => candidates[0],
            0 => new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"'{assembly.GetName().Name}' contains no public {nameof(IConnectorPlugin)} implementation "
                + $"for connector '{connectorId}'."),

            // Several is a legitimate design (a real connector plus a sandbox one); it just has
            // to be stated rather than guessed.
            _ => new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"'{assembly.GetName().Name}' contains {candidates.Length} {nameof(IConnectorPlugin)} "
                + $"implementations ({string.Join(", ", candidates.Select(t => t.FullName))}). "
                + "Name the entry type in connector.plugin.json."),
        };
    }

    private void Add(ConnectorCatalogEntry entry)
    {
        if (_entries.TryAdd(entry.Manifest.Id, entry))
        {
            return;
        }

        // Two sources claiming one id is ambiguous in a way that cannot be resolved safely:
        // silently preferring one would mean orders going to a broker the operator did not
        // choose.
        RecordFailure(
            entry.PluginDirectory ?? entry.Source.ToString(),
            entry.Manifest.Id,
            new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"Connector id '{entry.Manifest.Id}' is already registered from another source; "
                + "the duplicate was ignored."));
    }

    private void RecordFailure(string location, string? connectorId, Error error)
    {
        lock (_failures)
        {
            _failures.Add(new ConnectorLoadFailure(location, connectorId, error));
        }

        _logger.LogError(
            "Connector at '{Location}' ({ConnectorId}) failed to load: {Error}",
            location,
            connectorId ?? "unknown",
            error.Message);
    }
}
