using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using ModularRuntime.Engine.Internal;

namespace ModularRuntime.Engine.Services;

/// <summary>
/// Generates proxy types for typed-service contracts.
/// </summary>
/// <remarks>
/// <para>
/// A proxy type implements the contract interface (and every interface it inherits) by calling
/// the provider's instance directly through <see cref="TypedCallContext"/>: no boxing, no
/// reflection, and no allocation per call beyond what the contract itself needs. Every method is
/// wrapped in admission (<see cref="TypedCallContext.Enter"/>), cancellation linking for
/// <see cref="CancellationToken"/> parameters, exception translation, the shared-value check, and
/// for methods returning <see cref="Task"/> or <see cref="ValueTask"/> an asynchronous observer
/// that ends the admission when the provider's task completes (<see cref="InvocationEmitter"/>).
/// </para>
/// <para>
/// Proxy code is emitted into a collectible dynamic assembly created under the
/// <see cref="AssemblyLoadContext"/> of the contract assembly (<c>EnterContextualReflection</c>),
/// so a proxy for a contract in a collectible shared-contract context is collected together with
/// that context. One dynamic module per load context, one proxy type per contract, both cached
/// with weak keys. <see cref="System.Reflection.DispatchProxy"/> cannot be used: it emits into the
/// context of the proxy's base type, which lives in the engine, and a non-collectible assembly
/// may not reference a collectible one.
/// </para>
/// </remarks>
internal static class TypedProxyEmitter
{
    private static readonly ConditionalWeakTable<AssemblyLoadContext, ModuleHolder> Modules = new();
    private static readonly ConditionalWeakTable<Type, ProxyHolder> Proxies = new();
    private static readonly object Sync = new();

