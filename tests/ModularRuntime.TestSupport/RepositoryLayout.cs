namespace ModularRuntime.TestSupport;

/// <summary>
/// Where the tests find this repository and, for suites that span repositories, the sibling
/// projects of a local workspace (<c>&lt;workspace&gt;/ModularRuntime</c> next to other checkouts).
/// </summary>
public static class RepositoryLayout
{
    /// <summary>
    /// The root of this repository (the folder that holds <c>ModularRuntime.sln</c>): the
    /// <c>MODULARRUNTIME_ROOT</c> environment variable when set (test suites of other repositories
    /// that reuse these helpers), otherwise found above the test binaries.
    /// </summary>
    public static string Root { get; } = LocateRoot();

    /// <summary>
    /// Another project of the workspace: <paramref name="environmentVariable"/> when set, otherwise the
    /// sibling folder <paramref name="folderName"/> next to this repository. The folder may not exist.
    /// </summary>
    public static string Sibling(string folderName, string environmentVariable)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? Path.Combine(Root, "..", folderName) : configured);
    }

    private static string LocateRoot()
    {
        if (Environment.GetEnvironmentVariable("MODULARRUNTIME_ROOT") is { Length: > 0 } configured)
        {
            var root = Path.GetFullPath(configured);
            return File.Exists(Path.Combine(root, "ModularRuntime.sln"))
                ? root
                : throw new DirectoryNotFoundException($"MODULARRUNTIME_ROOT ('{root}') does not contain ModularRuntime.sln.");
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ModularRuntime.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root not found (no ModularRuntime.sln above the test binaries).");
    }
}
