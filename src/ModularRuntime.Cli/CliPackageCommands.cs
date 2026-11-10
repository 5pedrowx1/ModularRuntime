using System.Globalization;
using ModularRuntime.Ipc;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Cli;

/// <summary>Registry and package commands. All work is done by the daemon; the CLI only asks and reports.</summary>
internal static class CliPackageCommands
{
    public static async Task<int> RegistryStatusAsync(CliContext context, IRuntimeClient client)
    {
        var status = await client.GetRegistryStatusAsync(context.Cancellation).ConfigureAwait(false);
        context.Write(status, () => WriteRegistryStatus(context.Output, status));
        return CliExitCodes.Success;
    }

    public static async Task<int> RegistryRefreshAsync(CliContext context, IRuntimeClient client)
    {
        var status = await client.RefreshRegistryAsync(context.Cancellation).ConfigureAwait(false);
        context.Write(status, () => WriteRegistryStatus(context.Output, status));
        return CliExitCodes.Success;
    }

    public static async Task<int> RegistryListAsync(CliContext context, IRuntimeClient client)
    {
        var response = await client.ListRegistryAsync(context.Cancellation).ConfigureAwait(false);
        context.Write(response, () =>
        {
            if (!response.Status.Loaded)
            {
                WriteRegistryStatus(context.Output, response.Status);
                return;
            }

            if (response.Plugins.Count == 0)
            {
                context.Output.WriteLine("The official registry has no plugins.");
                return;
            }

            var width = Math.Max(2, response.Plugins.Max(p => p.Id.Length));
            context.Output.WriteLine($"{"ID".PadRight(width)}  {"VERSION",-9} {"INSTALLED",-10} {"CATEGORY",-14} NAME");
            foreach (var plugin in response.Plugins)
            {
                var installed = plugin.InstalledVersion ?? "-";
                var note = plugin.Status != "published" ? $"  [{plugin.Status}]"
                    : !plugin.Compatible ? "  [incompatible]" : plugin.UpdateAvailable ? "  [update available]" : string.Empty;
                context.Output.WriteLine($"{plugin.Id.PadRight(width)}  {plugin.Version,-9} {installed,-10} {plugin.Category,-14} {plugin.Name}{note}");
            }

            context.Output.WriteLine($"Registry revision {response.Status.Revision}, verified {response.Status.VerifiedAt?.ToLocalTime():g} (key {response.Status.SigningKeyId}).");
        });
        return CliExitCodes.Success;
    }

    public static async Task<int> RegistryInfoAsync(CliContext context, IRuntimeClient client, string pluginId)
    {
        var plugin = await client.GetRegistryEntryAsync(pluginId, context.Cancellation).ConfigureAwait(false);
        context.Write(plugin, () =>
        {
            var o = context.Output;
            o.WriteLine($"{plugin.Name} ({plugin.Id}) {plugin.Version}");
            o.WriteLine($"  Author:            {plugin.Author}");
            o.WriteLine($"  Category:          {plugin.Category}");
            o.WriteLine($"  Description:       {plugin.Description}");
            o.WriteLine($"  Plugin API:        {plugin.ApiVersion}  (minimum ModularRuntime {plugin.MinimumRuntimeVersion})");
            o.WriteLine($"  Compatible:        {(plugin.Compatible ? "yes" : $"no — {plugin.IncompatibilityReason}")}");
            o.WriteLine($"  Status:            {plugin.Status}{(plugin.StatusReason is null ? string.Empty : $" — {plugin.StatusReason}")}{(plugin.AdvisoryUrl is null ? string.Empty : $" ({plugin.AdvisoryUrl})")}");
            if (plugin.License is not null)
            {
                o.WriteLine($"  License:           {plugin.License}");
            }

            if (plugin.BuildId is not null)
            {
                o.WriteLine($"  Build:             {plugin.BuildId} (approval SHA-256 {plugin.ApprovalSha256 ?? "-"})");
            }
            o.WriteLine($"  Package:           {plugin.PackageSize} bytes, SHA-256 {plugin.PackageSha256}");
            o.WriteLine($"  Signing key:       {plugin.SigningKeyId}");
            o.WriteLine($"  Published:         {plugin.PublishedAt.ToLocalTime():g}");
            if (plugin.SourceRepository is not null)
            {
                o.WriteLine($"  Source:            {plugin.SourceRepository} @ {plugin.SourceCommit}");
            }

            o.WriteLine($"  Installed version: {plugin.InstalledVersion ?? "-"}");
        });
        return CliExitCodes.Success;
    }

    public static async Task<int> InstallOfficialAsync(CliContext context, IRuntimeClient client, string pluginId, string? version)
    {
        var operation = await client.InstallOfficialAsync(pluginId, version, context.Cancellation).ConfigureAwait(false);
        return await FollowAsync(context, client, operation).ConfigureAwait(false);
    }

