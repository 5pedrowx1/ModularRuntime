using System.ComponentModel;
using System.Runtime.ExceptionServices;
using ModularRuntime.Contracts;
using ModularRuntime.Engine.Internal;
using ModularRuntime.Engine.Ownership;

namespace ModularRuntime.Engine.Services;

/// <summary>Per-call state of a typed-service invocation. Infrastructure for generated proxies.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public struct TypedCallState
{
    internal OwnershipScope? Scope;
    internal CancellationToken ScopeToken;
    internal CancellationToken CallerToken;
    internal CancellationTokenSource? Linked;
    internal CancellationTokenRegistration CallerLink;
    internal CancellationTokenRegistration ScopeLink;
    internal InvocationFrames.Frame? PreviousFrame;
    internal bool Asynchronous;
    internal bool FrameActive;
}

/// <summary>
/// Base class of every generated typed-service proxy. Infrastructure for generated code.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class TypedServiceProxy
{
    protected TypedServiceProxy(TypedCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
    }

    /// <summary>The runtime-side context the generated methods call into.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public TypedCallContext Context { get; }

    public override string ToString() => $"proxy of typed service '{Context.ServiceId}'";
}

/// <summary>The object a typed call is dispatched to: a typed-service registration or a callback registration.</summary>
internal interface ICallTarget
{
    OwnershipScope Scope { get; }

    /// <summary>The provider's object or the registered delegate, or <see langword="null"/> after release.</summary>
    object? Instance { get; }

    bool IsReleased { get; }

    bool IsActive { get; }

    ResourceOwner Owner { get; }
}

internal interface ICallTargetResolver
{
    ICallTarget? FindTarget(Guid registrationId);
}

