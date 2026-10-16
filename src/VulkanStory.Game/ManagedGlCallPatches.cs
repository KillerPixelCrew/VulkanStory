using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Mono.Cecil;
using OpenTK.Graphics.OpenGL4;

namespace VulkanStory.Game;

/// <summary>Installs exact managed OpenTK command entry points for owned Vulkan contexts.</summary>
public static partial class ManagedGlCallPatches
{
    private const string Owner = "vulkanstory.routing.managed-gl";
    private static ProcessRuntime? runtime;
    [ThreadStatic] private static bool missingContextObserved;
    [ThreadStatic] private static int missingContextError;
    private static readonly Dictionary<int, MethodInfo> Calls = new();
    private static readonly Type[] Bindings = [typeof(GL), typeof(OpenTK.Graphics.OpenGL.GL)];
    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        "GetInteger", "GetBoolean", "GetDouble", "GetError", "GenVertexArray", "GenVertexArrays", "BindVertexArray", "IsVertexArray", "DeleteVertexArray", "DeleteVertexArrays",
        "GetVertexAttrib", "GetVertexAttribPointer",
        "GenBuffer", "GenBuffers", "BindBuffer", "DeleteBuffer", "DeleteBuffers", "BufferData", "BufferSubData", "VertexAttribPointer", "VertexAttribIPointer",
        "EnableVertexAttribArray", "DisableVertexAttribArray", "DrawElements", "DrawElementsBaseVertex", "ActiveTexture", "GenTexture", "GenTextures",
        "BindTexture", "DeleteTexture", "DeleteTextures", "TexStorage2D", "TexSubImage2D", "GenerateMipmap", "TexParameter",
        "CreateProgram", "AttachShader", "LinkProgram", "GetProgram", "GetProgramInfoLog", "DetachShader", "DeleteShader", "DeleteProgram", "CreateShader",
        "ShaderSource", "CompileShader", "GetShader", "GetShaderInfoLog", "UseProgram", "UniformMatrix4", "Uniform1", "GetUniformLocation", "GetAttribLocation", "BindAttribLocation",
        "Enable", "Disable", "IsEnabled", "BlendEquation", "BlendEquationSeparate", "BlendFunc", "BlendFuncSeparate", "PolygonMode", "Scissor", "Viewport", "Clear", "ClearColor",
        "DepthFunc", "DepthMask", "ColorMask", "CullFace",
        "Uniform2", "Uniform3", "Uniform4",
        "GenFramebuffer", "GenFramebuffers", "DeleteFramebuffer", "DeleteFramebuffers", "BindFramebuffer", "FramebufferTexture2D",
        "CheckFramebufferStatus", "GetFramebufferAttachmentParameter", "DrawBuffer", "DrawBuffers", "ReadBuffer",
        "PixelStore", "GetTexImage", "GetTextureSubImage", "ReadPixels", "GetTexLevelParameter", "IsTexture",
        "LoadBindings",
        "BlitFramebuffer", "CopyTexSubImage2D",
        "DrawArrays",
        "TexImage2D", "TexImage3D", "TexSubImage3D",
        "GetFloat", "GetString", "ClearDepth", "BindSampler", "PushDebugGroup", "PopDebugGroup", "DebugMessageInsert",
        "GetActiveUniform", "GetUniform", "ProgramUniform1",
        "ClearBuffer",
        "GenQuery", "GenQueries", "DeleteQuery", "DeleteQueries", "BeginQuery", "EndQuery", "QueryCounter", "GetQueryObject", "Finish",
        "PolygonOffset",
        "ClipPlane", "GetClipPlane",
        "ClearStencil", "StencilFunc", "StencilFuncSeparate", "StencilMask", "StencilMaskSeparate", "StencilOp", "StencilOpSeparate",
        "BlendColor",
        "GenRenderbuffer", "GenRenderbuffers", "DeleteRenderbuffer", "DeleteRenderbuffers", "BindRenderbuffer", "IsRenderbuffer",
        "RenderbufferStorage", "RenderbufferStorageMultisample", "FramebufferRenderbuffer", "GetRenderbufferParameter",
    };
    private static readonly List<Type> PrefixOwners = new();
    private static readonly MethodInfo[] SupportedMethods = Bindings.SelectMany(binding => binding.GetMethods(BindingFlags.Public | BindingFlags.Static))
        .Where(method => Names.Contains(method.Name) && !method.IsGenericMethodDefinition && !method.ContainsGenericParameters &&
            method.GetMethodBody() is not null && SupportedSignature(method)).OrderBy(method => method.MetadataToken).ToArray();

    internal static bool SupportsManagedCall(MethodReference reference)
    {
        if (!Bindings.Any(binding => binding.FullName == reference.DeclaringType.FullName)) return false;
        if (reference is GenericInstanceMethod generic) return SupportsNativeGeneric(generic);
        if (reference.HasGenericParameters) return false;
        return SupportedMethods.Any(method => method.DeclaringType!.FullName == reference.DeclaringType.FullName && method.Name == reference.Name &&
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
            case "ClipPlane": case "GetClipPlane": return parameters.Length == 2 &&
                (parameters[1].ParameterType == typeof(double[]) || parameters[1].ParameterType.IsPointer && parameters[1].ParameterType.GetElementType() == typeof(double) || parameters[1].ParameterType == typeof(IntPtr));
            case "GetInteger": return parameters.Length == 1 && method.ReturnType == typeof(int) || parameters.Length == 2 && Output(1) || parameters.Length == 3 && Output(2);
            case "GetBoolean": return parameters.Length == 1 && method.ReturnType == typeof(bool) || parameters.Length == 2 && Output(1, true) || parameters.Length == 3 && Output(2, true);
            case "GetFloat": return parameters.Length == 1 && method.ReturnType == typeof(float) || parameters.Length == 2 &&
                (parameters[1].ParameterType == typeof(IntPtr) || parameters[1].ParameterType.HasElementType && parameters[1].ParameterType.GetElementType() == typeof(float));
            case "GetDouble": return parameters.Length == 1 && method.ReturnType == typeof(double) || parameters.Length == 2 &&
                (parameters[1].ParameterType == typeof(IntPtr) || parameters[1].ParameterType.HasElementType && parameters[1].ParameterType.GetElementType() == typeof(double));
            case "GetVertexAttrib": return parameters.Length == 3 && parameters[2].ParameterType.HasElementType &&
                parameters[2].ParameterType.GetElementType() is { } attributeType && (attributeType == typeof(int) || attributeType == typeof(float) || attributeType == typeof(double));
            case "GetVertexAttribPointer": return parameters.Length == 3 &&
                (parameters[2].ParameterType == typeof(IntPtr) || parameters[2].ParameterType.HasElementType && parameters[2].ParameterType.GetElementType() == typeof(IntPtr) ||
                 parameters[2].ParameterType.IsPointer && parameters[2].ParameterType.GetElementType()!.IsPointer);
            case "GetString": return parameters.Length is 1 or 2 && method.ReturnType == typeof(string);
            case "GetActiveUniform": return parameters.Length == 4 && method.ReturnType == typeof(string) && parameters[2].ParameterType.IsByRef && parameters[3].ParameterType.IsByRef ||
                parameters.Length == 7 && method.ReturnType == typeof(void) && parameters.Skip(3).All(parameter => parameter.ParameterType.IsPointer || parameter.ParameterType == typeof(nint));
            case "GetUniform": return parameters.Length == 3 && parameters[2].ParameterType.HasElementType && parameters[2].ParameterType.GetElementType() is { } uniformType && (uniformType == typeof(int) || uniformType == typeof(float));
            case "ProgramUniform1": return parameters.Length == 3 && (parameters[2].ParameterType == typeof(int) || parameters[2].ParameterType == typeof(float));
            case "ClearBuffer": return parameters.Length == 3 && parameters[2].ParameterType.HasElementType && parameters[2].ParameterType.GetElementType() is { } clearType && (clearType == typeof(int) || clearType == typeof(float));
            case "PushDebugGroup": return parameters.Length == 4 && parameters[3].ParameterType == typeof(string);
            case "DebugMessageInsert": return parameters.Length == 6 && parameters[5].ParameterType == typeof(string);
            case "GenBuffers": case "GenVertexArrays": case "GenTextures": case "GenFramebuffers": case "GenQueries": case "GenRenderbuffers":
            case "DeleteBuffers": case "DeleteVertexArrays": case "DeleteTextures": case "DeleteFramebuffers": case "DeleteQueries": case "DeleteRenderbuffers": return parameters.Length == 2 && Output(1);
            case "GetRenderbufferParameter": return parameters.Length == 2 && method.ReturnType == typeof(int) || parameters.Length == 3 && Output(2);
            case "GetQueryObject": return parameters.Length == 3 && parameters[2].ParameterType.HasElementType && parameters[2].ParameterType.GetElementType() is { } queryType &&
                (queryType == typeof(int) || queryType == typeof(uint) || queryType == typeof(long) || queryType == typeof(ulong));
            case "DrawBuffers": return parameters.Length == 2 && (parameters[1].ParameterType.IsArray && parameters[1].ParameterType.GetElementType()!.IsEnum || Output(1));
            case "GetFramebufferAttachmentParameter": return parameters.Length == 3 && method.ReturnType == typeof(int) || parameters.Length == 4 && Output(3);
            case "GetTexLevelParameter": return parameters.Length == 3 && method.ReturnType == typeof(int) || parameters.Length == 4 && Output(3);
            case "GetTexImage": return parameters.Length == 5 && Bytes(4);
            case "GetTextureSubImage": return parameters.Length == 12 && Bytes(11);
            case "ReadPixels": return parameters.Length == 7 && Bytes(6);
            case "PixelStore": return parameters.Length == 2 && parameters[1].ParameterType == typeof(int);
            case "BufferData": return parameters.Length == 4 && Bytes(2);
            case "BufferSubData": return parameters.Length == 4 && Bytes(3);
            case "ShaderSource": return parameters.Length == 2 && parameters[1].ParameterType == typeof(string) ||
                parameters.Length == 4 && (parameters[2].ParameterType == typeof(string[]) && Output(3) ||
                    (parameters[2].ParameterType.IsPointer || parameters[2].ParameterType == typeof(nint)) && Output(3));
            case "GetProgram": case "GetShader": return parameters.Length == 2 && method.ReturnType == typeof(int) || parameters.Length == 3 && Output(2);
            case "GetProgramInfoLog": case "GetShaderInfoLog": return parameters.Length == 1 && method.ReturnType == typeof(string) ||
                parameters.Length == 4 && Output(2) && (parameters[3].ParameterType == typeof(string).MakeByRefType() ||
                    parameters[3].ParameterType == typeof(System.Text.StringBuilder) || parameters[3].ParameterType == typeof(IntPtr) || parameters[3].ParameterType.IsPointer);
            case "Uniform1": return parameters.Length == 2 && parameters[1].ParameterType is { } value && (value == typeof(int) || value == typeof(float)) ||
                parameters.Length == 3 && parameters[2].ParameterType.HasElementType && parameters[2].ParameterType.GetElementType() == typeof(float);
            case "Uniform2": case "Uniform3": case "Uniform4":
                int components = method.Name[^1] - '0';
                return components == 2 && parameters.Length == 3 && parameters[1].ParameterType == typeof(int) && parameters[2].ParameterType == typeof(int) ||
                    parameters.Length == components + 1 && parameters.Skip(1).All(parameter => parameter.ParameterType == typeof(float)) ||
                    parameters.Length == 3 && parameters[1].ParameterType == typeof(int) &&
                    (parameters[2].ParameterType == typeof(IntPtr) || parameters[2].ParameterType.HasElementType && parameters[2].ParameterType.GetElementType() == typeof(float));
            case "UniformMatrix4": return parameters.Length == 3 && parameters[2].ParameterType == typeof(OpenTK.Mathematics.Matrix4).MakeByRefType() ||
                parameters.Length == 4 && (parameters[3].ParameterType == typeof(IntPtr) || parameters[3].ParameterType.HasElementType && parameters[3].ParameterType.GetElementType() == typeof(float));
            case "TexParameter": return parameters.Length == 3 && (parameters[2].ParameterType == typeof(int) || parameters[2].ParameterType == typeof(float) || parameters[2].ParameterType.HasElementType && parameters[2].ParameterType.GetElementType() == typeof(float));
            case "TexSubImage2D": return parameters.Length == 9 && Bytes(8);
            case "TexImage2D": return parameters.Length == 9 && Bytes(8);
            case "TexImage3D": return parameters.Length == 10 && Bytes(9);
            case "TexSubImage3D": return parameters.Length == 11 && Bytes(10);
            case "DrawElements": return parameters.Length == 4 && (parameters[3].ParameterType == typeof(int) || parameters[3].ParameterType == typeof(IntPtr));
            case "DrawElementsBaseVertex": return parameters.Length == 5 && (parameters[3].ParameterType == typeof(int) || parameters[3].ParameterType == typeof(IntPtr));
            case "ClearColor": return parameters.Length == 4 && parameters.All(parameter => parameter.ParameterType == typeof(float)) ||
                parameters.Length == 1 && (parameters[0].ParameterType == typeof(OpenTK.Mathematics.Color4) ||
                    parameters[0].ParameterType == typeof(OpenTK.Mathematics.Color4).MakeByRefType());
            case "Scissor": case "Viewport": return parameters.Length == 4 && parameters.All(parameter => parameter.ParameterType == typeof(int));
            case "Enable": case "Disable": case "IsEnabled": return parameters.Length is 1 or 2;
            case "DepthFunc": case "CullFace": return parameters.Length == 1 && parameters[0].ParameterType.IsEnum;
            case "DepthMask": return parameters.Length == 1 && parameters[0].ParameterType == typeof(bool);
            case "ColorMask": return parameters.Length == 4 && parameters.All(parameter => parameter.ParameterType == typeof(bool)) || parameters.Length == 5 && parameters.Skip(1).All(parameter => parameter.ParameterType == typeof(bool));
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
            _ = NativePlans.Value;
            NativeGlLibraryRouting.Prepare();
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
            InstallNativeEntries();
            InstallNativeProcedures();
            NativeGlLibraryRouting.Install(harmony, owner);
        }, () =>
        {
            if (!attempted) return;
            NativeGlLibraryRouting.Restore();
            RestoreNativeEntries();
            harmony.UnpatchAll(Owner);
            if (ReferenceEquals(runtime, owner)) runtime = null;
            Calls.Clear(); prefixes.Clear(); PrefixOwners.Clear(); attempted = false;
        });
    }

    /// <summary>Dispatches an exact-signature prefix; original GL is available only before Vulkan ownership commits.</summary>
    /// <param name="key">Installed overload identity.</param>
    /// <param name="args">Boxed arguments, including mutable by-reference outputs.</param>
    /// <param name="result">Boxed original return type or null for void.</param>
    /// <returns>Whether Harmony should execute the original OpenTK implementation.</returns>
    public static bool Route(int key, object?[] args, out object? result)
    {
        result = null;
        if (runtime?.HasCommitted != true)
        { missingContextObserved = false; missingContextError = 0; return true; }
        if (Calls[key].Name == "LoadBindings")
        {
            if (args[0] is null) throw new ArgumentNullException("context");
            ActivateNativeEntries();
            return false;
        }
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
            ("Uniform1", 2) or ("Uniform2", 2) or ("Uniform3", 2) or ("Uniform4", 2) or ("UniformMatrix4", 3) or ("ShaderSource", 3) or
            ("GenBuffers", 1) or ("GenVertexArrays", 1) or ("GenTextures", 1) or
            ("GenFramebuffers", 1) or ("DeleteFramebuffers", 1) or ("DrawBuffers", 1) or
            ("GetTexImage", 4) or ("GetTextureSubImage", 11) or ("ReadPixels", 6) or
            ("TexImage2D", 8) or ("TexImage3D", 9) or ("TexSubImage3D", 10) or
            ("ClearBuffer", 2) or
            ("TexParameter", 2) or
            ("GenQueries", 1) or ("DeleteQueries", 1) or
            ("DeleteBuffers", 1) or ("DeleteVertexArrays", 1) or ("DeleteTextures", 1);
}
