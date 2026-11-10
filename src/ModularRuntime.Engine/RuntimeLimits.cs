namespace ModularRuntime.Engine;

/// <summary>Configurable limits of the extensibility platform for one runtime instance.</summary>
public sealed record RuntimeLimits
{
    public const int DefaultMaxPayloadBytes = 256 * 1024;

    /// <summary>Maximum UTF-8 size of an event payload, invocation payload, or response payload.</summary>
    public int MaxPayloadBytes { get; init; } = DefaultMaxPayloadBytes;

    /// <summary>
    /// How long stopping an owner waits for its in-flight handlers to finish after their tokens
    /// were canceled. On timeout the stop continues and a <c>drain_timeout</c> warning is logged;
    /// handlers that ignore cancellation keep running. <see cref="Timeout.InfiniteTimeSpan"/>
    /// waits indefinitely.
    /// </summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Maximum nesting of event publications made from inside event handlers.</summary>
    public int MaxEventDispatchDepth { get; init; } = 16;

    internal void Validate()
    {
        if (MaxPayloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPayloadBytes), "MaxPayloadBytes must be positive.");
        }

        if (DrainTimeout < TimeSpan.Zero && DrainTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(DrainTimeout), "DrainTimeout must be non-negative or infinite.");
        }

        if (MaxEventDispatchDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxEventDispatchDepth), "MaxEventDispatchDepth must be positive.");
        }
    }
}
