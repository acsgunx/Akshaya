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
    /// Directory names that never contain repository source, wherever they appear in the tree.
    /// </summary>
    private static readonly string[] ExcludedNames =
        ["bin", "obj", "node_modules", "dist", ".git", ".claude"];

    /// <summary>
    /// Whether the walk should stop at this directory rather than descend into it.
    ///
    /// Build output and package caches are the obvious part. The load-bearing part is the
    /// nested-checkout check.
    ///
    /// A git worktree is a second, complete copy of the repository inside the first one —
    /// Claude Code puts them under <c>.claude/worktrees/&lt;name&gt;/</c>. They are listed in
    /// <c>.git/info/exclude</c>, so git ignores them, but a filesystem walk does not: a rule that
    /// enumerates from the repository root would read every project and every source file in
    /// every leftover worktree and report them as violations of the real repository. CI checks
    /// out a clean tree and never has one, so the result is a rule that fails on a developer's
    /// machine and cannot be reproduced on the build that gates the merge — the least useful
    /// failure an architecture test can produce.
    ///
    /// Keying off the <c>.git</c> entry rather than the <c>.claude/worktrees</c> path is what
    /// makes this general: it catches a worktree, submodule or nested clone wherever it is put,
    /// not only where one tool happens to put them today. (In a worktree <c>.git</c> is a file
    /// pointing at the real git dir, in a clone a directory, so this tests for either.)
    /// </summary>
    private static bool IsExcluded(DirectoryInfo dir) =>
        ExcludedNames.Contains(dir.Name, StringComparer.OrdinalIgnoreCase)
        || Path.Exists(Path.Combine(dir.FullName, ".git"));

    /// <summary>
    /// Depth-first walk that prunes excluded directories as it goes, rather than enumerating
    /// everything and filtering paths afterwards. Pruning is what keeps the rule honest — a
    /// path filter only hides the results, and still pays to walk a worktree's node_modules.
    /// </summary>
    private static IEnumerable<FileInfo> Walk(DirectoryInfo dir, string pattern)
    {
        foreach (var file in dir.EnumerateFiles(pattern))
        {
            yield return file;
        }

        foreach (var child in dir.EnumerateDirectories())
        {
            if (IsExcluded(child))
            {
                continue;
            }

            foreach (var file in Walk(child, pattern))
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// Every project file in the repository, excluding build output and nested checkouts.
    /// </summary>
    public static IEnumerable<FileInfo> ProjectFiles() => Walk(Value, "*.csproj");

    /// <summary>
    /// Every file matching <paramref name="pattern"/> under the given repo-relative directories,
    /// excluding build output and nested checkouts. Directories that do not exist are skipped.
    /// </summary>
    public static IEnumerable<FileInfo> FilesUnder(string pattern, params string[] relativeDirectories)
    {
        foreach (var relative in relativeDirectories)
        {
            var dir = new DirectoryInfo(Path.Combine(Value.FullName, relative));
            if (!dir.Exists)
            {
                continue;
            }

            foreach (var file in Walk(dir, pattern))
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// Every C# source file under the given repo-relative directories, excluding build output.
    /// </summary>
    public static IEnumerable<FileInfo> SourceFiles(params string[] relativeDirectories) =>
        FilesUnder("*.cs", relativeDirectories);

    public static string RelativePath(FileInfo file) =>
        Path.GetRelativePath(Value.FullName, file.FullName).Replace('\\', '/');
}
