using System.Runtime.CompilerServices;
using ModularRuntime.Contracts;
using ModularRuntime.Engine.Services;

namespace ModularRuntime.Engine.Internal;

/// <summary>Why a value was refused at a typed boundary.</summary>
internal readonly record struct ValueCheckFailure(string Code, string Message);

/// <summary>Where a value crosses, formatted only when a check fails (no allocation on the hot path).</summary>
internal readonly record struct ValueBoundary(string Label, string Kind, string? Name)
{
    public override string ToString() => Kind.Length == 0 ? Label : Name is null ? $"{Label}, {Kind}" : $"{Label}, {Kind} '{Name}'";
}

/// <summary>
/// Applies <see cref="RuntimeLimits.TypedValueCheck"/> to values crossing typed boundaries and
/// validates contract signatures at registration.
/// </summary>
/// <remarks>
/// <para>
/// A value is <em>shared</em> when its runtime type is closed (see <see cref="SharedTypeAnalyzer"/>),
/// or when the bounded inspection of an open type finds only shared references
/// (<see cref="ValueScan"/>). A value whose type is closed costs one weak-table lookup; nothing
/// is inspected. In <see cref="TypedValueCheck.Reject"/> mode offenders are refused
/// (<c>value_not_shared</c>) and so are values the inspection cannot finish within the limits
/// (<c>value_not_verifiable</c>): the runtime fails closed. In <see cref="TypedValueCheck.Warn"/>
/// mode a warning is logged once per offending type and the value passes.
/// </para>
/// <para>
/// Decisions are cached per type with weak keys, so plugin types are never kept alive by the
/// cache. The per-thread scan state is reused and cleared after every scan.
/// </para>
/// </remarks>
internal sealed class SharedTypes
{
    private static readonly object WarnedMarker = new();

    private readonly RuntimeCore _core;
    private readonly ConditionalWeakTable<Type, object> _warned = new();

    public SharedTypes(RuntimeCore core, ISharedTypePolicy policy)
    {
        _core = core;
        Analyzer = new SharedTypeAnalyzer(policy);
    }

    public SharedTypeAnalyzer Analyzer { get; }

    public TypedValueCheck Mode => _core.Limits.TypedValueCheck;

    /// <summary>True when values of the static type <paramref name="type"/> need no per-instance check.</summary>
    public bool IsClosed(Type type) => Mode == TypedValueCheck.None || Analyzer.GetShape(type).Static == TypeVerdict.Closed;

    public TypeVerdict StaticVerdict(Type type) => Analyzer.GetShape(type).Static;

    /// <summary>
    /// Checks one value. Returns <see langword="null"/> when it may pass, otherwise the failure (in
    /// <see cref="TypedValueCheck.Reject"/> mode). In <see cref="TypedValueCheck.Warn"/> mode a
    /// warning is logged once per offending type and the value passes.
    /// </summary>
    public ValueCheckFailure? Check(object? value, in ValueBoundary boundary, Func<ResourceOwner?>? owner)
    {
        if (value is null || Mode == TypedValueCheck.None)
        {
            return null;
        }

        var type = value.GetType();
        if (Analyzer.GetShape(type).Exact == TypeVerdict.Closed)
        {
            return null;
        }

        var scan = ValueScan.Rent(Analyzer, _core.Limits.MaxValueScanReferences, _core.Limits.MaxValueScanDepth);
        try
        {
            if (scan.Scan(value, boundary))
            {
                return null;
            }

            var failure = Describe(scan);
            if (Mode == TypedValueCheck.Reject)
            {
                return failure;
            }

            WarnOnce(scan.Offending ?? type, failure.Message, owner?.Invoke());
            return null;
        }
        finally
        {
            scan.Return();
        }
    }

