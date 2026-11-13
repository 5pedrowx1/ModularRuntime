using System.Reflection;
using System.Reflection.Emit;
using ModularRuntime.Engine.Internal;

namespace ModularRuntime.Engine.Services;

/// <summary>
/// Emits the body shared by typed-service proxy methods and callback wrappers: shared-value checks
/// of the arguments, admission through <see cref="TypedCallContext.Enter"/>, cancellation linking,
/// the direct call, exception translation, the result check, and for <see cref="Task"/> and
/// <see cref="ValueTask"/> results an observer that ends the admission when the task completes.
/// </summary>
/// <remarks>
/// The emitted code expects the <see cref="TypedCallContext"/> to be reachable through
/// <paramref name="loadContext"/> and the call's arguments at IL argument positions 1..n (position
/// 0 is the proxy instance or the context itself).
/// </remarks>
internal static class InvocationEmitter
{
    private static readonly MethodInfo EnterMethod = typeof(TypedCallContext).GetMethod(nameof(TypedCallContext.Enter))!;
    private static readonly MethodInfo LinkMethod = typeof(TypedCallContext).GetMethod(nameof(TypedCallContext.Link))!;
    private static readonly MethodInfo LeaveFrameMethod = typeof(TypedCallContext).GetMethod(nameof(TypedCallContext.LeaveFrame))!;
    private static readonly MethodInfo ExitMethod = typeof(TypedCallContext).GetMethod(nameof(TypedCallContext.Exit))!;
    private static readonly MethodInfo TranslateMethod = typeof(TypedCallContext).GetMethod(nameof(TypedCallContext.Translate))!;
    private static readonly MethodInfo CheckArgumentMethod = typeof(TypedCallContext).GetMethod(nameof(TypedCallContext.CheckArgument))!;
    private static readonly MethodInfo CheckResultMethod = typeof(TypedCallContext).GetMethod(nameof(TypedCallContext.CheckResult))!;
    private static readonly MethodInfo ObserveTaskMethod = typeof(TypedCallContext).GetMethods().Single(m => m.Name == nameof(TypedCallContext.ObserveTask) && !m.IsGenericMethod);
    private static readonly MethodInfo ObserveTaskOfTMethod = typeof(TypedCallContext).GetMethods().Single(m => m.Name == nameof(TypedCallContext.ObserveTask) && m.IsGenericMethod);
    private static readonly MethodInfo ObserveValueTaskMethod = typeof(TypedCallContext).GetMethods().Single(m => m.Name == nameof(TypedCallContext.ObserveValueTask) && !m.IsGenericMethod);
    private static readonly MethodInfo ObserveValueTaskOfTMethod = typeof(TypedCallContext).GetMethods().Single(m => m.Name == nameof(TypedCallContext.ObserveValueTask) && m.IsGenericMethod);
    private static readonly MethodInfo FailedTaskMethod = typeof(TypedCallContext).GetMethods().Single(m => m.Name == nameof(TypedCallContext.FailedTask) && !m.IsGenericMethod);
    private static readonly MethodInfo FailedTaskOfTMethod = typeof(TypedCallContext).GetMethods().Single(m => m.Name == nameof(TypedCallContext.FailedTask) && m.IsGenericMethod);
    private static readonly MethodInfo FailedValueTaskMethod = typeof(TypedCallContext).GetMethods().Single(m => m.Name == nameof(TypedCallContext.FailedValueTask) && !m.IsGenericMethod);
    private static readonly MethodInfo FailedValueTaskOfTMethod = typeof(TypedCallContext).GetMethods().Single(m => m.Name == nameof(TypedCallContext.FailedValueTask) && m.IsGenericMethod);

