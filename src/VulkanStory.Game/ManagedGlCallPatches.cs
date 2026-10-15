using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Mono.Cecil;
using OpenTK.Graphics.OpenGL4;

namespace VulkanStory.Game;

/// <summary>Installs exact managed OpenTK command entry points for owned Vulkan contexts.</summary>
public static class ManagedGlCallPatches
{
    private const string Owner = "vulkanstory.routing.managed-gl";
    private static ProcessRuntime? runtime;
    [ThreadStatic] private static bool missingContextObserved;
    [ThreadStatic] private static int missingContextError;
    private static readonly Dictionary<int, MethodInfo> Calls = new();
    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        "GetInteger", "GetBoolean", "GetError", "GenVertexArray", "GenVertexArrays", "BindVertexArray", "DeleteVertexArray", "DeleteVertexArrays",
        "GenBuffer", "GenBuffers", "BindBuffer", "DeleteBuffer", "DeleteBuffers", "BufferData", "BufferSubData", "VertexAttribPointer", "VertexAttribIPointer",
        "EnableVertexAttribArray", "DisableVertexAttribArray", "DrawElements", "DrawElementsBaseVertex", "ActiveTexture", "GenTexture", "GenTextures",
        "BindTexture", "DeleteTexture", "DeleteTextures", "TexStorage2D", "TexSubImage2D", "GenerateMipmap", "TexParameter",
        "CreateProgram", "AttachShader", "LinkProgram", "GetProgram", "GetProgramInfoLog", "DetachShader", "DeleteShader", "DeleteProgram", "CreateShader",
        "ShaderSource", "CompileShader", "GetShader", "GetShaderInfoLog", "UseProgram", "UniformMatrix4", "Uniform1", "GetUniformLocation", "GetAttribLocation", "BindAttribLocation",
        "Enable", "Disable", "IsEnabled", "BlendEquation", "BlendEquationSeparate", "BlendFunc", "BlendFuncSeparate", "PolygonMode", "Scissor", "Viewport", "Clear", "ClearColor",
    };
    private static readonly List<Type> PrefixOwners = new();
    private static readonly MethodInfo[] SupportedMethods = typeof(GL).GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(method => Names.Contains(method.Name) && !method.IsGenericMethodDefinition && !method.ContainsGenericParameters &&
            method.GetMethodBody() is not null && SupportedSignature(method)).OrderBy(method => method.MetadataToken).ToArray();

    internal static bool SupportsManagedCall(MethodReference reference)
    {
        if (reference.DeclaringType.FullName != typeof(GL).FullName || reference.HasGenericParameters || reference is GenericInstanceMethod) return false;
        return SupportedMethods.Any(method => method.Name == reference.Name &&
            TypeName(method.ReturnType) == reference.ReturnType.FullName.Replace('/', '+') &&
            method.GetParameters().Select(parameter => TypeName(parameter.ParameterType)).SequenceEqual(
                reference.Parameters.Select(parameter => parameter.ParameterType.FullName.Replace('/', '+')), StringComparer.Ordinal));
    }

    private static string TypeName(Type type) => type.IsByRef ? TypeName(type.GetElementType()!) + "&" :
        type.IsPointer ? TypeName(type.GetElementType()!) + "*" : type.IsArray ? TypeName(type.GetElementType()!) +
            "[" + new string(',', type.GetArrayRank() - 1) + "]" : type.FullName!;

    private static bool SupportedSignature(MethodInfo method)
    {
        ParameterInfo[] parameters = method.GetParameters();
        bool Output(int index, bool boolean = false)
        {
            Type type = parameters[index].ParameterType;
            Type scalar = type.HasElementType ? type.GetElementType()! : type;
            return type == typeof(IntPtr) || type.IsPointer || (type.IsByRef || type.IsArray) &&
                (scalar == typeof(int) || scalar == typeof(uint) || boolean && scalar == typeof(bool));
        }
        bool Bytes(int index)
        {
            Type type = parameters[index].ParameterType;
            return type == typeof(IntPtr) || type.IsPointer || type.IsArray && type.GetElementType()!.IsPrimitive ||
                type.IsByRef && type.GetElementType()!.IsPrimitive;
        }
        switch (method.Name)
        {
            case "GetInteger": return parameters.Length == 1 && method.ReturnType == typeof(int) || parameters.Length == 2 && Output(1);
            case "GetBoolean": return parameters.Length == 1 && method.ReturnType == typeof(bool) || parameters.Length == 2 && Output(1, true);
            case "GenBuffers": case "GenVertexArrays": case "GenTextures":
            case "DeleteBuffers": case "DeleteVertexArrays": case "DeleteTextures": return parameters.Length == 2 && Output(1);
            case "BufferData": return parameters.Length == 4 && Bytes(2);
            case "BufferSubData": return parameters.Length == 4 && Bytes(3);
            case "ShaderSource": return parameters.Length == 2 && parameters[1].ParameterType == typeof(string) ||
                parameters.Length == 4 && parameters[2].ParameterType == typeof(string[]) && Output(3);
            case "GetProgram": case "GetShader": return parameters.Length == 2 && method.ReturnType == typeof(int) || parameters.Length == 3 && Output(2);
            case "GetProgramInfoLog": case "GetShaderInfoLog": return parameters.Length == 1 && method.ReturnType == typeof(string) ||
                parameters.Length == 4 && Output(2) && (parameters[3].ParameterType == typeof(string).MakeByRefType() ||
                    parameters[3].ParameterType == typeof(System.Text.StringBuilder) || parameters[3].ParameterType == typeof(IntPtr) || parameters[3].ParameterType.IsPointer);
            case "Uniform1": return parameters.Length == 2 && parameters[1].ParameterType is { } value && (value == typeof(int) || value == typeof(float)) ||
                parameters.Length == 3 && parameters[2].ParameterType.HasElementType && parameters[2].ParameterType.GetElementType() == typeof(float);
            case "UniformMatrix4": return parameters.Length == 3 && parameters[2].ParameterType == typeof(OpenTK.Mathematics.Matrix4).MakeByRefType() ||
                parameters.Length == 4 && (parameters[3].ParameterType == typeof(IntPtr) || parameters[3].ParameterType.HasElementType && parameters[3].ParameterType.GetElementType() == typeof(float));
            case "TexParameter": return parameters.Length == 3 && (parameters[2].ParameterType == typeof(int) || parameters[2].ParameterType == typeof(float));
            case "TexSubImage2D": return parameters.Length == 9 && Bytes(8);
            case "DrawElements": return parameters.Length == 4 && (parameters[3].ParameterType == typeof(int) || parameters[3].ParameterType == typeof(IntPtr));
            case "DrawElementsBaseVertex": return parameters.Length == 5 && (parameters[3].ParameterType == typeof(int) || parameters[3].ParameterType == typeof(IntPtr));
            case "ClearColor": return parameters.Length == 4 && parameters.All(parameter => parameter.ParameterType == typeof(float)) ||
                parameters.Length == 1 && (parameters[0].ParameterType == typeof(OpenTK.Mathematics.Color4) ||
                    parameters[0].ParameterType == typeof(OpenTK.Mathematics.Color4).MakeByRefType());
            case "Scissor": case "Viewport": return parameters.Length == 4 && parameters.All(parameter => parameter.ParameterType == typeof(int));
            case "Enable": case "Disable": case "IsEnabled": return parameters.Length == 1;
            case "VertexAttribPointer": return parameters.Length == 6 &&
                (parameters[5].ParameterType == typeof(int) || parameters[5].ParameterType == typeof(IntPtr));
            case "VertexAttribIPointer": return parameters.Length == 5 &&
                (parameters[4].ParameterType == typeof(int) || parameters[4].ParameterType == typeof(IntPtr));
            default: return !parameters.Any(parameter => parameter.ParameterType.IsArray || parameter.ParameterType.IsByRef || parameter.ParameterType.IsPointer);
        }
    }

    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner)
    {
        var harmony = new Harmony(Owner);
        MethodInfo[] targets = SupportedMethods;
        var prefixes = new List<(MethodInfo Target, MethodInfo Prefix)>();
        bool attempted = false;
        return new StartupPatchGroup("graphics-managed-gl", () =>
        {
            foreach (string name in Names)
                if (!targets.Any(target => target.Name == name)) throw new MissingMethodException("OpenTK managed GL entry point is absent: " + name);
            foreach (MethodInfo target in targets)
            {
                if (target.ReturnType.IsByRef || target.ReturnType.IsPointer)
                    throw new NotSupportedException("Managed GL cannot return a borrowed pointer: " + target);
                foreach (ParameterInfo parameter in target.GetParameters())
                    if (parameter.ParameterType.IsByRefLike || parameter.ParameterType.IsByRef && parameter.ParameterType.GetElementType()!.IsByRefLike)
                        throw new NotSupportedException("Managed GL overload cannot box its call arguments: " + target);
            }
        }, () =>
        {
            if (runtime is not null) throw new InvalidOperationException("Managed GL routing already has an owner.");
            runtime = owner; attempted = true;
            foreach (MethodInfo target in targets)
            {
                int key = Calls.Count + 1; Calls.Add(key, target);
                MethodInfo prefix = EmitPrefix(target, key);
                prefixes.Add((target, prefix));
                harmony.Patch(target, prefix: new HarmonyMethod(prefix) { priority = Priority.First });
            }
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner);
            if (ReferenceEquals(runtime, owner)) runtime = null;
            Calls.Clear(); prefixes.Clear(); PrefixOwners.Clear(); attempted = false;
        });
    }

    /// <summary>Dispatches an emitted exact-signature prefix; original GL remains available only while routing is inactive.</summary>
    /// <param name="key">Installed overload identity.</param>
    /// <param name="args">Boxed arguments, including mutable by-reference outputs.</param>
    /// <param name="result">Boxed original return type or null for void.</param>
    /// <returns>Whether Harmony should execute the original OpenTK implementation.</returns>
    public static bool Route(int key, object?[] args, out object? result)
    {
        result = null;
        if (runtime?.IsActive != true)
        { missingContextObserved = false; missingContextError = 0; return true; }
        if (ManagedGlBridge.Current is not { } bridge)
        {
            if (!missingContextObserved) { missingContextObserved = true; missingContextError = 0x0502; }
            MethodInfo method = Calls[key];
            if (method.Name == "GetError")
            {
                result = method.ReturnType.IsEnum ? Enum.ToObject(method.ReturnType, missingContextError) : missingContextError;
                missingContextError = 0;
                return false;
            }
            missingContextError = 0x0502;
            throw new InvalidOperationException("Managed GL command requires a current owned Vulkan context: " + method);
        }
        missingContextObserved = false; missingContextError = 0;
        result = bridge.Dispatch(Calls[key], args);
        return false;
    }

    /// <summary>Keeps a prefix's pinned input reference live through the whole native data-copy dispatch.</summary>
    /// <typeparam name="T">Original input element type.</typeparam>
    /// <param name="value">Original pinned input reference.</param>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static void KeepPinned<T>(ref T value) => GC.KeepAlive(value);

    // Boxing a native pointer through Harmony's object[] convention is not
    // portable, and a ref object result is not the enum/int ABI. Emitted prefixes
    // preserve every exact signature, copying pointer values as IntPtr and
    // writing query outputs/return values using their original managed types.
    private static MethodInfo EmitPrefix(MethodInfo target, int key)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("VulkanStory.ManagedGlPrefixes." + key), AssemblyBuilderAccess.RunAndCollect);
        var module = assembly.DefineDynamicModule("Commands");
        var type = module.DefineType("GlPrefix" + key, System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Abstract | System.Reflection.TypeAttributes.Sealed);
        ParameterInfo[] original = target.GetParameters();
        bool result = target.ReturnType != typeof(void);
        Type[] signature = original.Select(parameter => parameter.ParameterType).Concat(result ? [target.ReturnType.MakeByRefType()] : Type.EmptyTypes).ToArray();
        MethodBuilder prefix = type.DefineMethod("Prefix", System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static, typeof(bool), signature);
        for (int index = 0; index < original.Length; index++) prefix.DefineParameter(index + 1, original[index].Attributes, "__" + index);
        if (result) prefix.DefineParameter(signature.Length, System.Reflection.ParameterAttributes.None, "__result");
        ILGenerator il = prefix.GetILGenerator();
        LocalBuilder arguments = il.DeclareLocal(typeof(object[])), returned = il.DeclareLocal(typeof(object));
        LocalBuilder continueOriginal = il.DeclareLocal(typeof(bool));
        var pinnedInputs = new List<(LocalBuilder Local, Type Element)>();
        Label originalCall = il.DefineLabel();
        il.Emit(OpCodes.Ldc_I4, original.Length); il.Emit(OpCodes.Newarr, typeof(object)); il.Emit(OpCodes.Stloc, arguments);
        for (int index = 0; index < original.Length; index++)
        {
            Type parameter = original[index].ParameterType;
            Type value = parameter.IsByRef ? parameter.GetElementType()! : parameter;
            il.Emit(OpCodes.Ldloc, arguments); il.Emit(OpCodes.Ldc_I4, index);
            if (original[index].IsOut && parameter.IsByRef && !InputPointer(target, index))
            {
                if (value.IsValueType)
                {
                    LocalBuilder empty = il.DeclareLocal(value); il.Emit(OpCodes.Ldloca, empty); il.Emit(OpCodes.Initobj, value); il.Emit(OpCodes.Ldloc, empty); il.Emit(OpCodes.Box, value);
                }
                else il.Emit(OpCodes.Ldnull);
            }
            else
            {
                bool inputPointer = InputPointer(target, index);
                if (inputPointer)
                {
                    // A GL data overload's managed ref can span count elements.
                    // Keep the original interior reference pinned for the full
                    // dispatch, rather than losing GC ownership in an IntPtr.
                    LocalBuilder pinned = il.DeclareLocal(parameter, pinned: true);
                    pinnedInputs.Add((pinned, value));
                    il.Emit(OpCodes.Ldarg, index); il.Emit(OpCodes.Stloc, pinned); il.Emit(OpCodes.Ldloc, pinned);
                }
                else { il.Emit(OpCodes.Ldarg, index); if (parameter.IsByRef) il.Emit(OpCodes.Ldobj, value); }
                if (value.IsPointer || inputPointer)
                { il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.Newobj, typeof(IntPtr).GetConstructor([typeof(long)])!); il.Emit(OpCodes.Box, typeof(IntPtr)); }
                else if (value.IsValueType) il.Emit(OpCodes.Box, value);
            }
            il.Emit(OpCodes.Stelem_Ref);
        }
        il.Emit(OpCodes.Ldc_I4, key); il.Emit(OpCodes.Ldloc, arguments); il.Emit(OpCodes.Ldloca, returned);
        il.Emit(OpCodes.Call, typeof(ManagedGlCallPatches).GetMethod(nameof(Route))!);
        il.Emit(OpCodes.Stloc, continueOriginal);
        foreach (var pinned in pinnedInputs)
        {
            il.Emit(OpCodes.Ldloc, pinned.Local);
            il.Emit(OpCodes.Call, typeof(ManagedGlCallPatches).GetMethod(nameof(KeepPinned))!.MakeGenericMethod(pinned.Element));
        }
        il.Emit(OpCodes.Ldloc, continueOriginal);
        il.Emit(OpCodes.Brtrue, originalCall);
        for (int index = 0; index < original.Length; index++)
        {
            Type parameter = original[index].ParameterType;
            if (!parameter.IsByRef) continue;
            if (InputPointer(target, index)) continue;
            Type value = parameter.GetElementType()!;
            il.Emit(OpCodes.Ldarg, index); il.Emit(OpCodes.Ldloc, arguments); il.Emit(OpCodes.Ldc_I4, index); il.Emit(OpCodes.Ldelem_Ref);
            il.Emit(value.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, value); il.Emit(OpCodes.Stobj, value);
        }
        if (result)
        {
            il.Emit(OpCodes.Ldarg, original.Length); il.Emit(OpCodes.Ldloc, returned);
            il.Emit(target.ReturnType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, target.ReturnType); il.Emit(OpCodes.Stobj, target.ReturnType);
        }
        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
        il.MarkLabel(originalCall); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ret);
        Type owner = type.CreateType()!; PrefixOwners.Add(owner); return owner.GetMethod("Prefix")!;
    }

    private static bool InputPointer(MethodInfo method, int index) => method.GetParameters()[index].ParameterType.IsByRef &&
        (method.Name, index) is
            ("BufferData", 2) or ("BufferSubData", 3) or ("TexSubImage2D", 8) or
            ("Uniform1", 2) or ("UniformMatrix4", 3) or ("ShaderSource", 3) or
            ("GenBuffers", 1) or ("GenVertexArrays", 1) or ("GenTextures", 1) or
            ("DeleteBuffers", 1) or ("DeleteVertexArrays", 1) or ("DeleteTextures", 1);
}
