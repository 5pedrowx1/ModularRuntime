using ModularRuntime.Desktop.Mvvm;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Desktop.ViewModels;

/// <summary>Display texts for trust states. Only OfficialVerified is ever presented as verified.</summary>
public static class TrustText
{
    public static string Label(TrustStateDto? trust, bool testKey) => trust switch
    {
        TrustStateDto.OfficialVerified => testKey ? "Official (test key)" : "Official verified",
        TrustStateDto.LocalUnverified => "Local, unverified",
        TrustStateDto.VerificationFailed => "Verification failed",
        TrustStateDto.Modified => "Modified",
        TrustStateDto.Incompatible => "Incompatible",
        TrustStateDto.Unmanaged => "Unmanaged",
        TrustStateDto.Revoked => "Revoked",
        _ => "Unknown",
    };

    public static string Explanation(TrustStateDto? trust, bool testKey) => trust switch
    {
        TrustStateDto.OfficialVerified => (testKey ? "Signed with a TEST key trusted only on this development installation. " : "Signed by the ModularRuntime team. ") +
            "The signature and the SHA-256 of every file were verified. This confirms origin and integrity, not that the code is harmless.",
        TrustStateDto.LocalUnverified => "Installed from a local ZIP without an official signature. Its origin is not verified. Its files are inventoried, so later changes are detected, but that is not an approval. Plugins run inside the daemon with your permissions.",
        TrustStateDto.VerificationFailed => "The official signature no longer verifies. The plugin will not be loaded. Reinstall it.",
        TrustStateDto.Modified => "Files changed after installation. The plugin will not be loaded. Reinstall it.",
        TrustStateDto.Incompatible => "Not compatible with this version of ModularRuntime.",
        TrustStateDto.Unmanaged => "Copied into the plugin folder by hand. Not managed or verified by the package manager.",
        TrustStateDto.Revoked => "Revoked by the official registry. It will not be loaded again; if it is running it keeps running until you stop it. Stop and remove it. The installation was not deleted automatically.",
        _ => string.Empty,
    };
}

/// <summary>One package operation shown while it runs.</summary>
public sealed class OperationItemViewModel(PackageOperationDto operation) : ObservableObject
{
    private PackageOperationDto _operation = operation;

    public string OperationId => _operation.OperationId;

    public string? PluginId => _operation.PluginId;

    public PackageOperationStageDto Stage => _operation.Stage;

    public string Message => _operation.Message;

    public bool HasProgress => _operation.Progress is not null && !_operation.IsFinished;

    public double ProgressPercent => (_operation.Progress ?? 0) * 100;

    public bool IsIndeterminate => _operation.Progress is null && !_operation.IsFinished;

    public bool IsFinished => _operation.IsFinished;

    public string Title => _operation.Kind switch
    {
        PackageOperationKindDto.InstallOfficial => $"Installing {_operation.PluginId}",
        PackageOperationKindDto.InstallLocal => $"Installing {_operation.PluginId ?? "local package"}",
        PackageOperationKindDto.Update => $"Updating {_operation.PluginId}",
        _ => $"Removing {_operation.PluginId}",
    };

    public PackageOperationDto Operation => _operation;

    internal void Update(PackageOperationDto operation)
    {
        _operation = operation;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>The message shown when the daemon reports the final stage.</summary>
    public static string Outcome(PackageOperationDto operation) =>
        operation.Stage == PackageOperationStageDto.Completed
            ? operation.Message + (operation.Kind is PackageOperationKindDto.Remove ? string.Empty : " It is not started automatically.")
            : $"{new OperationItemViewModel(operation).Title} failed [{operation.ErrorCode}]: {operation.Message}";
}
