using System.Reflection;

namespace Akshaya.Api.Endpoints;

/// <summary>
/// The running build's identity — version plus the commit it was published from — so
/// "did that deploy land" is a GET, not a guess.
///
/// Anonymous like the health probes: the check has to work before a session exists
/// (the sign-in screen renders it too), and a commit hash is not tenant data.
/// </summary>
public static class VersionEndpoints
{
    public static IEndpointRouteBuilder MapVersionEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/version", () => Results.Ok(BuildInfo.Current))
            .AllowAnonymous();

        return app;
    }
}

/// <summary>
/// What <c>GET /api/version</c> returns. Both fields come out of the API assembly's
/// <see cref="AssemblyInformationalVersionAttribute"/>: the SDK stamps it as
/// <c>"{Version}+{SourceRevisionId}"</c>, and SourceRevisionId is the git HEAD whenever
/// the build runs inside a checkout — which the CI and both code-deploy workflows do.
/// Container builds have no .git (it is dockerignored), so deploy/Dockerfile accepts a
/// <c>GIT_SHA</c> build arg and passes it to <c>dotnet publish</c> instead.
/// </summary>
public sealed record BuildInfo(string Version, string? Commit)
{
    public static readonly BuildInfo Current = From(typeof(BuildInfo).Assembly);

    private static BuildInfo From(Assembly assembly)
    {
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        int plus = informational?.IndexOf('+', StringComparison.Ordinal) ?? -1;
        string version = plus >= 0
            ? informational![..plus]
            : informational ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
        string? commit = plus >= 0 ? informational![(plus + 1)..] : null;

        return new BuildInfo(version, commit);
    }
}
