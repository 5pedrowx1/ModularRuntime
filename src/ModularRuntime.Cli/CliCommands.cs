using System.Threading.Channels;
using ModularRuntime.Ipc;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Cli;

/// <summary>
/// Commands executed against the daemon. Text output follows the former standalone host's
/// format; every command also supports <c>--json</c>.
/// </summary>
internal static class CliCommands
{
    public static async Task<int> StatusAsync(CliContext context, IRuntimeClient client)
    {
        var status = await client.GetStatusAsync(context.Cancellation).ConfigureAwait(false);
        context.Write(status, () =>
        {
            var o = context.Output;
            o.WriteLine($"Daemon:          {status.State} (pid {status.ProcessId})");
            o.WriteLine($"Version:         {status.DaemonVersion} (plugin API {status.PluginApiVersion}, IPC protocol {status.ProtocolVersion})");
            o.WriteLine($"Uptime:          {FormatUptime(status.UptimeSeconds)}");
            o.WriteLine($"Runtime ID:      {status.RuntimeId}");
            o.WriteLine($"Profile:         {status.ProfileDirectory}");
            o.WriteLine($"Plugins folder:  {status.PluginDirectory}");
            o.WriteLine($"Logs folder:     {status.LogDirectory}");
            o.WriteLine($"Plugins:         {status.PluginCount} total, {status.RunningPluginCount} running, {status.FailedPluginCount} failed");
            o.WriteLine($"Services:        {status.ServiceCount}");
            o.WriteLine($"Extension points:{status.ExtensionPointCount,2}");
            o.WriteLine($"Subscriptions:   {status.SubscriptionCount}");
            o.WriteLine($"Clients:         {string.Join(", ", status.Clients.Select(c => c.ClientName))}");
        });
        return CliExitCodes.Success;
    }

    public static async Task<int> ListPluginsAsync(CliContext context, IRuntimeClient client)
    {
        var response = await client.ListPluginsAsync(context.Cancellation).ConfigureAwait(false);
        context.Write(response, () =>
        {
            if (response.Plugins.Count == 0)
            {
                context.Output.WriteLine("No plugins are registered.");
                return;
            }

            var idWidth = Math.Max(2, response.Plugins.Max(p => p.Id.Length));
            context.Output.WriteLine($"{"ID".PadRight(idWidth)}  {"STATE",-10} {"VERSION",-9} NAME");
            foreach (var plugin in response.Plugins)
            {
                var error = plugin.HasError ? $"  [{plugin.LastErrorCode}]" : string.Empty;
                context.Output.WriteLine($"{plugin.Id.PadRight(idWidth)}  {plugin.State,-10} {plugin.Version,-9} {plugin.Name}{error}");
            }
        });
        return CliExitCodes.Success;
    }

    public static async Task<int> PluginInfoAsync(CliContext context, IRuntimeClient client, string pluginId)
    {
        var details = await client.GetPluginAsync(pluginId, context.Cancellation).ConfigureAwait(false);
        context.Write(details, () =>
        {
            var o = context.Output;
            var s = details.Summary;
            o.WriteLine($"ID:             {s.Id}");
            o.WriteLine($"Name:           {s.Name}");
            o.WriteLine($"Version:        {s.Version}");
            o.WriteLine($"API version:    {details.ApiVersion}");
            o.WriteLine($"State:          {s.State}");
            o.WriteLine($"Start order:    {s.StartOrder}");
            o.WriteLine($"Entry:          {details.EntryType} ({details.EntryAssembly})");
            o.WriteLine($"Instance:       {s.InstanceId?.ToString() ?? "<none>"}");
            o.WriteLine($"Started:        {s.StartedAt?.ToLocalTime().ToString("u") ?? "<none>"}");
            o.WriteLine($"Stopped:        {s.StoppedAt?.ToLocalTime().ToString("u") ?? "<none>"}");
            o.WriteLine($"Resources:      {details.Resources.Count}");
            foreach (var resource in details.Resources)
            {
                o.WriteLine($"  {resource.Kind,-22} {resource.Name}");
            }

            if (details.LastError is { } error)
            {
                o.WriteLine($"Last error:     [{error.Code}] {error.Operation}: {error.Message}");
                if (error.Diagnostic is { } diagnostic)
                {
                    o.WriteLine($"Exception:      {diagnostic.ExceptionType}: {diagnostic.Message}");
                }
            }
        });
        return CliExitCodes.Success;
    }

