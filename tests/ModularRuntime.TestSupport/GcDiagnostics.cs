namespace ModularRuntime.TestSupport;

/// <summary>Diagnostic helpers for cooperative unload tests. Production code never forces GC.</summary>
public static class GcDiagnostics
{
    public static bool WaitForCollection(WeakReference reference, int attempts = 10)
    {
        ArgumentNullException.ThrowIfNull(reference);

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            if (!reference.IsAlive)
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return !reference.IsAlive;
    }
}
