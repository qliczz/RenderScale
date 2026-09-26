using System;
using System.Runtime.InteropServices;
using TerraFX.Interop.Windows;

namespace FloppyUtils;

public static class MiscExtensions
{
    /// <summary>
    /// Throws if the HRESULT indicates failure.
    /// </summary>
    /// <param name="result"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    public static HRESULT ThrowIfFailed(this HRESULT result)
    {
        Marshal.ThrowExceptionForHR(result.Value);
        return result;
    }

    /// <summary>
    /// Gets the vtbl index for the given function name in the given type.
    /// </summary>
    /// <param name="type"></param>
    /// <param name="name"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static int GetVtblIndex(this Type type, string name)
    {
        if (type.IsAssignableTo(typeof(IUnknown.Interface)))
        {
            var methods = type.GetMethods();
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name == name)
                {
                    return i;
                }
            }

            throw new InvalidOperationException($"COM type {type.FullName} does not contain method {name}");
        }

        // TODO: Hook up to Vtbl fake RTTI
        throw new InvalidOperationException($"Type {type.FullName} does not contain method {name} with vtbl index info");
    }
}
