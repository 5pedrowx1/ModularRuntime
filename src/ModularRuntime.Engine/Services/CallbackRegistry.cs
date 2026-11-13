using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using ModularRuntime.Contracts;
using ModularRuntime.Engine.Internal;
using ModularRuntime.Engine.Ownership;

namespace ModularRuntime.Engine.Services;

/// <summary>
/// Callback registrations of one runtime: delegates a plugin registers so that other plugins can
/// invoke them through a shared wrapper that holds nothing of the owner.
/// </summary>
/// <remarks>
/// A registration is an owned resource. The wrapper delegate handed out resolves the registration
/// through this registry on every call (identifiers only, like a typed-service proxy), admits the
/// call through the owner's scope, checks arguments and results, and translates exceptions.
/// Releasing the registration (explicitly or because the owner stops) drops the target: a holder
/// of the wrapper then gets <c>callback_released</c> and retains nothing of the owner.
/// </remarks>
internal sealed class CallbackRegistry : ICallTargetResolver
{
    private readonly RuntimeCore _core;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, CallbackEntry> _byId = [];

    public CallbackRegistry(RuntimeCore core)
    {
        _core = core;
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _byId.Count;
            }
        }
    }

    public ICallbackRegistration<TDelegate> Register<TDelegate>(OwnershipScope scope, string name, TDelegate target)
        where TDelegate : Delegate
    {
        ArgumentNullException.ThrowIfNull(target);
        Validation.ValidateDisplayName(name, nameof(name));
        if (CallbackEmitter.ValidateDelegateType(typeof(TDelegate)) is { } error)
        {
            throw new PluginRuntimeException(PluginErrorCodes.TypedServiceInvalidContract, error);
        }

        if (_core.SharedTypes.ValidateContract(typeof(TDelegate), CallbackEmitter.SignatureTypes(typeof(TDelegate)), $"Callback '{name}'", scope.Owner) is { } sharedError)
        {
            throw new PluginRuntimeException(PluginErrorCodes.TypedServiceInvalidContract, sharedError);
        }

        _core.EnsureCanRegister(scope);
        var entry = new CallbackEntry<TDelegate>(this, scope, name, target);
        lock (_sync)
        {
            _byId.Add(entry.ResourceId, entry);
        }

        try
        {
            var context = new TypedCallContext(_core, this, null, name, entry.ResourceId, isCallback: true);
            entry.Callback = (TDelegate)CallbackEmitter.CreateWrapper(typeof(TDelegate), context);
        }
        catch
        {
            Remove(entry);
            entry.DropOwnerReferences();
            throw;
        }

        if (!scope.TryAdd(entry))
        {
            Remove(entry);
            entry.DropOwnerReferences();
            throw RuntimeCore.ScopeClosed(scope);
        }

        _core.Log(LogLevel.Information, "Services", $"Callback '{name}' ({typeof(TDelegate).Name}) registered.", scope.Owner);
        return entry;
    }

    ICallTarget? ICallTargetResolver.FindTarget(Guid registrationId)
    {
        lock (_sync)
        {
            return _byId.GetValueOrDefault(registrationId);
        }
    }

    internal void Remove(CallbackEntry entry)
    {
        lock (_sync)
        {
            _byId.Remove(entry.ResourceId);
        }
    }

    internal void LogRemoved(CallbackEntry entry) =>
        _core.Log(LogLevel.Information, "Services", $"Callback '{entry.Name}' released.", entry.Owner);
}

internal abstract class CallbackEntry : OwnedResource, ICallTarget
{
    private readonly CallbackRegistry _registry;
    private Delegate? _target;

    protected CallbackEntry(CallbackRegistry registry, OwnershipScope scope, string name, Delegate target)
        : base(scope, ResourceKind.Callback, name)
    {
        _registry = registry;
        _target = target;
    }

    OwnershipScope ICallTarget.Scope => Scope;

    /// <summary>The registered delegate, or <see langword="null"/> after release.</summary>
    public object? Instance => Volatile.Read(ref _target);

