using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Mono.Cecil;
using CecilCode = Mono.Cecil.Cil.Code;

namespace VulkanStory.Game;

public static partial class ManagedGlCallPatches
{
    private static readonly HashSet<string> NativeMemoryCommands = new(StringComparer.Ordinal)
    {
        "BufferData", "BufferSubData", "TexSubImage2D", "GetTexImage", "GetTextureSubImage", "ReadPixels",
        "Uniform1", "Uniform2", "Uniform3", "Uniform4", "UniformMatrix4",
        "GenBuffers", "GenVertexArrays", "GenTextures", "GenFramebuffers",
        "DeleteBuffers", "DeleteVertexArrays", "DeleteTextures", "DeleteFramebuffers", "DrawBuffers",
    };
    private sealed record NativePlan(FieldInfo Table, int Slot, MethodInfo Command, Type[] Parameters, MethodInfo[] GenericMethods);
    private sealed class NativeEntry(NativePlan plan, nint pointer)
    {
        internal readonly NativePlan Plan = plan;
        internal readonly nint Pointer = pointer;
        internal nint[]? Table;
        internal nint Original;
    }
    private static readonly Lazy<NativePlan[]> NativePlans = new(DiscoverNativePlans);
    private static readonly List<NativeEntry> NativeEntries = new();
    // Procedure pointers can be cached by a binding. Their delegates stay rooted
    // until process exit, including after the original table has been restored.
    private static readonly List<Delegate> NativeThunkRoots = new();
    private static bool nativeEntriesActive;
    private static long nativeFailures;
    internal static long NativeFailureCount => Interlocked.Read(ref nativeFailures);

    private static bool SignatureMatches(Type type, TypeReference reference)
    {
        if (type.IsGenericParameter) return reference is GenericParameter parameter && parameter.Type == GenericParameterType.Method && parameter.Position == type.GenericParameterPosition;
        if (type.IsArray) return reference is ArrayType array && array.Rank == type.GetArrayRank() && SignatureMatches(type.GetElementType()!, array.ElementType);
        if (type.IsByRef) return reference is ByReferenceType byRef && SignatureMatches(type.GetElementType()!, byRef.ElementType);
        if (type.IsPointer) return reference is PointerType pointer && SignatureMatches(type.GetElementType()!, pointer.ElementType);
        return type.FullName == reference.FullName.Replace('/', '+');
    }

    private static bool SupportsNativeGeneric(GenericInstanceMethod reference)
    {
        MethodReference definition = reference.ElementMethod;
        return NativePlans.Value.SelectMany(plan => plan.GenericMethods).Any(method =>
            method.DeclaringType!.FullName == definition.DeclaringType.FullName && method.Name == definition.Name &&
            method.GetGenericArguments().Length == reference.GenericArguments.Count &&
            SignatureMatches(method.ReturnType, definition.ReturnType) && method.GetParameters().Length == definition.Parameters.Count &&
            method.GetParameters().Select((parameter, index) => SignatureMatches(parameter.ParameterType, definition.Parameters[index].ParameterType)).All(match => match));
    }

    private static Type? AbiType(TypeReference type) => type.MetadataType switch
    {
        MetadataType.Void => typeof(void), MetadataType.Int32 => typeof(int), MetadataType.UInt32 => typeof(uint),
        MetadataType.Int64 => typeof(long), MetadataType.UInt64 => typeof(ulong), MetadataType.IntPtr or MetadataType.UIntPtr or MetadataType.Pointer => typeof(nint),
        MetadataType.Boolean or MetadataType.Byte => typeof(byte), MetadataType.SByte => typeof(sbyte), MetadataType.Int16 => typeof(short), MetadataType.UInt16 => typeof(ushort),
        MetadataType.Single => typeof(float), MetadataType.Double => typeof(double), _ => null,
    };