    /// <summary>
    /// True when <paramref name="exception"/> and everything it carries (its own fields, inner
    /// exceptions, the parts of an <see cref="AggregateException"/>, the keys and values of
    /// <see cref="Exception.Data"/>) are shared, so the object can be handed to another owner
    /// without retaining a load context through it. The type itself is always checked; the
    /// content only when the check is enabled.
    /// </summary>
    public bool IsSharedException(Exception exception, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var shape = Analyzer.GetShape(exception.GetType());
        if (shape.Exact == TypeVerdict.Private)
        {
            reason = shape.Reason;
            return false;
        }

        if (Mode == TypedValueCheck.None)
        {
            reason = null;
            return true;
        }

        var scan = ValueScan.Rent(Analyzer, _core.Limits.MaxValueScanReferences, _core.Limits.MaxValueScanDepth);
        try
        {
            if (scan.Scan(exception, new ValueBoundary("the exception", string.Empty, null)))
            {
                reason = null;
                return true;
            }

            reason = Describe(scan).Message;
            return false;
        }
        finally
        {
            scan.Return();
        }
    }

    /// <summary>
    /// Validates that a contract type (interface or delegate) and the types of its signature are
    /// shared and analyzable. Returns the error in <see cref="TypedValueCheck.Reject"/> mode; logs
    /// warnings in <see cref="TypedValueCheck.Warn"/> mode; does nothing in <see cref="TypedValueCheck.None"/>.
    /// </summary>
    public string? ValidateContract(Type contract, IEnumerable<(string Member, Type Type)> signature, string what, ResourceOwner owner)
    {
        if (Mode == TypedValueCheck.None)
        {
            return null;
        }

        var contractShape = Analyzer.GetShape(contract);
        if (contractShape.Static is TypeVerdict.Private or TypeVerdict.Unverifiable && Report(
                contractShape.Static == TypeVerdict.Private
                    ? $"{what}: the contract {SharedTypeAnalyzer.Describe(contract)} is not a shared type. Declare its assembly in plugin.json ('sharedContracts') so that the runtime loads it once for every plugin that uses it; a contract private to one plugin cannot be shared with others."
                    : $"{what}: the contract {SharedTypeAnalyzer.Describe(contract)} cannot be analyzed ({contractShape.Reason}).",
                owner) is { } contractError)
        {
            return contractError;
        }

        foreach (var (member, type) in signature)
        {
            var shape = Analyzer.GetShape(type);
            if (shape.Static is TypeVerdict.Private or TypeVerdict.Unverifiable && Report(
                    shape.Static == TypeVerdict.Private
                        ? $"{what}: {member} uses {SharedTypeAnalyzer.Describe(type)}, which is not a shared type ({shape.Reason}); signatures may only use types from the contract assembly, ModularRuntime.Contracts, or the framework."
                        : $"{what}: {member} uses {SharedTypeAnalyzer.Describe(type)}, which cannot be analyzed ({shape.Reason}); use a simpler type.",
                    owner) is { } error)
            {
                return error;
            }
        }

        return null;
    }

    private string? Report(string message, ResourceOwner owner)
    {
        if (Mode == TypedValueCheck.Reject)
        {
            return message;
        }

        _core.Log(LogLevel.Warning, "Services", $"[{PluginErrorCodes.TypedServiceInvalidContract}] {message}", owner);
        return null;
    }

    private static ValueCheckFailure Describe(ValueScan scan) =>
        scan.OffendingDescription is { } offending
            ? new ValueCheckFailure(
                PluginErrorCodes.ValueNotShared,
                $"{scan.Path}: {offending} is not a shared type (contract assembly, ModularRuntime.Contracts, or framework); holding it keeps that assembly's load context alive.")
            : new ValueCheckFailure(
                PluginErrorCodes.ValueNotVerifiable,
                $"{scan.Path}: the value cannot be verified ({scan.LimitReached}). Use closed types (sealed records and structs of shared types, typed arrays and collections) so that no inspection is needed, or raise RuntimeLimits.MaxValueScanReferences / MaxValueScanDepth.");

    private void WarnOnce(Type key, string message, ResourceOwner? owner)
    {
        if (_warned.TryGetValue(key, out _))
        {
            return;
        }

        _warned.AddOrUpdate(key, WarnedMarker);
        _core.Log(LogLevel.Warning, "Services", $"[{PluginErrorCodes.ValueNotShared}] {message}", owner);
    }
}
