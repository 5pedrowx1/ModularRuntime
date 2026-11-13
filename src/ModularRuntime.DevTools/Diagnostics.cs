namespace ModularRuntime.DevTools;

internal enum Severity
{
    Info,
    Warning,
    Error,
}

/// <summary>One finding of a validation. Codes are stable (documented in docs/PLUGIN_PACKAGING.md).</summary>
internal sealed record Diagnostic(Severity Severity, string Code, string Message, string? Path = null);

/// <summary>Stable diagnostic codes of the developer tool.</summary>
internal static class DiagnosticCodes
{
    public const string ManifestMissing = "manifest_missing";
    public const string ManifestInvalid = "manifest_invalid";
    public const string EntryAssemblyNameMismatch = "entry_assembly_name_mismatch";
    public const string TargetFrameworkUnsupported = "target_framework_unsupported";
    public const string ContractsReferenceMissing = "contracts_reference_missing";
    public const string HostAssemblyReferenced = "host_assembly_referenced";
    public const string DynamicLoadingDisabled = "dynamic_loading_disabled";
    public const string ManifestNotCopied = "manifest_not_copied";
    public const string VersionMismatch = "version_mismatch";
    public const string NotBuilt = "not_built";
    public const string EntryAssemblyMissing = "entry_assembly_missing";
    public const string EntryAssemblyInvalid = "entry_assembly_invalid";
    public const string EntryTypeNotFound = "entry_type_not_found";
    public const string EntryTypeNotAPlugin = "entry_type_not_a_plugin";
    public const string EntryTypeNotInstantiable = "entry_type_not_instantiable";
    public const string ApiVersionMismatch = "api_version_mismatch";
    public const string DependencyMissing = "dependency_missing";
    public const string UnsafePath = "unsafe_path";
    public const string PathCollision = "path_collision";
    public const string PackageLimits = "package_limits";
    public const string SharedContractDependency = "shared_contract_dependency";
    public const string SharedContractUnused = "shared_contract_unused";
}

internal sealed class ValidationReport
{
    private readonly List<Diagnostic> _items = [];

    public IReadOnlyList<Diagnostic> Items => _items;

    public bool HasErrors => _items.Any(d => d.Severity == Severity.Error);

    public int ErrorCount => _items.Count(d => d.Severity == Severity.Error);

    public int WarningCount => _items.Count(d => d.Severity == Severity.Warning);

    public void Error(string code, string message, string? path = null) => _items.Add(new Diagnostic(Severity.Error, code, message, path));

    public void Warning(string code, string message, string? path = null) => _items.Add(new Diagnostic(Severity.Warning, code, message, path));

    public void Info(string code, string message, string? path = null) => _items.Add(new Diagnostic(Severity.Info, code, message, path));

    public void WriteTo(TextWriter output)
    {
        foreach (var item in _items)
        {
            var label = item.Severity switch
            {
                Severity.Error => "error",
                Severity.Warning => "warning",
                _ => "info",
            };
            output.WriteLine($"  {label} [{item.Code}] {item.Message}" + (item.Path is null ? string.Empty : $" ({item.Path})"));
        }
    }
}
