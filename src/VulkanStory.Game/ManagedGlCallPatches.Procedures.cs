using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using AssemblyDefinition = Mono.Cecil.AssemblyDefinition;
using ReaderParameters = Mono.Cecil.ReaderParameters;
using MethodDefinition = Mono.Cecil.MethodDefinition;

namespace VulkanStory.Game;

public static partial class ManagedGlCallPatches
{
    private static readonly Dictionary<string, nint> NativeProcedures = new(StringComparer.Ordinal);
    private static readonly Dictionary<int, Type> ProcedureReturnTypes = new();
    private static readonly Dictionary<string, nint> NativeStrings = new(StringComparer.Ordinal);

    internal static nint ProcedureAddress(string name) =>
        NativeProcedures.TryGetValue(name, out nint address) ? address : 0;

    private static void InstallNativeProcedures()
    {
        using var image = AssemblyDefinition.ReadAssembly(typeof(OpenTK.Graphics.OpenGL4.GL).Assembly.Location,
            new ReaderParameters { ReadSymbols = false });
        if (image.MainModule.Mvid != typeof(OpenTK.Graphics.OpenGL4.GL).Module.ModuleVersionId)
            throw new InvalidOperationException("OpenTK native procedure metadata does not match the loaded assembly.");

        foreach (Type binding in Bindings)
        {
            string[] names = (string[])(binding.GetField("EntryPointNames", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null)
                ?? throw new MissingFieldException(binding.FullName, "EntryPointNames"));
            foreach (MethodInfo method in SupportedMethods.Where(method => method.DeclaringType == binding &&
                method.GetParameters().All(parameter => parameter.ParameterType.IsPrimitive || parameter.ParameterType.IsEnum ||
                    parameter.ParameterType.IsPointer || parameter.ParameterType == typeof(nint) || parameter.ParameterType == typeof(string))))
            {
                var shape = RawNativeSignature((MethodDefinition)image.MainModule.LookupToken(method.MetadataToken));
                if (shape is not { } native || native.Parameters.Length != method.GetParameters().Length ||
                    (uint)native.Slot >= names.Length || NativeProcedures.ContainsKey(names[native.Slot])) continue;
                // String arguments arrive as borrowed UTF-8 pointers. All other
                // parameters must keep the exact widths of OpenTK's own calli.
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Where((parameter, index) => parameter.ParameterType == typeof(string) && native.Parameters[index] != typeof(nint)).Any()) continue;
                if (method.ReturnType == typeof(string) && native.ReturnType != typeof(nint)) continue;

                int key = Calls.Count + 1;
                Calls.Add(key, method);
                ProcedureReturnTypes.Add(key, native.ReturnType);
                NativeProcedures.Add(names[native.Slot], EmitNativeProcedure(key, native.ReturnType, native.Parameters));
            }
        }
    }

    private static nint EmitNativeProcedure(int key, Type returns, Type[] parameters)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("VulkanStory.GlProcedure." + key), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("Entry");
        var type = module.DefineType("GlProcedure" + key, TypeAttributes.Public | TypeAttributes.Sealed, typeof(MulticastDelegate));
        type.SetCustomAttribute(new CustomAttributeBuilder(typeof(UnmanagedFunctionPointerAttribute).GetConstructor([typeof(CallingConvention)])!, [CallingConvention.Winapi]));
        type.DefineConstructor(MethodAttributes.Public | MethodAttributes.RTSpecialName | MethodAttributes.HideBySig,
            CallingConventions.Standard, [typeof(object), typeof(nint)]).SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
        type.DefineMethod("Invoke", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            returns, parameters).SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
        Type delegateType = type.CreateType()!;
        var entry = new DynamicMethod("DispatchGlProcedure" + key, returns, parameters, typeof(ManagedGlCallPatches).Module, false);
        ILGenerator il = entry.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4, key);
        il.Emit(OpCodes.Ldc_I4, parameters.Length); il.Emit(OpCodes.Newarr, typeof(object));
        for (int i = 0; i < parameters.Length; i++)
        {
            il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Ldarg, i);
            il.Emit(OpCodes.Box, parameters[i]); il.Emit(OpCodes.Stelem_Ref);
        }
        il.Emit(OpCodes.Call, typeof(ManagedGlCallPatches).GetMethod(nameof(NativeProcedureDispatch))!);
        if (returns == typeof(void)) il.Emit(OpCodes.Pop); else il.Emit(OpCodes.Unbox_Any, returns);
        il.Emit(OpCodes.Ret);
        Delegate thunk = entry.CreateDelegate(delegateType);
        NativeThunkRoots.Add(thunk); // Native callers may retain this pointer until process exit.
        return Marshal.GetFunctionPointerForDelegate(thunk);
    }

    /// <summary>Dispatches a native GL ABI entry without allowing exceptions to cross its boundary.</summary>
    public static object? NativeProcedureDispatch(int key, object?[] arguments)
    {
        Type returns = ProcedureReturnTypes[key];
        object? zero = returns == typeof(void) ? null : Activator.CreateInstance(returns);
        ProcessRuntime? owner = runtime;
        if (owner?.HasCommitted != true) return zero;
        try
        {
            MethodInfo command = Calls[key];
            ParameterInfo[] parameters = command.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
                if (parameters[i].ParameterType == typeof(string))
                    arguments[i] = Marshal.PtrToStringUTF8((nint)arguments[i]!) ?? "";
            Route(key, arguments, out object? result);
            if (returns == typeof(void) || result is null) return zero;
            if (result is string text)
            {
                lock (NativeStrings)
                {
                    if (!NativeStrings.TryGetValue(text, out nint pointer))
                        NativeStrings.Add(text, pointer = Marshal.StringToCoTaskMemUTF8(text));
                    return pointer;
                }
            }
            if (returns == typeof(nint)) return result is nint pointer ? pointer : new nint(Convert.ToInt64(result));
            return Convert.ChangeType(result, returns, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception error)
        {
            missingContextError = 0x0502;
            long count = Interlocked.Increment(ref nativeFailures);
            try
            {
                string detail = Calls[key] + ": " + error;
                if (count <= 8) owner.Notify("native-gl.procedure-error", detail);
                ManagedGlBridge.Current?.RecordNativeFailure(detail, count <= 8);
            }
            catch { /* Native callbacks cannot propagate diagnostic failures. */ }
            return zero;
        }
    }
}
