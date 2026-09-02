namespace Akshaya.Architecture.Tests;

/// <summary>
/// Several of these rules are source-text rules rather than IL rules — "no broker name in the
/// core" cannot be expressed against compiled metadata, because the offending string usually
/// lives in a comment, a log message or a string literal, and those are exactly the places a
/// special case starts before it becomes code.
///
/// So the tests need the repository on disk. This walks up from the test binary until it finds
/// the solution file.
/// </summary>
internal static class RepoRoot
{
    private static readonly Lazy<DirectoryInfo> Cached = new(Locate);

    public static DirectoryInfo Value => Cached.Value;

    private static DirectoryInfo Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (dir.EnumerateFiles("Akshaya.sln").Any())
            {
                return dir;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root (no Akshaya.sln found walking up from "
            + $"{AppContext.BaseDirectory}). These architecture rules read source files, so they "
            + "cannot run from a detached binary.");
    }

    /// <summary>
    /// Every C# source file under the given repo-relative directories, excluding build output.
    /// </summary>
    public static IEnumerable<FileInfo> SourceFiles(params string[] relativeDirectories)
    {
        foreach (var relative in relativeDirectories)
        {
            var dir = new DirectoryInfo(Path.Combine(Value.FullName, relative));
            if (!dir.Exists)
            {
                continue;
            }

            foreach (var file in dir.EnumerateFiles("*.cs", SearchOption.AllDirectories))
            {
                var path = file.FullName.Replace('\\', '/');
                if (path.Contains("/bin/", StringComparison.Ordinal)
                    || path.Contains("/obj/", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    public static string RelativePath(FileInfo file) =>
        Path.GetRelativePath(Value.FullName, file.FullName).Replace('\\', '/');
}