    public static async Task<int> UpdateAsync(CliContext context, IRuntimeClient client, string pluginId, bool stopIfRunning)
    {
        var operation = await client.UpdatePackageAsync(pluginId, stopIfRunning, context.Cancellation).ConfigureAwait(false);
        return await FollowAsync(context, client, operation).ConfigureAwait(false);
    }

    public static async Task<int> RemoveAsync(CliContext context, IRuntimeClient client, string pluginId, bool stopIfRunning, bool yes)
    {
        if (!yes && !Confirm(context, $"Remove '{pluginId}' and delete its files? Type 'remove' to continue: ", "remove"))
        {
            context.Error.WriteLine("Not removed. Pass --yes to remove without asking.");
            return CliExitCodes.Usage;
        }

        var operation = await client.RemovePackageAsync(pluginId, stopIfRunning, context.Cancellation).ConfigureAwait(false);
        return await FollowAsync(context, client, operation).ConfigureAwait(false);
    }

    public static async Task<int> VerifyAsync(CliContext context, IRuntimeClient client, string pluginId)
    {
        var result = await client.VerifyPackageAsync(pluginId, context.Cancellation).ConfigureAwait(false);
        context.Write(result, () =>
        {
            context.Output.WriteLine($"{pluginId}: {result.Status}. {result.Message}");
            foreach (var problem in result.Problems)
            {
                context.Output.WriteLine($"  - {problem}");
            }
        });
        return result.Status == "Verified" ? CliExitCodes.Success : CliExitCodes.OperationFailed;
    }

    public static async Task<int> UpdatesAsync(CliContext context, IRuntimeClient client)
    {
        var response = await client.CheckUpdatesAsync(context.Cancellation).ConfigureAwait(false);
        context.Write(response, () =>
        {
            if (!response.Registry.Loaded)
            {
                WriteRegistryStatus(context.Output, response.Registry);
                return;
            }

            if (response.Updates.Count == 0)
            {
                context.Output.WriteLine("Every installed package is up to date.");
                return;
            }

            foreach (var update in response.Updates)
            {
                var note = update.Compatible ? string.Empty : $"  [incompatible: {update.IncompatibilityReason}]";
                context.Output.WriteLine($"{update.PluginId}  {update.InstalledVersion} -> {update.AvailableVersion}  {update.Name}{note}");
            }
        });
        return CliExitCodes.Success;
    }

    public static async Task<int> PackagesAsync(CliContext context, IRuntimeClient client)
    {
        var response = await client.ListPackagesAsync(context.Cancellation).ConfigureAwait(false);
        context.Write(response, () =>
        {
            if (response.Packages.Count == 0)
            {
                context.Output.WriteLine("No plugins are installed.");
                return;
            }

            var width = Math.Max(2, response.Packages.Max(p => p.PluginId.Length));
            context.Output.WriteLine($"{"ID".PadRight(width)}  {"VERSION",-9} {"STATE",-10} {"TRUST",-19} SOURCE");
            foreach (var package in response.Packages)
            {
                var trust = TrustLabel(package.Trust, package.SignedWithTestKey);
                var update = package.Revocation is { } revocation ? $"  [REVOKED: {revocation.Reason}]"
                    : package.CatalogStatus is "withdrawn" ? "  [withdrawn from the Store]"
                    : package.AvailableVersion is null ? string.Empty : $"  [update {package.AvailableVersion}]";
                context.Output.WriteLine($"{package.PluginId.PadRight(width)}  {package.Version,-9} {package.EngineState ?? "-",-10} {trust,-19} {package.Source}{update}");
            }
        });
        return CliExitCodes.Success;
    }