    private static (int Slot, Type[] Parameters)? NativeSignature(MethodDefinition method)
    {
        if (!method.HasBody) return null;
        var instructions = method.Body.Instructions;
        var indirect = instructions.Where(instruction => instruction.OpCode.Code == CecilCode.Calli).ToArray();
        if (indirect.Length != 1 || indirect[0].Operand is not CallSite signature || AbiType(signature.ReturnType) != typeof(void) || signature.HasThis) return null;
        Type?[] types = signature.Parameters.Select(parameter => AbiType(parameter.ParameterType)).ToArray();
        if (types.Any(type => type is null)) return null;
        for (int i = 0; i + 2 < instructions.Count; i++)
        {
            if (instructions[i].OpCode.Code != CecilCode.Ldsfld || instructions[i].Operand is not FieldReference field ||
                field.Name != "EntryPoints" || field.DeclaringType.FullName != method.DeclaringType.FullName || instructions[i + 2].OpCode.Code != CecilCode.Ldelem_I) continue;
            int slot = instructions[i + 1].OpCode.Code switch
            {
                CecilCode.Ldc_I4 => (int)instructions[i + 1].Operand,
                CecilCode.Ldc_I4_S => Convert.ToInt32(instructions[i + 1].Operand),
                CecilCode.Ldc_I4_0 => 0, CecilCode.Ldc_I4_1 => 1, CecilCode.Ldc_I4_2 => 2, CecilCode.Ldc_I4_3 => 3,
                CecilCode.Ldc_I4_4 => 4, CecilCode.Ldc_I4_5 => 5, CecilCode.Ldc_I4_6 => 6, CecilCode.Ldc_I4_7 => 7, CecilCode.Ldc_I4_8 => 8,
                _ => -1,
            };
            if (slot >= 0) return (slot, types.Select(type => type!).ToArray());
        }
        return null;
    }

    private static NativePlan[] DiscoverNativePlans()
    {
        var plans = new List<NativePlan>();
        using var image = AssemblyDefinition.ReadAssembly(typeof(OpenTK.Graphics.OpenGL4.GL).Assembly.Location, new ReaderParameters { ReadSymbols = false });
        if (image.MainModule.Mvid != typeof(OpenTK.Graphics.OpenGL4.GL).Module.ModuleVersionId)
            throw new InvalidOperationException("OpenTK procedure metadata does not match the loaded assembly.");
        foreach (Type binding in Bindings)
        {
            FieldInfo table = binding.GetField("EntryPoints", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) ?? throw new MissingFieldException(binding.FullName, "EntryPoints");
            if (table.FieldType != typeof(nint[])) throw new InvalidOperationException("OpenTK procedure table type changed.");
            var commands = new Dictionary<int, (MethodInfo Method, Type[] Parameters)>();
            foreach (MethodInfo command in SupportedMethods.Where(method => method.DeclaringType == binding && NativeMemoryCommands.Contains(method.Name) &&
                method.ReturnType == typeof(void) && method.GetParameters().Any(parameter => parameter.ParameterType.IsPointer || parameter.ParameterType == typeof(nint)) &&
                method.GetParameters().All(parameter => parameter.ParameterType.IsEnum || parameter.ParameterType.IsPrimitive || parameter.ParameterType.IsPointer || parameter.ParameterType == typeof(nint))))
            {
                var signature = NativeSignature((MethodDefinition)image.MainModule.LookupToken(command.MetadataToken));
                if (signature is { } native && command.GetParameters().Length == native.Parameters.Length)
                    commands.TryAdd(native.Slot, (command, native.Parameters));
            }
            foreach (var entry in commands)
            {
                MethodInfo[] generic = binding.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(method => method.IsGenericMethodDefinition && method.Name == entry.Value.Method.Name &&
                    NativeSignature((MethodDefinition)image.MainModule.LookupToken(method.MetadataToken)) is { } signature && signature.Slot == entry.Key && signature.Parameters.SequenceEqual(entry.Value.Parameters)).ToArray();
                plans.Add(new(table, entry.Key, entry.Value.Method, entry.Value.Parameters, generic));
            }
        }
        return plans.ToArray();
    }

