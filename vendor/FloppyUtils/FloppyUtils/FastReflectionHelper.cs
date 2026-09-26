using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace FloppyUtils;

public delegate object? DynamicMethodDelegate(object? target, params object?[] args);

public static class FastReflectionHelper
{
    private static readonly Type[] _dynamicMethodDelegateArgs = [typeof(object), typeof(object?[])];

    /// <summary>
    /// Creates a new dynamic method delegate for fast calls into private property getters.
    /// </summary>
    /// <typeparam name="TInstance"></typeparam>
    /// <typeparam name="TResult"></typeparam>
    /// <param name="property"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static Func<TInstance, TResult> CreateGetterDelegate<TInstance, TResult>(this PropertyInfo property)
    {
        if (property.GetGetMethod(true) is not { IsStatic: false } getter)
        {
            throw new InvalidOperationException($"Property {property.Name} in {property.DeclaringType!.FullName} doesn't have an instance getter");
        }

        var dm = new DynamicMethod($"FastReflection<{property.DeclaringType!.FullName}::{property.Name}>", typeof(TResult), [typeof(TInstance)]);
        var il = dm.GetILGenerator();

        il.Emit(OpCodes.Ldarg_0);

        if (property.DeclaringType!.IsValueType && property.DeclaringType != typeof(TInstance))
        {
            il.Emit(OpCodes.Unbox_Any, property.DeclaringType);
        }

        if (getter.IsFinal || !getter.IsVirtual)
        {
            il.Emit(OpCodes.Call, getter);
        }
        else
        {
            il.Emit(OpCodes.Callvirt, getter);
        }

        if (property.PropertyType.IsValueType && property.PropertyType != typeof(TResult))
        {
            il.Emit(OpCodes.Box, property.PropertyType);
        }

        il.Emit(OpCodes.Ret);

        return dm.CreateDelegate<Func<TInstance, TResult>>();
    }

    /// <summary>
    /// Creates a new dynamic delegate for faster-than-Invoke calls of any method.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="method"></param>
    /// <returns></returns>
    public static T CreateDynamicDelegate<T>(this MethodBase method) where T : Delegate
    {
        List<Type> methodArgs = [];
        if (!method.IsStatic)
        {
            methodArgs.Add(method.DeclaringType!);
        }
        methodArgs.AddRange(method.GetParameters().Select(static p => p.ParameterType));

        var delegateInvoke = typeof(T).GetMethod("Invoke")!;
        Type[] delegateArgs = [.. delegateInvoke.GetParameters().Select(static p => p.ParameterType)];

        if (methodArgs.Count != delegateArgs.Length)
        {
            throw new InvalidOperationException("Number of arguments must match between target method and delegate signature");
        }

        var dm = new DynamicMethod(
            $"FastReflectionHelper.CreateDynamicDelegate<{method}>",
            delegateInvoke.ReturnType,
            delegateArgs
        );
        var il = dm.GetILGenerator();

        for (var i = 0; i < methodArgs.Count; i++)
        {
            var methodArg = methodArgs[i];
            var delegateArg = delegateArgs[i];

            il.Emit(OpCodes.Ldarg, i);

            if (methodArg.IsValueType && !delegateArg.IsValueType)
            {
                il.Emit(OpCodes.Unbox, methodArg);
            }
        }

        if (method.IsConstructor)
        {
            il.Emit(OpCodes.Newobj, (ConstructorInfo) method);
        }
        else if (method.IsFinal || !method.IsVirtual)
        {
            il.Emit(OpCodes.Call, (MethodInfo) method);
        }
        else
        {
            il.Emit(OpCodes.Callvirt, (MethodInfo) method);
        }

        var methodRet = method.IsConstructor ? method.DeclaringType! : ((MethodInfo) method).ReturnType;

        if (methodRet != typeof(void))
        {
            if (delegateInvoke.ReturnType == typeof(void))
            {
                il.Emit(OpCodes.Pop);
            }
            else if (methodRet.IsValueType && !delegateInvoke.ReturnType.IsValueType)
            {
                il.Emit(OpCodes.Box, methodRet);
            }
        }

        il.Emit(OpCodes.Ret);

        return dm.CreateDelegate<T>();
    }

