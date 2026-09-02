using System.Collections.Concurrent;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Akshaya.Connectors.Host;

/// <summary>Where a running gateway can be reached.</summary>
/// <param name="Host">Hostname or address.</param>
/// <param name="Port">Port the daemon listens on.</param>
/// <param name="InstanceId">Runtime's own handle for this instance (container id, pid).</param>
public sealed record GatewayEndpoint(string Host, int Port, string? InstanceId = null)
{
    public override string ToString() => $"{Host}:{Port}";
}

/// <summary>Result of one health probe.</summary>
/// <param name="IsHealthy">False means the gateway is not usable right now.</param>
/// <param name="Detail">What went wrong, shown to the user.</param>
/// <param name="Latency">Round-trip time, when the probe measured one.</param>
public sealed record GatewayProbe(bool IsHealthy, string? Detail = null, TimeSpan? Latency = null);

public enum GatewayState
{
    /// <summary>Never started, or explicitly stopped.</summary>
    Stopped,

    Starting,

    /// <summary>Running and passing probes.</summary>
    Running,

    /// <summary>Running but failing probes. Distinct from Stopped: restarting may not help.</summary>
    Unhealthy,

    /// <summary>Start failed. The detail carries why.</summary>
    Failed,
}

/// <summary>Observable state of one credential's gateway, for the UI and for health endpoints.</summary>
public sealed record GatewayStatus
{
    public required string ConnectorId { get; init; }

    public required string CredentialId { get; init; }

    public required GatewayState State { get; init; }

    public GatewayEndpoint? Endpoint { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? LastProbedAt { get; init; }

    public string? Detail { get; init; }

    /// <summary>Consecutive failed probes. Drives escalation without a separate counter elsewhere.</summary>
    public int ConsecutiveFailures { get; init; }
}

/// <summary>
/// THE SEAM between the supervisor's policy and the mechanism that actually runs a daemon.
///
/// Deliberately abstract, and deliberately NOT implemented here. Real implementations shell
/// out to Docker, talk to the Kubernetes API, or hand off to systemd — all of which are
/// deployment concerns that must not be compiled into the connector host. Keeping them behind
/// this interface also means the supervisor's logic (which is the part with the interesting
/// bugs: races on concurrent start, probe caching, failure escalation) is testable with a fake.
///
/// Implementations must be idempotent: <see cref="EnsureStartedAsync"/> is called on every
/// connector creation for a gateway-hosted broker, and must return the existing instance
/// rather than starting a second one.
/// </summary>
public interface IGatewayRuntime
{
    /// <summary>Starts the gateway for this credential if it is not already running.</summary>
    Task<Result<GatewayEndpoint>> EnsureStartedAsync(
        GatewaySpec spec,
        string credentialId,
        CancellationToken ct = default);

    /// <summary>Stops it. Succeeds when it was not running.</summary>
    Task<Result> StopAsync(GatewaySpec spec, string credentialId, CancellationToken ct = default);

    /// <summary>Probes liveness. Must not throw; return an unhealthy probe instead.</summary>
    Task<Result<GatewayProbe>> ProbeAsync(
        GatewaySpec spec,
        string credentialId,
        GatewayEndpoint endpoint,
        CancellationToken ct = default);
}

/// <summary>
/// The default runtime: it runs nothing.
///
/// Chosen as the default because the alternative — silently doing nothing and reporting
/// success — would make a gateway broker appear healthy while every call failed. Instead,
/// every attempt fails immediately with a message that tells the operator exactly what is
/// missing, and with the manifest's setup URL where one exists.
/// </summary>
public sealed class NullGatewayRuntime(ILogger<NullGatewayRuntime> logger) : IGatewayRuntime
{
    public Task<Result<GatewayEndpoint>> EnsureStartedAsync(
        GatewaySpec spec,
        string credentialId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        logger.LogWarning(
            "No gateway runtime is registered, so gateway '{GatewayId}' cannot be started for credential "
            + "{CredentialId}. Register an {Interface} implementation for this deployment.",
            spec.Id,
            credentialId,
            nameof(IGatewayRuntime));

        var setup = spec.SetupInstructionsUrl is { } url
            ? $" Setup instructions: {url}"
            : string.Empty;

        return Task.FromResult(Result<GatewayEndpoint>.Failure(
            ConnectorErrors.GatewayUnavailable(
                spec.Id,
                $"no gateway runtime is configured on this host.{setup}")));
    }

