namespace ModularRuntime.Engine;

/// <summary>
/// What the runtime does when a value crossing a typed boundary (typed-service argument or
/// result, callback argument or result, stream item) is not a <em>shared value</em>: its type, or
/// the type of something it references (a delegate target, a collection element, a field), is
/// private to a plugin rather than from a shared contract assembly, <c>ModularRuntime.Contracts</c>,
/// or the framework. Such values keep the plugin's load context alive for as long as the other
/// side holds them. The same setting decides what happens when a value cannot be verified within
/// <see cref="RuntimeLimits.MaxValueScanReferences"/> and <see cref="RuntimeLimits.MaxValueScanDepth"/>.
/// </summary>
public enum TypedValueCheck
{
    /// <summary>
    /// No check at all, including no validation of contract signatures at registration. Only for
    /// hosts that trust every plugin's contract discipline completely; a private object that
    /// crosses is never reported and the retention it causes is invisible until unload fails.
    /// </summary>
    None = 0,

    /// <summary>
    /// Verify every value, log a warning once per offending type (or per unverifiable root type),
    /// and let the value pass. For development and migration: the retention still happens.
    /// </summary>
    Warn = 1,

    /// <summary>
    /// Verify every value and refuse offenders: <c>value_not_shared</c> for a private type,
    /// <c>value_not_verifiable</c> when the scan limits are reached (fail closed). Contract
    /// signatures are validated at registration. Default.
    /// </summary>
    Reject = 2,
}

/// <summary>Configurable limits of the extensibility platform for one runtime instance.</summary>
public sealed record RuntimeLimits
{
    public const int DefaultMaxPayloadBytes = 256 * 1024;

    public const int DefaultMaxBinaryPayloadBytes = 16 * 1024 * 1024;

    public const int DefaultMaxValueScanReferences = 4096;

    public const int DefaultMaxValueScanDepth = 64;

    /// <summary>Maximum UTF-8 size of an event payload, invocation payload, or response payload.</summary>
    public int MaxPayloadBytes { get; init; } = DefaultMaxPayloadBytes;

    /// <summary>
    /// Maximum size of a binary request or response (default 16 MiB). A binary service may lower
    /// it for itself (<see cref="Contracts.BinaryServiceDefinition.MaxRequestBytes"/>), never raise it.
    /// </summary>
    public int MaxBinaryPayloadBytes { get; init; } = DefaultMaxBinaryPayloadBytes;

    /// <summary>
    /// How long stopping an owner waits for its in-flight handlers to finish after their tokens
    /// were canceled. On timeout the stop continues and a <c>drain_timeout</c> warning is logged;
    /// handlers that ignore cancellation keep running. <see cref="Timeout.InfiniteTimeSpan"/>
    /// waits indefinitely.
    /// </summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Maximum nesting of event publications made from inside event handlers.</summary>
    public int MaxEventDispatchDepth { get; init; } = 16;

    /// <summary>Buffer capacity of a stream subscription when neither the stream nor the subscriber chooses one.</summary>
    public int DefaultStreamCapacity { get; init; } = 256;

    /// <summary>Largest buffer a stream subscription may request.</summary>
    public int MaxStreamCapacity { get; init; } = 65_536;

    /// <summary>Check applied to values crossing typed boundaries; see <see cref="Engine.TypedValueCheck"/>. Default <see cref="TypedValueCheck.Reject"/>.</summary>
    public TypedValueCheck TypedValueCheck { get; init; } = TypedValueCheck.Reject;

    /// <summary>
    /// Maximum number of references (fields, elements, entries, delegate targets) the verification
    /// of one value may examine (default 4096). A value whose type settles the question costs no
    /// scan at all; only values whose type is <c>object</c>, an interface, a non-sealed class, a
    /// delegate, or a type with such fields are scanned. Beyond the limit the value is
    /// <c>value_not_verifiable</c> (rejected or warned about according to <see cref="TypedValueCheck"/>).
    /// </summary>
    public int MaxValueScanReferences { get; init; } = DefaultMaxValueScanReferences;

    /// <summary>
    /// Maximum distance, in reference hops, from a value to the objects its verification enters
    /// (default 64): a chain of 65 linked objects is <c>value_not_verifiable</c>. The verification
    /// is iterative, so this bounds the shape of accepted values, not the stack; hosts whose
    /// contracts are flat may lower it to make nested graphs fail early.
    /// </summary>
    public int MaxValueScanDepth { get; init; } = DefaultMaxValueScanDepth;

    internal void Validate()
    {
        if (MaxPayloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPayloadBytes), "MaxPayloadBytes must be positive.");
        }

        if (MaxBinaryPayloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBinaryPayloadBytes), "MaxBinaryPayloadBytes must be positive.");
        }

        if (DrainTimeout < TimeSpan.Zero && DrainTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(DrainTimeout), "DrainTimeout must be non-negative or infinite.");
        }

        if (MaxEventDispatchDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxEventDispatchDepth), "MaxEventDispatchDepth must be positive.");
        }

        if (DefaultStreamCapacity <= 0 || MaxStreamCapacity <= 0 || DefaultStreamCapacity > MaxStreamCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(DefaultStreamCapacity), "Stream capacities must be positive and the default must not exceed the maximum.");
        }

        if (!Enum.IsDefined(TypedValueCheck))
        {
            throw new ArgumentOutOfRangeException(nameof(TypedValueCheck), "Unknown typed value check.");
        }

        if (MaxValueScanReferences <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxValueScanReferences), "MaxValueScanReferences must be positive.");
        }

        if (MaxValueScanDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxValueScanDepth), "MaxValueScanDepth must be positive.");
        }
    }
}