    public static async Task<int> StartPluginAsync(CliContext context, IRuntimeClient client, string pluginId) =>
        Report(context, await client.StartPluginAsync(pluginId, context.Cancellation).ConfigureAwait(false));

    public static async Task<int> StopPluginAsync(CliContext context, IRuntimeClient client, string pluginId) =>
        Report(context, await client.StopPluginAsync(pluginId, context.Cancellation).ConfigureAwait(false));

    public static async Task<int> RestartPluginAsync(CliContext context, IRuntimeClient client, string pluginId) =>
        Report(context, await client.RestartPluginAsync(pluginId, context.Cancellation).ConfigureAwait(false));

    public static async Task<int> StartAllAsync(CliContext context, IRuntimeClient client) =>
        Report(context, await client.StartAllAsync(context.Cancellation).ConfigureAwait(false), ignoreInvalidState: true);

    public static async Task<int> StopAllAsync(CliContext context, IRuntimeClient client) =>
        Report(context, await client.StopAllAsync(context.Cancellation).ConfigureAwait(false), ignoreInvalidState: true);

    public static async Task<int> ListServicesAsync(CliContext context, IRuntimeClient client)
    {
        var response = await client.GetServicesAsync(context.Cancellation).ConfigureAwait(false);
        context.Write(response, () =>
        {
            if (response.Services.Count == 0)
            {
                context.Output.WriteLine("No services are registered.");
            }

            foreach (var service in response.Services)
            {
                var availability = service.IsAvailable ? "available" : "unavailable";
                context.Output.WriteLine($"{service.ServiceId,-32} {service.Version,-8} {availability,-12} {service.Owner.Display}");
                context.Output.WriteLine($"  operations: {string.Join(", ", service.Operations)}");
            }
        });
        return CliExitCodes.Success;
    }

    public static async Task<int> ListExtensionsAsync(CliContext context, IRuntimeClient client)
    {
        var response = await client.GetExtensionsAsync(context.Cancellation).ConfigureAwait(false);
        context.Write(response, () =>
        {
            if (response.ExtensionPoints.Count == 0)
            {
                context.Output.WriteLine("No extension points are declared.");
            }

            foreach (var point in response.ExtensionPoints)
            {
                context.Output.WriteLine($"{point.ExtensionPointId,-32} {point.ContractVersion,-8} {point.Owner.Display}");
                foreach (var contribution in point.Contributions)
                {
                    context.Output.WriteLine($"  - {contribution.ContributionId,-24} {contribution.DisplayName,-24} {contribution.Owner.Display}");
                }
            }
        });
        return CliExitCodes.Success;
    }

    public static async Task<int> ListSubscriptionsAsync(CliContext context, IRuntimeClient client)
    {
        var response = await client.GetSubscriptionsAsync(context.Cancellation).ConfigureAwait(false);
        context.Write(response, () =>
        {
            if (response.Subscriptions.Count == 0)
            {
                context.Output.WriteLine("No active subscriptions.");
            }

            foreach (var subscription in response.Subscriptions)
            {
                context.Output.WriteLine($"{subscription.Topic,-32} {subscription.Subscriber.Display}");
            }
        });
        return CliExitCodes.Success;
    }