    /// <summary>Stopping something that was never started is the desired end state.</summary>
    public Task<Result> StopAsync(GatewaySpec spec, string credentialId, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    public Task<Result<GatewayProbe>> ProbeAsync(
        GatewaySpec spec,
        string credentialId,
        GatewayEndpoint endpoint,
        CancellationToken ct = default) =>
        Task.FromResult(Result<GatewayProbe>.Success(
            new GatewayProbe(false, "No gateway runtime is configured on this host.")));
}

/// <summary>Tracks and health-checks the gateway processes behind gateway-hosted connectors.</summary>
public interface IGatewaySupervisor
{
    /// <summary>
    /// Guarantees a healthy gateway for this credential, starting it if needed, or returns
    /// <see cref="ConnectorErrorCodes.GatewayUnavailable"/>.
    /// </summary>
    Task<Result<GatewayEndpoint>> EnsureAvailableAsync(
        ConnectorManifest manifest,
        string credentialId,
        CancellationToken ct = default);

    /// <summary>Current known state without probing. For dashboards and health endpoints.</summary>
    GatewayStatus? GetStatus(string connectorId, string credentialId);

    IReadOnlyCollection<GatewayStatus> GetAll();

    /// <summary>Stops a gateway — on unlink, or when an operator disables a broker link.</summary>
    Task<Result> ShutdownAsync(ConnectorManifest manifest, string credentialId, CancellationToken ct = default);
}

/// <summary>
/// Supervises per-credential gateway daemons (Moomoo OpenD, IBKR Client Portal Gateway).
///
/// Why per credential and not per connector: these daemons hold ONE user's session. Sharing one
/// between users would mean one trader's orders going out on another's account, so
/// <see cref="GatewaySpec.PerCredential"/> is almost always true and the cost of that — a
/// process per linked user — is a real number the pricing model has to carry.
///
/// The three problems this class actually solves, none of which are the daemon's own start-up:
///
///  1. CONCURRENT START. Two requests arriving together for the same credential must not start
///     two daemons. A per-key semaphore serialises them and the second one finds the first's
///     instance.
///  2. PROBE COST. Every connector creation would otherwise probe. Results are cached for
///     <see cref="ConnectorHostOptions.GatewayProbeCacheDuration"/>, which is short enough that
///     a dead gateway is noticed quickly and long enough that a busy trader does not probe on
///     every keystroke.
///  3. HONEST FAILURE. When the gateway is down, callers get
///     <see cref="ConnectorErrorCodes.GatewayUnavailable"/> with an actionable message —
///     never a timeout, and never a silent success that fails at the venue.
/// </summary>
public sealed class GatewaySupervisor : IGatewaySupervisor, IAsyncDisposable
{
    private readonly IGatewayRuntime _runtime;
    private readonly ConnectorHostOptions _options;
    private readonly ILogger<GatewaySupervisor> _logger;
    private readonly IClock _clock;

    private readonly ConcurrentDictionary<string, GatewayStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public GatewaySupervisor(
        IGatewayRuntime runtime,
        IOptions<ConnectorHostOptions> options,
        ILogger<GatewaySupervisor> logger,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(clock);

        _runtime = runtime;
        _options = options.Value;
        _logger = logger;
        _clock = clock;
    }

    public async Task<Result<GatewayEndpoint>> EnsureAvailableAsync(
        ConnectorManifest manifest,
        string credentialId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);

        if (manifest.Gateway is not { } spec)
        {
            return new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"'{manifest.Id}' has no gateway specification but gateway supervision was requested.");
        }