/// <summary>
/// Runtime side of one typed-service client or callback delegate: resolves the registration on
/// every call, admits the call through the callee's scope, links cancellation, tracks reentrancy
/// frames, translates exceptions, and applies the shared-value check. Infrastructure for
/// generated proxies and callback wrappers.
/// </summary>
/// <remarks>
/// Holds only identifiers and runtime objects: never the provider's instance or the registered
/// delegate, so a proxy or callback kept by another owner after the registration was released
/// does not keep the owner's load context alive.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class TypedCallContext
{
    private readonly ICallTargetResolver _resolver;
    private readonly ServiceRegistry? _registry;
    private readonly OwnershipScope? _caller;
    private readonly RuntimeCore _core;
    private readonly bool _isCallback;
    private readonly string _label;
    private readonly Func<ResourceOwner?> _calleeOwner;
    private readonly Func<ResourceOwner?> _callerOwner;

    internal TypedCallContext(RuntimeCore core, ICallTargetResolver resolver, OwnershipScope? caller, string serviceId, Guid registrationId, bool isCallback)
    {
        _core = core;
        _resolver = resolver;
        _registry = resolver as ServiceRegistry;
        _caller = caller;
        _isCallback = isCallback;
        ServiceId = serviceId;
        RegistrationId = registrationId;
        _label = isCallback ? $"Callback '{serviceId}'" : $"Typed service '{serviceId}'";
        _calleeOwner = () => _resolver.FindTarget(RegistrationId)?.Owner;
        _callerOwner = caller is null ? _calleeOwner : () => caller.Owner;
    }

    /// <summary>The service ID, or the callback's name for a callback.</summary>
    public string ServiceId { get; }

    public Guid RegistrationId { get; }

    internal bool IsAvailable => _resolver.FindTarget(RegistrationId) is { IsActive: true };

    internal PluginServiceDescriptor? GetDescriptor() => _registry?.FindRegistration(RegistrationId)?.Descriptor;

    private string Label => _label;

    /// <summary>Admits one call and returns the callee's object. Throws <see cref="TypedServiceException"/> when the call cannot proceed.</summary>
    public object Enter(ref TypedCallState state, bool asynchronous)
    {
        if (_core.IsDisposed)
        {
            throw Fail(PluginErrorCodes.EngineDisposed, "The runtime has been disposed.");
        }

        if (_caller is not null && !_caller.CanCallOut)
        {
            throw Fail(PluginErrorCodes.ScopeClosed, $"Owner '{_caller.Owner}' has stopped and cannot invoke services.");
        }

        if (_resolver.FindTarget(RegistrationId) is not { } target)
        {
            throw Fail(
                _isCallback ? PluginErrorCodes.CallbackReleased : PluginErrorCodes.ServiceRemoved,
                _isCallback ? $"Callback '{ServiceId}' was released by its owner." : $"The registration of typed service '{ServiceId}' bound to this proxy was removed.");
        }

        if (!target.Scope.TryEnterInvocation(out var scopeToken))
        {
            throw Fail(
                _isCallback ? PluginErrorCodes.CallbackUnavailable : PluginErrorCodes.ServiceUnavailable,
                $"{Label} is unavailable because its owner is shutting down.");
        }

        var instance = target.Instance;
        if (instance is null || target.IsReleased)
        {
            target.Scope.ExitInvocation();
            throw Fail(_isCallback ? PluginErrorCodes.CallbackReleased : PluginErrorCodes.ServiceRemoved, $"{Label} was removed.");
        }

        state.Scope = target.Scope;
        state.ScopeToken = scopeToken;
        state.Asynchronous = asynchronous;
        if (asynchronous)
        {
            state.PreviousFrame = InvocationFrames.EnterFlowing(_core.RuntimeId, target.Scope.OwnerKey);
        }
        else
        {
            InvocationFrames.PushSynchronous(_core.RuntimeId, target.Scope.OwnerKey);
        }

        state.FrameActive = true;
        return instance;
    }

    private static readonly Action<object?> CancelLinked = static linked => ((CancellationTokenSource)linked!).Cancel(throwOnFirstException: false);

    /// <summary>
    /// Returns the token to pass to the callee for a <see cref="CancellationToken"/> parameter:
    /// the callee's scope token when the caller's token cannot be canceled, otherwise a token owned
    /// by the call, canceled when the caller's token or the callee's scope is canceled.
    /// </summary>
    /// <remarks>
    /// The callee never receives the caller's token, so the proxy does not inspect it. The two
    /// registrations that link the call's source live in the call state and are disposed by
    /// <see cref="Exit"/>, so a callee that keeps the token keeps only a disposed source that
    /// references nothing of the caller. (A framework linked source keeps its registrations after
    /// disposal, and through them the caller's source and every callback registered on it.)
    /// </remarks>
    public static CancellationToken Link(ref TypedCallState state, CancellationToken callerToken)
    {
        state.CallerToken = callerToken;
        if (!callerToken.CanBeCanceled)
        {
            return state.ScopeToken;
        }

        var linked = new CancellationTokenSource();
        state.Linked = linked;
        state.CallerLink = callerToken.UnsafeRegister(CancelLinked, linked);
        if (state.ScopeToken.CanBeCanceled)
        {
            state.ScopeLink = state.ScopeToken.UnsafeRegister(CancelLinked, linked);
        }

        return linked.Token;
    }

    /// <summary>Pops the reentrancy frame; called when the callee's method returned (synchronously).</summary>
    public static void LeaveFrame(ref TypedCallState state)
    {
        if (!state.FrameActive)
        {
            return;
        }

        state.FrameActive = false;
        if (state.Asynchronous)
        {
            InvocationFrames.Restore(state.PreviousFrame);
            state.PreviousFrame = null;
        }
        else
        {
            InvocationFrames.PopSynchronous();
        }
    }

    /// <summary>Ends the admission of a call (synchronous completion or asynchronous observation finished).</summary>
    public static void Exit(ref TypedCallState state)
    {
        LeaveFrame(ref state);
        if (state.Linked is { } linked)
        {
            state.CallerLink.Dispose();
            state.ScopeLink.Dispose();
            state.CallerLink = default;
            state.ScopeLink = default;
            linked.Dispose();
            state.Linked = null;
        }

        state.Scope?.ExitInvocation();
        state.Scope = null;
    }

    /// <summary>
    /// Classifies an exception thrown by the callee: shared exceptions and caller cancellations
    /// are rethrown with their stack preserved (this method does not return); exceptions that are
    /// private, or carry private parts, are converted to a <see cref="TypedServiceException"/>,
    /// which is returned for the caller to throw.
    /// </summary>
    public Exception Translate(Exception exception, ref TypedCallState state)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (ExceptionClassifier.IsFatal(exception))
        {
            ExceptionDispatchInfo.Throw(exception);
        }

        if (exception is OperationCanceledException && state.ScopeToken.IsCancellationRequested && !state.CallerToken.IsCancellationRequested)
        {
            return Fail(PluginErrorCodes.OperationCanceled, $"The call to {Label.ToLowerInvariant()} was canceled because its owner is shutting down.");
        }

        string? reason = null;
        if (exception is TypedServiceException or OperationCanceledException || _core.SharedTypes.IsSharedException(exception, out reason))
        {
            ExceptionDispatchInfo.Throw(exception);
        }

        var diagnostic = PluginDiagnostic.FromException(exception);
        var carried = _core.SharedTypes.StaticVerdict(exception.GetType()) == TypeVerdict.Private ? string.Empty : $" (it carries a reference that is not shared: {reason})";
        var message = $"{Label} threw {diagnostic.ExceptionType}: {diagnostic.Message}{carried}";
        _core.Log(LogLevel.Warning, "Services", message, _resolver.FindTarget(RegistrationId)?.Owner, diagnostic);
        return Fail(PluginErrorCodes.HandlerFailed, message, diagnostic);
    }

    /// <summary>Applies the shared-value check to an argument before the call.</summary>
    public void CheckArgument(object? value, string parameterName)
    {
        if (_core.SharedTypes.Check(value, new ValueBoundary(_label, "argument", parameterName), _callerOwner) is { } failure)
        {
            throw Fail(failure.Code, failure.Message);
        }
    }

    /// <summary>Applies the shared-value check to a result after the call and returns it.</summary>
    public object? CheckResult(object? value)
    {
        if (_core.SharedTypes.Check(value, new ValueBoundary(_label, "result", null), _calleeOwner) is { } failure)
        {
            throw Fail(failure.Code, failure.Message);
        }

        return value;
    }

    public static Task FailedTask(Exception exception) => Task.FromException(exception);

    public static Task<T> FailedTask<T>(Exception exception) => Task.FromException<T>(exception);

    public static ValueTask FailedValueTask(Exception exception) => ValueTask.FromException(exception);

    public static ValueTask<T> FailedValueTask<T>(Exception exception) => ValueTask.FromException<T>(exception);

    /// <summary>Completes the admission of an asynchronous call when the callee's task completes.</summary>
    public async Task ObserveTask(Task task, TypedCallState state)
    {
        ArgumentNullException.ThrowIfNull(task);
        try
        {
            if (task.IsCompleted)
            {
                task.GetAwaiter().GetResult();
            }
            else
            {
                await task.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            }
        }
        catch (Exception exception) when (!ExceptionClassifier.IsFatal(exception))
        {
            throw Translate(exception, ref state);
        }
        finally
        {
            Exit(ref state);
        }
    }

    public async Task<T> ObserveTask<T>(Task<T> task, TypedCallState state, bool checkResult)
    {
        ArgumentNullException.ThrowIfNull(task);
        try
        {
            T result;
            if (task.IsCompleted)
            {
                result = task.GetAwaiter().GetResult();
            }
            else
            {
                result = await task.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            }

            if (checkResult)
            {
                CheckResult(result);
            }

            return result;
        }
        catch (Exception exception) when (!ExceptionClassifier.IsFatal(exception))
        {
            throw Translate(exception, ref state);
        }
        finally
        {
            Exit(ref state);
        }
    }

    public ValueTask ObserveValueTask(ValueTask task, TypedCallState state)
    {
        if (task.IsCompletedSuccessfully)
        {
            Exit(ref state);
            return ValueTask.CompletedTask;
        }

        return new ValueTask(ObserveTask(task.AsTask(), state));
    }

    public ValueTask<T> ObserveValueTask<T>(ValueTask<T> task, TypedCallState state, bool checkResult)
    {
        if (task.IsCompletedSuccessfully)
        {
            var result = task.Result;
            try
            {
                if (checkResult)
                {
                    CheckResult(result);
                }
            }
            catch (TypedServiceException exception)
            {
                Exit(ref state);
                return ValueTask.FromException<T>(exception);
            }

            Exit(ref state);
            return new ValueTask<T>(result);
        }

        return new ValueTask<T>(ObserveTask(task.AsTask(), state, checkResult));
    }

    private TypedServiceException Fail(string code, string message, PluginDiagnostic? diagnostic = null) =>
        _isCallback ? new TypedServiceException(code, message, diagnostic) : new TypedServiceException(code, message, ServiceId, diagnostic);
}