    /// <summary>Uploads a local ZIP to the daemon, shows the daemon's verification, and installs on confirmation.</summary>
    public static async Task<int> InstallZipAsync(CliContext context, IRuntimeClient client, string path, bool allowUnverified, bool stopIfRunning)
    {
        if (!File.Exists(path))
        {
            throw new UsageException($"'{path}' does not exist.");
        }

        Guid uploadId;
        await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
        {
            uploadId = await client.UploadPackageAsync(file, Path.GetFileName(path), cancellationToken: context.Cancellation).ConfigureAwait(false);
        }

        var preview = await client.InspectUploadAsync(uploadId, context.Cancellation).ConfigureAwait(false);
        if (!preview.Valid)
        {
            await client.DiscardUploadAsync(uploadId, context.Cancellation).ConfigureAwait(false);
            context.Write(preview, () => context.Error.WriteLine($"Rejected [{preview.ErrorCode}]: {preview.ErrorMessage}"));
            return CliExitCodes.OperationFailed;
        }

        if (!context.Json)
        {
            var o = context.Output;
            o.WriteLine($"{preview.Name} ({preview.PluginId}) {preview.Version}");
            o.WriteLine($"  Author:     {preview.Author ?? "(not declared)"}");
            o.WriteLine($"  Trust:      {TrustLabel(preview.Trust, preview.SignedWithTestKey)}{(preview.SigningKeyId is null ? string.Empty : $" (key {preview.SigningKeyId})")}");
            o.WriteLine($"  Integrity:  {preview.FileCount} files, every SHA-256 checked by the daemon");
            o.WriteLine($"  Package:    {preview.PackageSize} bytes, SHA-256 {preview.PackageSha256}");
            if (preview.ReplacesExisting)
            {
                o.WriteLine($"  Replaces:   installed version {preview.InstalledVersion}{(preview.ExistingIsRunning ? " (running)" : string.Empty)}");
            }
        }

        var unverified = preview.Trust != TrustStateDto.OfficialVerified;
        if (unverified && !allowUnverified)
        {
            context.Error.WriteLine("WARNING: this package has no official signature. Its origin is not verified and its code runs inside the daemon with your permissions.");
            if (!Confirm(context, "Type 'install' to install it anyway (it will not be started automatically): ", "install"))
            {
                await client.DiscardUploadAsync(uploadId, context.Cancellation).ConfigureAwait(false);
                context.Error.WriteLine("Not installed. Non-interactive installs of unverified packages require --allow-unverified.");
                return CliExitCodes.RequestFailed;
            }
        }

        var operation = await client.InstallUploadAsync(uploadId, acceptUnverified: unverified, stopIfRunning, context.Cancellation).ConfigureAwait(false);
        return await FollowAsync(context, client, operation).ConfigureAwait(false);
    }

    public static string TrustLabel(TrustStateDto trust, bool testKey) => trust switch
    {
        TrustStateDto.OfficialVerified => testKey ? "official (TEST KEY)" : "official verified",
        TrustStateDto.LocalUnverified => "local, unverified",
        TrustStateDto.VerificationFailed => "VERIFICATION FAILED",
        TrustStateDto.Modified => "MODIFIED",
        TrustStateDto.Incompatible => "incompatible",
        TrustStateDto.Revoked => "REVOKED",
        _ => "unmanaged",
    };

    /// <summary>Polls the operation until it finishes, printing stage changes.</summary>
    private static async Task<int> FollowAsync(CliContext context, IRuntimeClient client, PackageOperationDto operation)
    {
        var lastLine = string.Empty;
        while (!operation.IsFinished)
        {
            if (!context.Json)
            {
                var line = operation.Progress is { } p && operation.Stage == PackageOperationStageDto.Downloading
                    ? $"{operation.Stage}: {operation.Message} {p.ToString("P0", CultureInfo.InvariantCulture)}"
                    : $"{operation.Stage}: {operation.Message}";
                if (line != lastLine)
                {
                    context.Output.WriteLine(line);
                    lastLine = line;
                }
            }

            await Task.Delay(200, context.Cancellation).ConfigureAwait(false);
            operation = await client.GetOperationAsync(operation.OperationId, context.Cancellation).ConfigureAwait(false);
        }

        context.Write(operation, () =>
        {
            if (operation.Stage == PackageOperationStageDto.Completed)
            {
                context.Output.WriteLine(operation.Message);
                if (operation.Result is { } result && operation.Kind != PackageOperationKindDto.Remove)
                {
                    context.Output.WriteLine($"Trust: {TrustLabel(result.Trust, result.SignedWithTestKey)}. Start it with: plugins start {result.PluginId}");
                }
            }
            else
            {
                context.Error.WriteLine($"{operation.Stage} [{operation.ErrorCode}]: {operation.Message}");
            }
        });
        return operation.Stage == PackageOperationStageDto.Completed ? CliExitCodes.Success : CliExitCodes.OperationFailed;
    }

    private static void WriteRegistryStatus(TextWriter output, RegistryStatusDto status)
    {
        if (!status.Configured)
        {
            output.WriteLine("No official registry is configured for this daemon.");
            return;
        }

        output.WriteLine($"Registry:          {status.Location}");
        if (status.Loaded)
        {
            output.WriteLine($"Revision:          {status.Revision} ({status.PluginCount} entries), signed by '{status.SigningKeyId}'{(status.TestKeysTrusted ? " — TEST KEYS ARE TRUSTED" : string.Empty)}");
            output.WriteLine($"Generated/expires: {status.GeneratedAt?.ToLocalTime():g} / {status.ExpiresAt?.ToLocalTime():g}");
            output.WriteLine($"Verified:          {status.VerifiedAt?.ToLocalTime():g}");
        }
        else
        {
            output.WriteLine("Not loaded yet. Run 'registry refresh'.");
        }

        if (status.ErrorCode is not null)
        {
            output.WriteLine($"Last error:        [{status.ErrorCode}] {status.ErrorMessage}");
        }
    }

    private static bool Confirm(CliContext context, string prompt, string expected)
    {
        if (context.Json || Console.IsInputRedirected)
        {
            return false;
        }

        context.Error.Write(prompt);
        return string.Equals(Console.ReadLine()?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }
}
