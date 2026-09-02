using System.Reflection;
using System.Runtime.Loader;

namespace Akshaya.Connectors.Host;

/// <summary>
/// One isolated, collectible <see cref="AssemblyLoadContext"/> per connector plugin.
///
/// ══ WHY ISOLATION ══
///
/// Connectors are written by different people — including third parties — against different
/// versions of the same libraries. One broker's SDK wants Newtonsoft.Json 11, another wants
/// 13, a third pins an old gRPC. Loaded into the default context these collide, and .NET
/// resolves the collision by loading whichever assembly arrived first: the second connector
/// then fails at runtime, in production, with a MissingMethodException that names a type
/// nobody in the team has heard of. A separate load context per plugin makes each connector's
/// dependency graph its own problem.
///
/// ══ WHY COLLECTIBLE ══
///
/// A connector must be un-loadable without restarting the API: to ship a fix, to disable a
/// misbehaving broker link, to reload a plugin whose credentials rotated. Collectible contexts
/// make that possible — as long as nothing outside holds a reference into the context. That
/// caveat is real and is the reason <see cref="ConnectorFactory"/> hands out connector
/// instances scoped to a request rather than cached singletons.
///
/// ══ WHY THE SHARED LIST ══
///
/// Isolation must NOT extend to the contract. If a plugin loaded its own copy of
/// Akshaya.Connectors.Abstractions, its <c>IBrokerConnector</c> would be a DIFFERENT TYPE from
/// the host's — same name, same shape, incompatible identity — and every cast would fail with
/// the famously unhelpful "cannot convert IBrokerConnector to IBrokerConnector". The contract
/// assemblies, and the logging abstractions they surface, are therefore deliberately resolved
/// from the DEFAULT context by returning null from <see cref="Load"/>.
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    /// <summary>
    /// Assemblies that must come from the host, never from the plugin folder.
    ///
    /// Keep this list minimal. Everything on it becomes a version the plugin CANNOT choose,
    /// which is exactly the coupling isolation exists to avoid — it is here only for the
    /// assemblies whose TYPE IDENTITY must be shared across the boundary.
    /// </summary>
    private static readonly HashSet<string> SharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Akshaya.SharedKernel",
        "Akshaya.Connectors.Abstractions",
        "Akshaya.Connectors.Sdk",

        // ILogger crosses the boundary in ConnectorActivationContext, so its identity must be
        // shared too. Abstractions only — the plugin never sees a logging implementation.
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.Primitives",
    };

    private readonly AssemblyDependencyResolver _resolver;

    /// <param name="mainAssemblyPath">
    /// Full path to the plugin's entry assembly. Its <c>.deps.json</c> sitting beside it is
    /// what <see cref="AssemblyDependencyResolver"/> reads to resolve the plugin's private
    /// dependencies — a plugin published without one will fail to find them.
    /// </param>
    /// <param name="name">Context name, shown in diagnostics and dumps. Use the connector id.</param>
    public PluginLoadContext(string mainAssemblyPath, string name)
        : base(name: name, isCollectible: true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mainAssemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        MainAssemblyPath = mainAssemblyPath;
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    public string MainAssemblyPath { get; }

    /// <summary>True when this assembly's type identity must be shared with the host.</summary>
    public static bool IsSharedContract(string? assemblySimpleName) =>
        assemblySimpleName is not null && SharedAssemblies.Contains(assemblySimpleName);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Returning null delegates to the default context. That is the sharing mechanism —
        // there is no other way to guarantee a single type identity across the boundary.
        if (IsSharedContract(assemblyName.Name))
        {
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);

        // Also null for anything the plugin does not carry privately: BCL assemblies, and
        // anything it expects the host to provide. Loading a private copy of a BCL assembly
        // would be worse than useless.
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        // Native dependencies are real for connectors: some vendor SDKs ship a native
        // cryptography or protocol library. Resolve it from the plugin folder so two plugins
        // can carry different builds of the same native library.
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
