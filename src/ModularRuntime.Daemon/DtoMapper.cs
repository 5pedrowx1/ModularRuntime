using ModularRuntime.Contracts;
using ModularRuntime.Engine;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Daemon;

/// <summary>Converts runtime snapshots into neutral IPC DTOs (strings, numbers, GUIDs only).</summary>
internal static class DtoMapper
{
    public static DiagnosticDto? ToDto(PluginDiagnostic? diagnostic) =>
        diagnostic is null ? null : new DiagnosticDto(diagnostic.ExceptionType, diagnostic.Message, diagnostic.StackTrace, diagnostic.Detail);

    public static PluginSummaryDto ToSummary(PluginRuntimeInfo info) =>
        new(
            info.Descriptor.Id.Value,
            info.Descriptor.Name,
            info.Descriptor.Version.ToString(),
            info.State.ToString(),
            info.Descriptor.StartOrder,
            info.InstanceId,
            info.StartedAt,
            info.StoppedAt,
            info.LastError is not null,
            info.LastError?.Code);

    public static PluginDetailsDto ToDetails(PluginRuntimeInfo info, IReadOnlyList<ResourceInfo> resources) =>
        new(
            ToSummary(info),
            info.Descriptor.ApiVersion.ToString(),
            info.Descriptor.EntryAssembly,
            info.Descriptor.EntryType,
            info.LastError is { } error
                ? new ErrorInfoDto(error.Code, error.Operation, error.Message, error.OccurredAt, ToDto(error.Diagnostic))
                : null,
            [.. resources.Select(r => new ResourceDto(r.ResourceId, r.Kind.ToString(), r.Name, r.CreatedAt))]);

    public static OperationResultDto ToResult(PluginOperationResult result) =>
        new(
            result.PluginId.Value,
            result.Operation,
            result.Status.ToString(),
            result.State?.ToString(),
            result.ErrorCode,
            result.Message,
            ToDto(result.Diagnostic));

    public static OwnerDto ToDto(ResourceOwner owner) =>
        new(owner.IsHost ? "host" : "plugin", owner.PluginId?.Value, owner.InstanceId, owner.ToString());

    public static SubscriptionDto ToDto(EventSubscriptionInfo info) =>
        new(info.SubscriptionId, info.Topic, ToDto(info.Subscriber), info.CreatedAt);

    public static ServiceDto ToDto(PluginServiceDescriptor service) =>
        new(
            service.RegistrationId,
            service.ServiceId,
            service.Name,
            service.Version.ToString(),
            service.Operations,
            service.Metadata,
            ToDto(service.Owner),
            service.IsAvailable,
            service.RegisteredAt,
            service.Kind.ToString(),
            service.ContractTypeName);

    public static StreamDto ToDto(StreamDescriptor stream) =>
        new(
            stream.RegistrationId,
            stream.StreamId,
            stream.Name,
            stream.Version.ToString(),
            stream.ItemTypeName,
            stream.Metadata,
            ToDto(stream.Owner),
            stream.IsAvailable,
            stream.SubscriberCount,
            stream.Sequence,
            stream.DefaultCapacity,
            stream.DefaultFullMode.ToString(),
            stream.CreatedAt);

    public static SharedContractDto ToDto(Loader.SharedContractInfo contract) =>
        new(contract.Name, contract.Version.ToString(), [.. contract.Users.Select(u => u.Value)]);

    public static ExtensionPointDto ToDto(ExtensionPointDescriptor point, IReadOnlyList<ExtensionContributionDescriptor> contributions) =>
        new(
            point.RegistrationId,
            point.ExtensionPointId,
            point.Name,
            point.ContractVersion.ToString(),
            point.Operations,
            point.Metadata,
            ToDto(point.Owner),
            point.IsActive,
            [.. contributions.Select(c => new ContributionDto(
                c.RegistrationId,
                c.ContributionId,
                c.ContractVersion.ToString(),
                c.DisplayName,
                c.Metadata,
                ToDto(c.Owner),
                c.IsActive))]);
}