    /// <summary>
    /// Creates a new dynamic method delegate for faster-than-Invoke calls of any method.
    /// </summary>
    /// <param name="method"></param>
    /// <param name="directBoxValueAccess"></param>
    /// <returns></returns>
    [Obsolete("Use CreateDynamicDelegate instead.")]
    public static DynamicMethodDelegate CreateDynamicMethodDelegate(this MethodBase method, bool directBoxValueAccess = true)
    {
        var dm = new DynamicMethod($"FastReflectionHelper.CreateDynamicMethodDelegate<{method}>", typeof(object), _dynamicMethodDelegateArgs);
        var il = dm.GetILGenerator();

        var args = method.GetParameters();

        var generateLocalBoxValuePtr = true;

        if (!method.IsStatic)
        {
            il.Emit(OpCodes.Ldarg_0);

            if (method.DeclaringType?.IsValueType ?? false)
            {
                il.Emit(OpCodes.Unbox_Any, method.DeclaringType);
            }
        }

        for (var i = 0; i < args.Length; i++)
        {
            var argType = args[i].ParameterType;
            var argIsByRef = argType.IsByRef;

            if (argIsByRef)
            {
                argType = argType.GetElementType()!;
            }

            var argIsValueType = argType.IsValueType;

            if (argIsByRef && argIsValueType && !directBoxValueAccess)
            {
                // Used later when storing back the reference to the new box in the array.
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4, i);
            }

            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldc_I4, i);

            if (argIsByRef && !argIsValueType)
            {
                il.Emit(OpCodes.Ldelema, typeof(object));
            }
            else
            {
                il.Emit(OpCodes.Ldelem_Ref);

                if (argIsValueType)
                {
                    if (!argIsByRef || !directBoxValueAccess)
                    {
                        // if !directBoxValueAccess, create a new box if required
                        il.Emit(OpCodes.Unbox_Any, argType);

                        if (argIsByRef)
                        {
                            // box back
                            il.Emit(OpCodes.Box, argType);

                            // store new box value address to local 0
                            il.Emit(OpCodes.Dup);
                            il.Emit(OpCodes.Unbox, argType);

                            if (generateLocalBoxValuePtr)
                            {
                                generateLocalBoxValuePtr = false;
                                il.DeclareLocal(typeof(void*));
                            }

                            il.Emit(OpCodes.Stloc_0);

                            // arr and index set up already
                            il.Emit(OpCodes.Stelem_Ref);

                            // load address back to stack
                            il.Emit(OpCodes.Ldloc_0);
                        }
                    }
                    else
                    {
                        // if directBoxValueAccess, emit unbox (get value address)
                        il.Emit(OpCodes.Unbox, argType);
                    }
                }
            }
        }

        if (method.IsConstructor)
        {
            il.Emit(OpCodes.Newobj, (ConstructorInfo) method);
        }
        else if (method.IsFinal || !method.IsVirtual)
        {
            il.Emit(OpCodes.Call, (MethodInfo) method);
        }
        else
        {
            il.Emit(OpCodes.Callvirt, (MethodInfo) method);
        }

        var returnType = method.IsConstructor ? method.DeclaringType! : ((MethodInfo) method).ReturnType;

        if (returnType != typeof(void))
        {
            if (returnType.IsValueType)
            {
                il.Emit(OpCodes.Box, returnType);
            }
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
        }

        il.Emit(OpCodes.Ret);

        // If you were wondering how ancient this is: This right here is a good indicator.
        return (DynamicMethodDelegate) dm.CreateDelegate(typeof(DynamicMethodDelegate));
    }
}
