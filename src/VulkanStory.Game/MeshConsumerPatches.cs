using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

internal static class MeshConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-meshes";
    private static Func<bool>? enabled;
    private sealed record Binding(string Name, Type Result, Type[] Parameters, string Prefix);
    private static readonly Binding[] Bindings =
    [
        new("UploadMesh", typeof(MeshRef), [typeof(MeshData)], nameof(Upload)),
        new("UpdateMesh", typeof(void), [typeof(MeshRef), typeof(MeshData)], nameof(Update)),
        new("UpdateSSBOMesh", typeof(void), [typeof(MeshRef), typeof(MeshData)], nameof(UpdateSsbo)),
        new("DeleteMesh", typeof(void), [typeof(MeshRef)], nameof(Delete)),
        new("RenderMesh", typeof(void), [typeof(MeshRef)], nameof(Draw)),
        new("RenderMesh", typeof(void), [typeof(MeshRef), typeof(int[]), typeof(int[]), typeof(int), typeof(bool)], nameof(MultiDraw)),
        new("RenderMeshInstanced", typeof(void), [typeof(MeshRef), typeof(int)], nameof(Instanced)),
        new("RenderFullscreenTriangle", typeof(void), [typeof(MeshRef)], nameof(Fullscreen)),
        new("AllocateEmptyMesh", typeof(MeshRef), [typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int),
            typeof(CustomMeshDataPartFloat), typeof(CustomMeshDataPartShort), typeof(CustomMeshDataPartByte),
            typeof(CustomMeshDataPartInt), typeof(EnumDrawMode), typeof(bool)], nameof(Allocate)),
        new("AllocateEmptySSBOMesh", typeof(MeshRef), [typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int),
            typeof(CustomMeshDataPartFloat), typeof(CustomMeshDataPartShort), typeof(CustomMeshDataPartByte),
            typeof(CustomMeshDataPartInt), typeof(EnumDrawMode), typeof(bool)], nameof(AllocateSsbo)),
    ];
    private static MethodInfo Resolve(Binding binding)
    {
        var method = typeof(ClientPlatformWindows).GetMethod(binding.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
            binder: null, types: binding.Parameters, modifiers: null);
        if (method is null || method.ReturnType != binding.Result || method.GetMethodBody() is null)
            throw new MissingMethodException(typeof(ClientPlatformWindows).FullName, binding.Name);
        return method;
    }
    private static MethodInfo DisposeTarget => typeof(VAO).GetMethod(nameof(VAO.Dispose), Type.EmptyTypes)!;
    private static readonly MethodInfo GlDelete = typeof(GL).GetMethod(nameof(GL.DeleteVertexArray), [typeof(int)])!;
    private static void Check(IEnumerable<CodeInstruction> body)
    {
        if (body.Count(instruction => instruction.Calls(GlDelete)) != 1)
            throw new InvalidOperationException("Original VAO.Dispose must contain one GL.DeleteVertexArray call.");
    }
    internal static StartupPatchGroup CreateSubset(Func<bool> routing)
    {
        ArgumentNullException.ThrowIfNull(routing);
        var harmony = new Harmony(Owner);
        (MethodInfo Target, MethodInfo Prefix)[]? methods = null;
        bool attempted = false;
        return new StartupPatchGroup("graphics-meshes", () =>
        {
            methods = Bindings.Select(binding => (Resolve(binding), typeof(MeshConsumerPatches).GetMethod(binding.Prefix,
                BindingFlags.NonPublic | BindingFlags.Static)!)).ToArray();
            Check(PatchProcessor.GetOriginalInstructions(DisposeTarget));
        }, () =>
        {
            if (methods is null || enabled != null) throw new InvalidOperationException("Mesh routing is unvalidated or already owned.");
            enabled = routing; attempted = true;
            foreach (var method in methods) harmony.Patch(method.Target, prefix: new HarmonyMethod(method.Prefix));
            harmony.Patch(DisposeTarget, prefix: new HarmonyMethod(typeof(MeshConsumerPatches), nameof(DisposeGuard)),
                transpiler: new HarmonyMethod(typeof(MeshConsumerPatches), nameof(DisposeTranspiler)));
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner);
            if (ReferenceEquals(enabled, routing)) enabled = null;
            attempted = false;
        });
    }
    private static bool TryAdapter(ClientPlatformWindows platform, out GameGraphicsAdapter adapter)
    {
        adapter = null!;
        if (enabled?.Invoke() != true) return false;
        if (!GameGraphicsAdapter.TryGet(platform, out var found) || found is null)
            throw new InvalidOperationException("Active mesh routing has no renderer adapter.");
        adapter = found; return true;
    }
    private static bool Upload(ClientPlatformWindows __instance, MeshData __0, ref MeshRef __result)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        __result = adapter.UploadMesh(__0); return false;
    }
    private static bool Update(ClientPlatformWindows __instance, MeshRef __0, MeshData __1)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.UpdateMesh(__0, __1); return false;
    }
    private static bool Delete(ClientPlatformWindows __instance, MeshRef __0)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.DeleteMesh(__0); return false;
    }
    private static bool UpdateSsbo(ClientPlatformWindows __instance, MeshRef __0, MeshData __1)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.UpdateSsboMesh(__0, __1); return false;
    }
    private static bool Draw(ClientPlatformWindows __instance, MeshRef __0)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.RenderMesh(__0); return false;
    }
    private static bool MultiDraw(ClientPlatformWindows __instance, MeshRef __0, int[] __1, int[] __2, int __3, bool __4)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.RenderMesh(__0, __1, __2, __3, __4); return false;
    }
    private static bool Instanced(ClientPlatformWindows __instance, MeshRef __0, int __1)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.RenderMeshInstanced(__0, __1); return false;
    }
    private static bool Fullscreen(ClientPlatformWindows __instance, MeshRef __0)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.RenderFullscreenTriangle(__0); return false;
    }
    private static bool Allocate(ClientPlatformWindows __instance, int __0, int __1, int __2, int __3, int __4, int __5,
        CustomMeshDataPartFloat __6, CustomMeshDataPartShort __7, CustomMeshDataPartByte __8,
        CustomMeshDataPartInt __9, EnumDrawMode __10, bool __11, ref MeshRef __result)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        __result = adapter.AllocateEmptyMesh(__0, __1, __2, __3, __4, __5, __6, __7, __8, __9, __10, __11); return false;
    }
    private static bool AllocateSsbo(ClientPlatformWindows __instance, int __0, int __1, int __2, int __3, int __4, int __5,
        CustomMeshDataPartFloat __6, CustomMeshDataPartShort __7, CustomMeshDataPartByte __8,
        CustomMeshDataPartInt __9, EnumDrawMode __10, bool __11, ref MeshRef __result)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        __result = adapter.AllocateEmptySsboMesh(__0, __1, __2, __3, __4, __5, __6, __7, __8, __9, __10, __11); return false;
    }
    private static void DisposeGuard(VAO __instance)
    {
        if (enabled?.Invoke() != true || __instance.Disposed) return;
        GameGraphicsAdapter.MeshAdapter(__instance).RequireOwnedMesh(__instance);
        if (__instance.xyzVboId != 0 || __instance.normalsVboId != 0 || __instance.uvVboId != 0 ||
            __instance.rgbaVboId != 0 || __instance.flagsVboId != 0 || __instance.vboIdIndex != 0 ||
            __instance.customDataFloatVboId != 0 || __instance.customDataShortVboId != 0 ||
            __instance.customDataIntVboId != 0 || __instance.customDataByteVboId != 0)
            throw new InvalidOperationException("Renderer mesh reference contains unexpected GL buffer handles.");
    }
    private static IEnumerable<CodeInstruction> DisposeTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList(); Check(body);
        var wrapper = typeof(MeshConsumerPatches).GetMethod(nameof(Release), BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var instruction in body)
        {
            if (instruction.Calls(GlDelete))
            {
                var receiver = new CodeInstruction(OpCodes.Ldarg_0);
                receiver.labels.AddRange(instruction.labels);
                receiver.blocks.AddRange(instruction.blocks.Where(block => block.blockType != ExceptionBlockType.EndExceptionBlock));
                instruction.labels.Clear(); instruction.blocks.RemoveAll(block => block.blockType != ExceptionBlockType.EndExceptionBlock);
                yield return receiver; instruction.opcode = OpCodes.Call; instruction.operand = wrapper;
            }
            yield return instruction;
        }
    }
    private static void Release(int handle, VAO mesh)
    {
        if (enabled?.Invoke() != true) { GL.DeleteVertexArray(handle); return; }
        GameGraphicsAdapter.MeshAdapter(mesh).ReleaseMesh(mesh);
    }
}
