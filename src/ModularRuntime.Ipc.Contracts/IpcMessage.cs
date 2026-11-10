using System.Text.Json;

namespace ModularRuntime.Ipc.Contracts;

/// <summary>The single envelope carried by every frame.</summary>
public sealed class IpcMessage
{
    /// <summary>One of <see cref="IpcMessageTypes"/>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Request id chosen by the client (unique per connection); echoed in responses and cancels.</summary>
    public long? Id { get; set; }

    /// <summary>Operation (requests) or notification name (notifications).</summary>
    public string? Op { get; set; }

    /// <summary>Operation-specific payload.</summary>
    public JsonElement? Payload { get; set; }

    /// <summary>Set on failed responses and protocol errors.</summary>
    public IpcError? Error { get; set; }
}

/// <summary>Structured error with a stable code and an optional neutral diagnostic.</summary>
public sealed record IpcError(string Code, string Message, DiagnosticDto? Diagnostic = null);

/// <summary>Text-only exception snapshot (mirrors PluginDiagnostic; never an exception object).</summary>
public sealed record DiagnosticDto(string ExceptionType, string Message, string? StackTrace, string? Detail);

/// <summary>Handshake request.</summary>
public sealed record HelloPayload(int ProtocolVersion, string ClientName, Guid ClientId);

/// <summary>Handshake answer.</summary>
public sealed record WelcomePayload(
    int ProtocolVersion,
    Guid DaemonInstanceId,
    Guid RuntimeId,
    string DaemonVersion,
    Guid ConnectionId,
    int MaxMessageBytes);
