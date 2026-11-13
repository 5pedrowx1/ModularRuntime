namespace ModularRuntime.Loader;

/// <summary>Stable error codes produced while loading a validated plugin.</summary>
public static class PluginLoadErrorCodes
{
    public const string EntryAssemblyMissing = "entry_assembly_missing";
    public const string AssemblyLoadFailed = "assembly_load_failed";
    public const string ContractsAssemblyMismatch = "contracts_assembly_mismatch";
    public const string PluginDescriptorMismatch = "plugin_descriptor_mismatch";
    public const string EntryTypeNotFound = "entry_type_not_found";
    public const string EntryTypeNotAPlugin = "entry_type_not_a_plugin";
    public const string EntryTypeNotInstantiable = "entry_type_not_instantiable";
    public const string PluginInstanceCreationFailed = "plugin_instance_creation_failed";
    public const string FactoryReleased = "factory_released";
    public const string FactoryAlreadyConsumed = "factory_already_consumed";

    /// <summary>The installation failed the verification required before its assemblies are loaded.</summary>
    public const string InstallationVerificationFailed = "installation_verification_failed";

    /// <summary>The installation was revoked (by the signed catalog) and must not be loaded.</summary>
    public const string InstallationRevoked = "installation_revoked";

    // Shared contract assemblies (plugin API 0.4)
    public const string SharedContractMissing = "shared_contract_missing";
    public const string SharedContractInvalid = "shared_contract_invalid";
    public const string SharedContractVersionConflict = "shared_contract_version_conflict";
    public const string SharedContractDependency = "shared_contract_dependency";
}