        // A shared gateway is keyed by the connector alone; a per-credential one by both.
        // Getting this wrong in the sharing direction would cross two users' sessions.
        var key = KeyOf(manifest.Id, spec.PerCredential ? credentialId : "shared");
        var gate = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(ct);
        try
        {
            var now = _clock.UtcNow;
            var existing = _statuses.GetValueOrDefault(key);

            // Fast path: running, and probed recently enough to still believe it.
            if (existing is { State: GatewayState.Running, Endpoint: not null }
                && existing.LastProbedAt is { } lastProbe
                && now - lastProbe < _options.GatewayProbeCacheDuration)
            {
                return Result<GatewayEndpoint>.Success(existing.Endpoint);
            }

            if (existing is { State: GatewayState.Running or GatewayState.Unhealthy, Endpoint: not null })
            {
                var probe = await ProbeAsync(spec, manifest, credentialId, key, existing, existing.Endpoint, ct);
                if (probe.IsSuccess)
                {
                    return probe;
                }

                // A running-but-unhealthy gateway gets one restart attempt: these daemons
                // routinely wedge, and restarting is what an operator would do anyway. Falling
                // through to the start path below does exactly that.
                _logger.LogWarning(
                    "Gateway {GatewayId} for {CredentialId} is unhealthy; attempting a restart.",
                    spec.Id,
                    credentialId);
            }

            Update(key, manifest.Id, credentialId, s => s with
            {
                State = GatewayState.Starting,
                Detail = null,
            });

            var start = await _runtime.EnsureStartedAsync(spec, credentialId, ct);
            if (start.IsFailure)
            {
                Update(key, manifest.Id, credentialId, s => s with
                {
                    State = GatewayState.Failed,
                    Detail = start.Error.Message,
                    LastProbedAt = now,
                    ConsecutiveFailures = s.ConsecutiveFailures + 1,
                });

                // Normalise whatever the runtime said into the canonical gateway error, so
                // callers never have to know which runtime is deployed.
                return start.Error.Code == ConnectorErrorCodes.GatewayUnavailable
                    ? Result<GatewayEndpoint>.Failure(start.Error)
                    : ConnectorErrors.GatewayUnavailable(manifest.Id, start.Error.Message);
            }

            var endpoint = start.Value;
            Update(key, manifest.Id, credentialId, s => s with
            {
                State = GatewayState.Running,
                Endpoint = endpoint,
                StartedAt = s.StartedAt ?? now,
                Detail = null,
            });

            var status = _statuses[key];
            return await ProbeAsync(spec, manifest, credentialId, key, status, endpoint, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public GatewayStatus? GetStatus(string connectorId, string credentialId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);

        // Try the per-credential key first, then the shared one, because the caller does not
        // necessarily know which sharing model this connector uses.
        return _statuses.GetValueOrDefault(KeyOf(connectorId, credentialId))
               ?? _statuses.GetValueOrDefault(KeyOf(connectorId, "shared"));
    }

    public IReadOnlyCollection<GatewayStatus> GetAll() => _statuses.Values.ToArray();

    public async Task<Result> ShutdownAsync(
        ConnectorManifest manifest,
        string credentialId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);

        if (manifest.Gateway is not { } spec)
        {
            return Result.Success();
        }

        var key = KeyOf(manifest.Id, spec.PerCredential ? credentialId : "shared");
        var gate = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(ct);
        try
        {
            var stop = await _runtime.StopAsync(spec, credentialId, ct);

            Update(key, manifest.Id, credentialId, s => s with
            {
                State = GatewayState.Stopped,
                Endpoint = null,
                Detail = stop.IsFailure ? stop.Error.Message : null,
            });

            return stop;
        }
        finally
        {
            gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var gate in _gates.Values)
        {
            gate.Dispose();
        }

        _gates.Clear();

        // Deliberately does NOT stop the daemons. A host restart must not sign every linked
        // user out of their broker; the runtime owns process lifetime across deploys.
        return ValueTask.CompletedTask;
    }

    private async Task<Result<GatewayEndpoint>> ProbeAsync(
        GatewaySpec spec,
        ConnectorManifest manifest,
        string credentialId,
        string key,
        GatewayStatus status,
        GatewayEndpoint endpoint,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.GatewayProbeTimeout);

        Result<GatewayProbe> probe;
        try
        {
            probe = await _runtime.ProbeAsync(spec, credentialId, endpoint, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // A probe that hangs IS a down gateway. Treating it as anything else means the
            // caller waits for the daemon's own timeout, which for these tends to be minutes.
            probe = Result<GatewayProbe>.Success(
                new GatewayProbe(false, $"The gateway did not answer within {_options.GatewayProbeTimeout}."));
        }
        catch (Exception ex)
        {
            probe = Result<GatewayProbe>.Success(new GatewayProbe(false, ex.Message));
        }

        var now = _clock.UtcNow;
        var healthy = probe.IsSuccess && probe.Value.IsHealthy;
        var detail = probe.IsSuccess ? probe.Value.Detail : probe.Error.Message;

        Update(key, manifest.Id, credentialId, s => s with
        {
            State = healthy ? GatewayState.Running : GatewayState.Unhealthy,
            Endpoint = endpoint,
            LastProbedAt = now,
            Detail = detail,
            ConsecutiveFailures = healthy ? 0 : s.ConsecutiveFailures + 1,
        });

        if (healthy)
        {
            return Result<GatewayEndpoint>.Success(endpoint);
        }

        var failures = _statuses[key].ConsecutiveFailures;
        _logger.LogWarning(
            "Gateway {GatewayId} at {Endpoint} for {CredentialId} failed its health probe "
            + "({Failures} consecutive): {Detail}",
            spec.Id,
            endpoint,
            credentialId,
            failures,
            detail ?? "no detail");

        return ConnectorErrors.GatewayUnavailable(
            manifest.Id,
            detail ?? $"the gateway at {endpoint} is not answering health probes.");
    }

    private void Update(
        string key,
        string connectorId,
        string credentialId,
        Func<GatewayStatus, GatewayStatus> mutate) =>
        _statuses.AddOrUpdate(
            key,
            _ => mutate(new GatewayStatus
            {
                ConnectorId = connectorId,
                CredentialId = credentialId,
                State = GatewayState.Stopped,
            }),
            (_, existing) => mutate(existing));

    private static string KeyOf(string connectorId, string credentialId) => $"{connectorId}|{credentialId}";
}
