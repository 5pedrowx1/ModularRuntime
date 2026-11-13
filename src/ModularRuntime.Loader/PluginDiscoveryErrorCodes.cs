using ModularRuntime.Contracts;

namespace ModularRuntime.Loader;

/// <summary>Stable error codes produced while discovering plugin installations.</summary>
public static class PluginDiscoveryErrorCodes
{
    public const string PluginDirectoryInvalid = "plugin_directory_invalid";
    public const string PluginDirectoryNotFound = "plugin_directory_not_found";
    public const string PluginDirectoryAccessFailed = "plugin_directory_access_failed";
    public const string ManifestReadFailed = "manifest_read_failed";
    public const string ManifestJsonInvalid = "manifest_json_invalid";
    public const string ManifestFieldMissing = "manifest_field_missing";
    public const string ManifestFieldInvalid = "manifest_field_invalid";
    public const string ApiVersionIncompatible = "api_version_incompatible";
    public const string EntryAssemblyPathInvalid = "entry_assembly_path_invalid";
    public const string EntryAssemblyMissing = "entry_assembly_missing";
    public const string DuplicatePluginId = "duplicate_plugin_id";

    /// <summary>A manifest declares a shared contract whose assembly file is not in the installation.</summary>
    public const string SharedContractMissing = "shared_contract_missing";
}
