namespace ModularRuntime.TestSupport;

/// <summary>Creates an isolated directory for a test and removes it best-effort.</summary>
public sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory(string prefix = "plugin")
    {
        DirectoryPath = Path.Combine(
            Path.GetTempPath(),
            "ModularRuntime.Tests",
            $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(DirectoryPath);
    }

    public string DirectoryPath { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // A collectible load context may still be in the process of unloading.
            // Temporary test data is safe to leave for the operating system to clean up.
        }
    }
}