    /// <summary>Prints recent entries; with <paramref name="follow"/> streams new ones until Ctrl+C or disconnection.</summary>
    public static async Task<int> LogsAsync(CliContext context, IRuntimeClient client, LogFilterDto filter, int limit, bool follow)
    {
        var history = await client.QueryLogsAsync(new LogQueryRequest(filter, null, limit), context.Cancellation).ConfigureAwait(false);
        foreach (var entry in history.Entries)
        {
            WriteEntry(context, entry);
        }

        if (!follow)
        {
            return CliExitCodes.Success;
        }

        var lastSequence = history.LastSequence;
        var queue = Channel.CreateUnbounded<LogBatchNotification>();
        client.NotificationReceived += (_, notification) =>
        {
            if (notification.Name == IpcNotifications.LogEntries && notification.Read<LogBatchNotification>() is { } batch)
            {
                queue.Writer.TryWrite(batch);
            }
        };
        _ = client.Completion.ContinueWith(_ => queue.Writer.TryComplete(), TaskScheduler.Default);

        var subscription = await client.SubscribeLogsAsync(filter, context.Cancellation).ConfigureAwait(false);

        // Entries logged between the history query and the subscription are fetched once.
        var gap = await client.QueryLogsAsync(new LogQueryRequest(filter, lastSequence, 5000), context.Cancellation).ConfigureAwait(false);
        foreach (var entry in gap.Entries.Where(e => e.Sequence <= subscription.LastSequence))
        {
            WriteEntry(context, entry);
            lastSequence = entry.Sequence;
        }

        lastSequence = Math.Max(lastSequence, subscription.LastSequence);
        try
        {
            await foreach (var batch in queue.Reader.ReadAllAsync(context.Cancellation).ConfigureAwait(false))
            {
                if (batch.DroppedCount > 0)
                {
                    context.Error.WriteLine($"... {batch.DroppedCount} log entries were dropped (the client fell behind) ...");
                }

                foreach (var entry in batch.Entries.Where(e => e.Sequence > lastSequence))
                {
                    WriteEntry(context, entry);
                    lastSequence = entry.Sequence;
                }
            }
        }
        catch (OperationCanceledException) when (context.Cancellation.IsCancellationRequested)
        {
            return CliExitCodes.Success;
        }

        context.Error.WriteLine("The daemon closed the connection.");
        return CliExitCodes.DaemonUnavailable;
    }

    private static void WriteEntry(CliContext context, LogEntryDto entry)
    {
        if (context.Json)
        {
            context.Write(entry, static () => { });
            return;
        }

        var scope = entry.PluginId is null ? $"[{entry.Category}]" : $"[{entry.Category}] [{entry.PluginId}]";
        context.Output.WriteLine($"{entry.Timestamp.ToLocalTime():HH:mm:ss} {Abbreviate(entry.Level)} {scope} {entry.Message}");
        if (entry.Diagnostic is { } diagnostic)
        {
            context.Output.WriteLine($"    {diagnostic.ExceptionType}: {diagnostic.Message}");
        }
    }

    private static int Report(CliContext context, OperationResultDto result)
    {
        context.Write(result, () => WriteResult(context.Output, result));
        return result.IsSuccess ? CliExitCodes.Success : CliExitCodes.OperationFailed;
    }

    private static int Report(CliContext context, OperationResultsResponse response, bool ignoreInvalidState)
    {
        var relevant = response.Results.Where(r => !(ignoreInvalidState && r.ErrorCode == "invalid_state")).ToList();
        context.Write(response, () =>
        {
            if (relevant.Count == 0)
            {
                context.Output.WriteLine("No plugins were affected.");
            }

            foreach (var result in relevant)
            {
                WriteResult(context.Output, result);
            }
        });
        return relevant.All(r => r.IsSuccess) ? CliExitCodes.Success : CliExitCodes.OperationFailed;
    }

    private static void WriteResult(TextWriter output, OperationResultDto result)
    {
        var state = result.State ?? "unknown";
        output.WriteLine(result.IsSuccess
            ? $"{result.Operation} '{result.PluginId}': {result.Status} ({state})."
            : $"{result.Operation} '{result.PluginId}': {result.Status} ({state}) [{result.ErrorCode}] {result.Message}");
        if (!result.IsSuccess && result.Diagnostic is { } diagnostic)
        {
            output.WriteLine($"  {diagnostic.ExceptionType}: {diagnostic.Message}");
        }
    }

    private static string Abbreviate(LogLevelDto level) => level switch
    {
        LogLevelDto.Trace => "TRC",
        LogLevelDto.Debug => "DBG",
        LogLevelDto.Information => "INF",
        LogLevelDto.Warning => "WRN",
        LogLevelDto.Error => "ERR",
        _ => "CRT",
    };

    private static string FormatUptime(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        var clock = span.ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {clock}" : clock;
    }
}