    public static void EmitBody(
        ILGenerator il,
        Action<ILGenerator> loadContext,
        Type targetType,
        MethodInfo callee,
        Type returnType,
        Type[] parameterTypes,
        string?[] parameterNames)
    {
        var kind = Classify(returnType, out var taskResultType);
        var asynchronous = kind != ReturnKind.Synchronous;
        var checkResult = ShouldCheck(kind == ReturnKind.Synchronous ? returnType : taskResultType);

        var state = il.DeclareLocal(typeof(TypedCallState));
        var target = il.DeclareLocal(targetType);
        var exception = il.DeclareLocal(typeof(Exception));
        il.Emit(OpCodes.Ldloca, state);
        il.Emit(OpCodes.Initobj, typeof(TypedCallState));

        // Shared-value checks of the arguments happen before admission. A CancellationToken is not
        // checked: the callee never receives the caller's token (EmitCall passes the token that
        // TypedCallContext.Link returns, owned by the call or by the callee's scope).
        for (var index = 0; index < parameterTypes.Length; index++)
        {
            if (parameterTypes[index].IsByRef || parameterTypes[index] == typeof(CancellationToken) || !ShouldCheck(parameterTypes[index]))
            {
                continue;
            }

            loadContext(il);
            il.Emit(OpCodes.Ldarg, index + 1);
            if (parameterTypes[index].IsValueType || parameterTypes[index].IsGenericParameter)
            {
                il.Emit(OpCodes.Box, parameterTypes[index]);
            }

            il.Emit(OpCodes.Ldstr, parameterNames[index] ?? $"arg{index}");
            il.Emit(OpCodes.Call, CheckArgumentMethod);
        }

        // target = (TTarget)Context.Enter(ref state, asynchronous)  [asynchronous: failures become faulted tasks]
        var enterFailed = asynchronous ? il.DefineLabel() : default;
        if (asynchronous)
        {
            il.BeginExceptionBlock();
        }

        loadContext(il);
        il.Emit(OpCodes.Ldloca, state);
        il.Emit(asynchronous ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Call, EnterMethod);
        il.Emit(OpCodes.Castclass, targetType);
        il.Emit(OpCodes.Stloc, target);

        if (asynchronous)
        {
            var afterEnter = il.DefineLabel();
            il.BeginCatchBlock(typeof(Contracts.TypedServiceException));
            il.Emit(OpCodes.Stloc, exception);
            il.Emit(OpCodes.Leave, enterFailed);
            il.EndExceptionBlock();
            il.Emit(OpCodes.Br, afterEnter);

            il.MarkLabel(enterFailed);
            il.Emit(OpCodes.Ldloc, exception);
            il.Emit(OpCodes.Call, FailedFor(kind, taskResultType));
            il.Emit(OpCodes.Ret);
            il.MarkLabel(afterEnter);
        }

        if (kind == ReturnKind.Synchronous)
        {
            // try { result = target.M(args) } catch (Exception e) { throw Context.Translate(e, ref state) } finally { Context.Exit(ref state) }
            var result = returnType == typeof(void) ? null : il.DeclareLocal(returnType);
            il.BeginExceptionBlock();
            EmitCall(il, callee, target, parameterTypes, state);
            if (result is not null)
            {
                if (checkResult)
                {
                    EmitCheckResult(il, loadContext, returnType);
                }

                il.Emit(OpCodes.Stloc, result);
            }

            il.BeginCatchBlock(typeof(Exception));
            il.Emit(OpCodes.Stloc, exception);
            loadContext(il);
            il.Emit(OpCodes.Ldloc, exception);
            il.Emit(OpCodes.Ldloca, state);
            il.Emit(OpCodes.Call, TranslateMethod);
            il.Emit(OpCodes.Throw);
            il.BeginFinallyBlock();
            il.Emit(OpCodes.Ldloca, state);
            il.Emit(OpCodes.Call, ExitMethod);
            il.EndExceptionBlock();
            if (result is not null)
            {
                il.Emit(OpCodes.Ldloc, result);
            }

            il.Emit(OpCodes.Ret);
        }
        else
        {
            // try { task = target.M(args) } catch (Exception e) { Context.Exit(ref state); throw Context.Translate(e, ref state) }
            // Context.LeaveFrame(ref state); return Context.Observe(task, state[, checkResult])
            var task = il.DeclareLocal(returnType);
            var afterCall = il.DefineLabel();
            il.BeginExceptionBlock();
            EmitCall(il, callee, target, parameterTypes, state);
            il.Emit(OpCodes.Stloc, task);
            il.Emit(OpCodes.Leave, afterCall);
            il.BeginCatchBlock(typeof(Exception));
            il.Emit(OpCodes.Stloc, exception);
            il.Emit(OpCodes.Ldloca, state);
            il.Emit(OpCodes.Call, ExitMethod);
            loadContext(il);
            il.Emit(OpCodes.Ldloc, exception);
            il.Emit(OpCodes.Ldloca, state);
            il.Emit(OpCodes.Call, TranslateMethod);
            il.Emit(OpCodes.Throw);
            il.EndExceptionBlock();
            il.MarkLabel(afterCall);

            il.Emit(OpCodes.Ldloca, state);
            il.Emit(OpCodes.Call, LeaveFrameMethod);

            loadContext(il);
            il.Emit(OpCodes.Ldloc, task);
            il.Emit(OpCodes.Ldloc, state);
            switch (kind)
            {
                case ReturnKind.Task:
                    il.Emit(OpCodes.Call, ObserveTaskMethod);
                    break;
                case ReturnKind.TaskOfT:
                    il.Emit(checkResult ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Call, ObserveTaskOfTMethod.MakeGenericMethod(taskResultType!));
                    break;
                case ReturnKind.ValueTask:
                    il.Emit(OpCodes.Call, ObserveValueTaskMethod);
                    break;
                default:
                    il.Emit(checkResult ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Call, ObserveValueTaskOfTMethod.MakeGenericMethod(taskResultType!));
                    break;
            }

            il.Emit(OpCodes.Ret);
        }
    }