    private static void InstallNativeEntries()
    {
        if (NativeEntries.Count != 0) throw new InvalidOperationException("Native GL entry routing already has an owner.");
        Interlocked.Exchange(ref nativeFailures, 0);
        foreach (NativePlan plan in NativePlans.Value)
        {
            int key = Calls.Count + 1;
            Calls.Add(key, plan.Command);
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("VulkanStory.NativeGl." + key), AssemblyBuilderAccess.Run);
            var module = assembly.DefineDynamicModule("Entry");
            var type = module.DefineType("NativeGlEntry" + key, System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Sealed, typeof(MulticastDelegate));
            type.SetCustomAttribute(new CustomAttributeBuilder(typeof(UnmanagedFunctionPointerAttribute).GetConstructor([typeof(CallingConvention)])!, [CallingConvention.Winapi]));
            type.DefineConstructor(System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.RTSpecialName | System.Reflection.MethodAttributes.HideBySig,
                CallingConventions.Standard, [typeof(object), typeof(nint)]).SetImplementationFlags(System.Reflection.MethodImplAttributes.Runtime | System.Reflection.MethodImplAttributes.Managed);
            type.DefineMethod("Invoke", System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.HideBySig | System.Reflection.MethodAttributes.NewSlot | System.Reflection.MethodAttributes.Virtual,
                typeof(void), plan.Parameters).SetImplementationFlags(System.Reflection.MethodImplAttributes.Runtime | System.Reflection.MethodImplAttributes.Managed);
            Type delegateType = type.CreateType()!;
            var target = new DynamicMethod("NativeGlDispatch" + key, typeof(void), plan.Parameters, typeof(ManagedGlCallPatches).Module, false);
            ILGenerator il = target.GetILGenerator();
            il.Emit(OpCodes.Ldc_I4, key);
            il.Emit(OpCodes.Ldc_I4, plan.Parameters.Length); il.Emit(OpCodes.Newarr, typeof(object));
            for (int i = 0; i < plan.Parameters.Length; i++)
            {
                il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Ldarg, i); il.Emit(OpCodes.Box, plan.Parameters[i]); il.Emit(OpCodes.Stelem_Ref);
            }
            il.Emit(OpCodes.Call, typeof(ManagedGlCallPatches).GetMethod(nameof(NativeDispatch))!); il.Emit(OpCodes.Ret);
            Delegate thunk = target.CreateDelegate(delegateType);
            NativeThunkRoots.Add(thunk);
            NativeEntries.Add(new(plan, Marshal.GetFunctionPointerForDelegate(thunk)));
        }
    }

    internal static void ActivateNativeEntries()
    {
        if (nativeEntriesActive) return;
        if (runtime is null) throw new InvalidOperationException("Native GL entries have no installed routing owner.");
        try
        {
            foreach (NativeEntry entry in NativeEntries)
            {
                var table = (nint[]?)entry.Plan.Table.GetValue(null) ?? throw new InvalidOperationException("OpenTK procedure table is absent.");
                if ((uint)entry.Plan.Slot >= (uint)table.Length) throw new InvalidOperationException("OpenTK procedure table length changed.");
                entry.Table = table; entry.Original = table[entry.Plan.Slot];
                table[entry.Plan.Slot] = entry.Pointer;
            }
            nativeEntriesActive = true;
        }
        catch { RestoreNativeEntries(clear: false); throw; }
    }

    private static void RestoreNativeEntries(bool clear = true)
    {
        foreach (NativeEntry entry in NativeEntries)
        {
            if (entry.Table is { } table && table[entry.Plan.Slot] == entry.Pointer) table[entry.Plan.Slot] = entry.Original;
            entry.Table = null;
        }
        nativeEntriesActive = false;
        if (clear) NativeEntries.Clear();
    }

    /// <summary>Runs a native memory command without letting managed exceptions cross the unmanaged ABI.</summary>
    /// <param name="key">Installed command signature.</param>
    /// <param name="arguments">Native values; the OpenTK wrapper keeps its source/destination pinned.</param>
    public static void NativeDispatch(int key, object?[] arguments)
    {
        ProcessRuntime? owner = runtime;
        if (owner?.HasCommitted != true) return;
        MethodInfo? command = null;
        try
        {
            if (ManagedGlBridge.Current is not { } bridge) throw new InvalidOperationException("Native GL requires a current owned context.");
            command = Calls[key];
            bridge.Dispatch(command, arguments);
        }
        catch (Exception error)
        {
            missingContextError = 0x0502;
            long count = Interlocked.Increment(ref nativeFailures);
            try
            {
                string detail = (command?.ToString() ?? "command " + key) + ": " + error;
                if (count <= 8) owner.Notify("managed-gl.native-error", detail);
                ManagedGlBridge.Current?.RecordNativeFailure(detail, count <= 8);
            }
            catch { /* No diagnostic failure can cross an unmanaged callback. */ }
        }
    }
}