    private static readonly ConstructorInfo ObjectConstructor = typeof(TypedServiceProxy).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, [typeof(TypedCallContext)])!;
    private static readonly MethodInfo ContextGetter = typeof(TypedServiceProxy).GetProperty(nameof(TypedServiceProxy.Context))!.GetMethod!;

    /// <summary>
    /// Validates a contract type: a public interface, without static abstract or virtual members,
    /// whose members the proxy can implement. Returns an error message or <see langword="null"/>.
    /// </summary>
    public static string? ValidateContract(Type contract)
    {
        if (!contract.IsInterface)
        {
            return $"'{contract.FullName}' is not an interface. Typed service contracts must be public interfaces.";
        }

        if (!contract.IsVisible)
        {
            return $"'{contract.FullName}' is not public. Typed service contracts must be public interfaces (public at every nesting level).";
        }

        if (contract.ContainsGenericParameters)
        {
            return $"'{contract.FullName}' is an open generic interface; register a closed generic type.";
        }

        if (contract.IsByRefLike)
        {
            return $"'{contract.FullName}' cannot be a typed service contract.";
        }

        foreach (var type in AllInterfaces(contract))
        {
            if (!type.IsVisible)
            {
                return $"'{contract.FullName}' inherits '{type.FullName}', which is not public.";
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.IsStatic && (method.IsAbstract || method.IsVirtual))
                {
                    return $"'{type.FullName}.{method.Name}' is a static abstract or static virtual member; typed service contracts cannot have them.";
                }

                if (!method.IsStatic && !method.IsPublic && method.IsAbstract)
                {
                    return $"'{type.FullName}.{method.Name}' is not public; typed service contracts cannot have non-public abstract members.";
                }
            }
        }

        return null;
    }

    /// <summary>Every type a contract's members expose across the boundary, for validation at registration.</summary>
    public static IEnumerable<(string Member, Type Type)> SignatureTypes(Type contract)
    {
        foreach (var type in AllInterfaces(contract))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!method.IsAbstract)
                {
                    continue;
                }

                foreach (var entry in InvocationEmitter.SignatureTypes($"{type.Name}.{method.Name}", method))
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>Creates a proxy of <paramref name="contract"/> bound to <paramref name="context"/>.</summary>
    public static object CreateProxy(Type contract, TypedCallContext context)
    {
        var proxyType = GetProxyType(contract);
        return Activator.CreateInstance(proxyType, context)!;
    }

    private static Type GetProxyType(Type contract)
    {
        if (Proxies.TryGetValue(contract, out var holder))
        {
            return holder.ProxyType;
        }

        lock (Sync)
        {
            if (Proxies.TryGetValue(contract, out holder))
            {
                return holder.ProxyType;
            }

            var proxyType = Emit(contract);
            Proxies.Add(contract, new ProxyHolder(proxyType));
            return proxyType;
        }
    }

    private static ModuleBuilder GetModule(AssemblyLoadContext context)
    {
        if (Modules.TryGetValue(context, out var holder))
        {
            return holder.Module;
        }

        AssemblyBuilder assembly;
        using (context.EnterContextualReflection())
        {
            assembly = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName($"ModularRuntime.TypedServiceProxies.{Guid.NewGuid():N}"),
                AssemblyBuilderAccess.RunAndCollect);
        }

        var module = assembly.DefineDynamicModule("proxies");
        Modules.Add(context, new ModuleHolder(module));
        return module;
    }

    private static Type Emit(Type contract)
    {
        var context = DynamicCode.ContextFor(contract);
        var module = GetModule(context);
        var interfaces = AllInterfaces(contract).ToArray();
        var typeName = $"ModularRuntime.TypedServiceProxies.{SafeName(contract)}_{Guid.NewGuid():N}";
        var builder = module.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class, typeof(TypedServiceProxy), interfaces);

        var constructor = builder.DefineConstructor(MethodAttributes.Public | MethodAttributes.HideBySig, CallingConventions.Standard, [typeof(TypedCallContext)]);
        var constructorIl = constructor.GetILGenerator();
        constructorIl.Emit(OpCodes.Ldarg_0);
        constructorIl.Emit(OpCodes.Ldarg_1);
        constructorIl.Emit(OpCodes.Call, ObjectConstructor);
        constructorIl.Emit(OpCodes.Ret);

        foreach (var type in interfaces)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!method.IsAbstract)
                {
                    continue; // default interface method: inherited by the proxy, runs contract code on the caller side
                }

                EmitMethod(builder, type, method);
            }
        }

        return builder.CreateType();
    }

    private static void EmitMethod(TypeBuilder builder, Type contract, MethodInfo method)
    {
        var parameters = method.GetParameters();
        var impl = builder.DefineMethod(
            contract.FullName + "." + method.Name,
            MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
            CallingConventions.HasThis);

        // Generic methods: redefine the type parameters on the implementation and substitute them
        // in every signature type.
        var map = new Dictionary<Type, Type>();
        if (method.IsGenericMethodDefinition)
        {
            var sourceParameters = method.GetGenericArguments();
            var targetParameters = impl.DefineGenericParameters(sourceParameters.Select(p => p.Name).ToArray());
            for (var index = 0; index < sourceParameters.Length; index++)
            {
                map[sourceParameters[index]] = targetParameters[index];
            }

            for (var index = 0; index < sourceParameters.Length; index++)
            {
                var source = sourceParameters[index];
                var defined = targetParameters[index];
                defined.SetGenericParameterAttributes(source.GenericParameterAttributes);
                var interfaceConstraints = new List<Type>();
                foreach (var constraint in source.GetGenericParameterConstraints())
                {
                    var substituted = Substitute(constraint, map);
                    if (substituted.IsInterface)
                    {
                        interfaceConstraints.Add(substituted);
                    }
                    else
                    {
                        defined.SetBaseTypeConstraint(substituted);
                    }
                }

                if (interfaceConstraints.Count > 0)
                {
                    defined.SetInterfaceConstraints(interfaceConstraints.ToArray());
                }
            }
        }

        var returnType = Substitute(method.ReturnType, map);
        var parameterTypes = parameters.Select(p => Substitute(p.ParameterType, map)).ToArray();
        impl.SetSignature(returnType, null, null, parameterTypes, null, null);
        for (var index = 0; index < parameters.Length; index++)
        {
            impl.DefineParameter(index + 1, parameters[index].Attributes, parameters[index].Name);
        }

        var callee = method.IsGenericMethodDefinition
            ? method.MakeGenericMethod(method.GetGenericArguments().Select(argument => map[argument]).ToArray())
            : method;
        InvocationEmitter.EmitBody(
            impl.GetILGenerator(),
            static il =>
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, ContextGetter);
            },
            contract,
            callee,
            returnType,
            parameterTypes,
            parameters.Select(p => p.Name).ToArray());
        builder.DefineMethodOverride(impl, method);
    }

    private static Type Substitute(Type type, Dictionary<Type, Type> map)
    {
        if (map.Count == 0)
        {
            return type;
        }

        if (map.TryGetValue(type, out var mapped))
        {
            return mapped;
        }

        if (type.IsByRef)
        {
            return Substitute(type.GetElementType()!, map).MakeByRefType();
        }

        if (type.IsArray)
        {
            var element = Substitute(type.GetElementType()!, map);
            return type.GetArrayRank() == 1 && type == type.GetElementType()!.MakeArrayType() ? element.MakeArrayType() : element.MakeArrayType(type.GetArrayRank());
        }

        if (type.IsPointer)
        {
            return Substitute(type.GetElementType()!, map).MakePointerType();
        }

        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            var arguments = type.GetGenericArguments();
            var changed = false;
            for (var index = 0; index < arguments.Length; index++)
            {
                var substituted = Substitute(arguments[index], map);
                changed |= !ReferenceEquals(substituted, arguments[index]);
                arguments[index] = substituted;
            }

            return changed ? type.GetGenericTypeDefinition().MakeGenericType(arguments) : type;
        }

        return type;
    }

    private static IEnumerable<Type> AllInterfaces(Type contract)
    {
        yield return contract;
        foreach (var inherited in contract.GetInterfaces())
        {
            yield return inherited;
        }
    }

    private static string SafeName(Type type)
    {
        var name = type.FullName ?? type.Name;
        var characters = name.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            if (!char.IsAsciiLetterOrDigit(characters[index]) && characters[index] != '_')
            {
                characters[index] = '_';
            }
        }

        return new string(characters);
    }

    private sealed record ModuleHolder(ModuleBuilder Module);

    private sealed record ProxyHolder(Type ProxyType);
}