    /// <summary>
    /// Whether values of a static type need the shared-value check: everything except types that
    /// are closed by construction regardless of the engine's policy (framework and other
    /// non-collectible types whose values cannot reference anything else). Generic parameters,
    /// interfaces, delegates, non-sealed classes, and every collectible type are always checked;
    /// the check itself is one weak-table lookup when the engine finds the runtime type closed.
    /// </summary>
    public static bool ShouldCheck(Type? type)
    {
        if (type is null || type == typeof(void) || type.IsByRef || type.IsPointer)
        {
            return false;
        }

        return SharedTypeAnalyzer.ByConstruction.GetShape(type).Static != TypeVerdict.Closed;
    }

    private static void EmitCall(ILGenerator il, MethodInfo callee, LocalBuilder target, Type[] parameterTypes, LocalBuilder state)
    {
        il.Emit(OpCodes.Ldloc, target);
        for (var index = 0; index < parameterTypes.Length; index++)
        {
            if (parameterTypes[index] == typeof(CancellationToken))
            {
                il.Emit(OpCodes.Ldloca, state);
                il.Emit(OpCodes.Ldarg, index + 1);
                il.Emit(OpCodes.Call, LinkMethod);
            }
            else
            {
                il.Emit(OpCodes.Ldarg, index + 1);
            }
        }

        il.Emit(OpCodes.Callvirt, callee);
    }

    private static void EmitCheckResult(ILGenerator il, Action<ILGenerator> loadContext, Type returnType)
    {
        // stack: result -> Context.CheckResult(object) -> back to the typed result
        var temp = il.DeclareLocal(returnType);
        il.Emit(OpCodes.Stloc, temp);
        loadContext(il);
        il.Emit(OpCodes.Ldloc, temp);
        if (returnType.IsValueType || returnType.IsGenericParameter)
        {
            il.Emit(OpCodes.Box, returnType);
        }

        il.Emit(OpCodes.Call, CheckResultMethod);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldloc, temp);
    }

    private static MethodInfo FailedFor(ReturnKind kind, Type? resultType) => kind switch
    {
        ReturnKind.Task => FailedTaskMethod,
        ReturnKind.TaskOfT => FailedTaskOfTMethod.MakeGenericMethod(resultType!),
        ReturnKind.ValueTask => FailedValueTaskMethod,
        _ => FailedValueTaskOfTMethod.MakeGenericMethod(resultType!),
    };

    private static ReturnKind Classify(Type returnType, out Type? resultType)
    {
        resultType = null;
        if (returnType == typeof(Task))
        {
            return ReturnKind.Task;
        }

        if (returnType == typeof(ValueTask))
        {
            return ReturnKind.ValueTask;
        }

        if (returnType.IsGenericType)
        {
            var definition = returnType.GetGenericTypeDefinition();
            if (definition == typeof(Task<>))
            {
                resultType = returnType.GetGenericArguments()[0];
                return ReturnKind.TaskOfT;
            }

            if (definition == typeof(ValueTask<>))
            {
                resultType = returnType.GetGenericArguments()[0];
                return ReturnKind.ValueTaskOfT;
            }
        }

        return ReturnKind.Synchronous;
    }

    /// <summary>The types a signature exposes to the check: parameters (without by-ref; a <see cref="CancellationToken"/> is replaced by the runtime), results (without the task wrapper).</summary>
    public static IEnumerable<(string Member, Type Type)> SignatureTypes(string memberName, MethodInfo method)
    {
        foreach (var parameter in method.GetParameters())
        {
            var type = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
            if (!type.IsGenericParameter && !type.ContainsGenericParameters && type != typeof(CancellationToken))
            {
                yield return ($"parameter '{parameter.Name}' of '{memberName}'", type);
            }
        }

        var returnType = method.ReturnType;
        if (Classify(returnType, out var taskResult) != ReturnKind.Synchronous)
        {
            returnType = taskResult ?? typeof(void);
        }

        if (returnType != typeof(void) && !returnType.IsGenericParameter && !returnType.ContainsGenericParameters)
        {
            yield return ($"the result of '{memberName}'", returnType);
        }
    }

    private enum ReturnKind
    {
        Synchronous,
        Task,
        TaskOfT,
        ValueTask,
        ValueTaskOfT,
    }
}