    public void DropOwnerReferences() => Volatile.Write(ref _target, null);

    protected override ValueTask ReleaseCoreAsync()
    {
        _registry.Remove(this);
        DropOwnerReferences();
        _registry.LogRemoved(this);
        return ValueTask.CompletedTask;
    }
}

internal sealed class CallbackEntry<TDelegate> : CallbackEntry, ICallbackRegistration<TDelegate>
    where TDelegate : Delegate
{
    public CallbackEntry(CallbackRegistry registry, OwnershipScope scope, string name, TDelegate target)
        : base(registry, scope, name, target)
    {
    }

    /// <summary>The shared wrapper; set once by the registry before the entry is handed out.</summary>
    public TDelegate Callback { get; internal set; } = null!;
}

/// <summary>
/// Generates callback wrappers: a <see cref="DynamicMethod"/> per delegate type with the
/// delegate's signature plus a leading <see cref="TypedCallContext"/> parameter, closed over the
/// context when the delegate instance is created. Its body is the typed-service invocation body
/// (<see cref="InvocationEmitter"/>) calling the registered delegate's <c>Invoke</c>.
/// </summary>
/// <remarks>
/// The method is associated with the module of the delegate type when that type is collectible
/// (a contract delegate, or a framework delegate instantiated over contract types), so it is
/// collected with the contract; otherwise with the engine's module. The wrapper delegate's
/// target is the context (an engine object) and its method has no declaring type, so the
/// wrapper itself is a shared value.
/// </remarks>
internal static class CallbackEmitter
{
    private static readonly ConditionalWeakTable<Type, MethodHolder> Methods = new();
    private static readonly object Sync = new();

    public static string? ValidateDelegateType(Type delegateType)
    {
        if (!typeof(Delegate).IsAssignableFrom(delegateType) || delegateType == typeof(Delegate) || delegateType == typeof(MulticastDelegate))
        {
            return $"'{delegateType.FullName}' is not a delegate type.";
        }

        if (delegateType.ContainsGenericParameters)
        {
            return $"'{delegateType.FullName}' is an open generic delegate type; register a closed type.";
        }

        if (!delegateType.IsVisible)
        {
            return $"'{delegateType.FullName}' is not public. Callback delegate types must be public (public at every nesting level).";
        }

        return delegateType.GetMethod("Invoke") is null ? $"'{delegateType.FullName}' has no Invoke method." : null;
    }

    public static IEnumerable<(string Member, Type Type)> SignatureTypes(Type delegateType) =>
        InvocationEmitter.SignatureTypes(delegateType.Name, delegateType.GetMethod("Invoke")!);

    public static Delegate CreateWrapper(Type delegateType, TypedCallContext context)
    {
        var method = GetMethod(delegateType);
        return method.CreateDelegate(delegateType, context);
    }

    private static DynamicMethod GetMethod(Type delegateType)
    {
        if (Methods.TryGetValue(delegateType, out var holder))
        {
            return holder.Method;
        }

        lock (Sync)
        {
            if (Methods.TryGetValue(delegateType, out holder))
            {
                return holder.Method;
            }

            var method = Emit(delegateType);
            Methods.Add(delegateType, new MethodHolder(method));
            return method;
        }
    }

    private static DynamicMethod Emit(Type delegateType)
    {
        var invoke = delegateType.GetMethod("Invoke")!;
        var parameters = invoke.GetParameters();
        var parameterTypes = parameters.Select(p => p.ParameterType).ToArray();
        var method = DynamicCode.Define(
            $"ModularRuntime.Callback.{delegateType.Name}",
            invoke.ReturnType,
            [typeof(TypedCallContext), .. parameterTypes],
            DynamicCode.ModuleFor(delegateType));
        InvocationEmitter.EmitBody(
            method.GetILGenerator(),
            static il => il.Emit(OpCodes.Ldarg_0),
            delegateType,
            invoke,
            invoke.ReturnType,
            parameterTypes,
            parameters.Select(p => p.Name).ToArray());
        return method;
    }

    private sealed record MethodHolder(DynamicMethod Method);
}
