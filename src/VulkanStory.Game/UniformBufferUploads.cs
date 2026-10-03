using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;
using Vintagestory.API.Util;

namespace VulkanStory.Game;

/// <summary>Capture the pointer upload from unchanged generic UBO methods after their owned Bind.</summary>
internal static class UniformBufferUploads
{
    [ThreadStatic] private static UBO? bound;
    internal static UBO? BoundForTests => bound;
    private sealed record ObservedHandle(GCHandleProvider Provider, UBO Buffer);
    [ThreadStatic] private static Dictionary<IntPtr, ObservedHandle>? observedHandles;
    internal static int ObservedHandlesForTests => observedHandles?.Count ?? 0;
    private static MethodInfo? dataTarget, subDataTarget;
    private static readonly MethodInfo PointerGetter = typeof(GCHandleProvider).GetProperty(nameof(GCHandleProvider.Pointer))!.GetMethod!;
    private static readonly MethodInfo ProviderDispose = typeof(GCHandleProvider).GetMethod(nameof(GCHandleProvider.Dispose), Type.EmptyTypes)!;

    internal static void NoteBound(UBO buffer) => bound = buffer;
    internal static void Clear() { bound = null; observedHandles?.Clear(); }
    internal static void NoteDeleted(UBO buffer) { if (ReferenceEquals(bound, buffer)) Clear(); }

    internal static void ValidateBindings()
    {
        MethodInfo[] definitions = typeof(UBO).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == "Update" && method.IsGenericMethodDefinition).ToArray();
        if (definitions.Length != 2) throw new InvalidOperationException("Expected two original generic UBO updates.");
        foreach (var definition in definitions)
            if (PatchProcessor.GetOriginalInstructions(definition.MakeGenericMethod(typeof(long)))
                .Count(instruction => instruction.Calls(PointerGetter)) != 1)
                throw new InvalidOperationException("Generic UBO update must use one original GCHandleProvider.Pointer getter.");
        if (PointerGetter.ReturnType != typeof(IntPtr) || PointerGetter.GetMethodBody() is null ||
            ProviderDispose.ReturnType != typeof(void) || ProviderDispose.GetMethodBody() is null)
            throw new InvalidOperationException("Original UBO handle provider contracts changed.");
        dataTarget = UploadCall(definitions.Single(method => method.GetParameters().Length == 1), "BufferData");
        subDataTarget = UploadCall(definitions.Single(method => method.GetParameters().Length == 3), "BufferSubData");
        ValidateUpload(dataTarget, subrange: false);
        ValidateUpload(subDataTarget, subrange: true);
    }

    private static MethodInfo UploadCall(MethodInfo definition, string name)
    {
        // Inspect a closed body without patching or executing generic JIT code.
        var calls = PatchProcessor.GetOriginalInstructions(definition.MakeGenericMethod(typeof(long)))
            .Where(instruction => instruction.operand is MethodInfo method && method.DeclaringType == typeof(GL) && method.Name == name)
            .Select(instruction => (MethodInfo)instruction.operand).ToArray();
        if (calls.Length != 1) throw new InvalidOperationException("Generic UBO update must contain one expected GL upload.");
        return calls[0];
    }
    private static void ValidateUpload(MethodInfo method, bool subrange)
    {
        Type[] parameters = method.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        int sizeIndex = subrange ? 2 : 1;
        if (!method.IsStatic || method.ReturnType != typeof(void) || method.GetMethodBody() is null ||
            parameters.Length != 4 || parameters[0] != typeof(BufferTarget) ||
            (parameters[sizeIndex] != typeof(int) && parameters[sizeIndex] != typeof(IntPtr)) ||
            (subrange ? parameters[1] != typeof(IntPtr) || parameters[3] != typeof(IntPtr) :
                parameters[2] != typeof(IntPtr) || parameters[3] != typeof(BufferUsageHint)))
            throw new InvalidOperationException("Original UBO pointer upload signature changed.");
    }
    internal static void Install(Harmony harmony)
    {
        if (dataTarget is null || subDataTarget is null) throw new InvalidOperationException("UBO upload targets were not validated.");
        string dataPrefix = dataTarget.GetParameters()[1].ParameterType == typeof(int) ? nameof(DataInt) : nameof(DataPointer);
        string subPrefix = subDataTarget.GetParameters()[2].ParameterType == typeof(int) ? nameof(SubInt) : nameof(SubPointer);
        harmony.Patch(dataTarget, prefix: new HarmonyMethod(typeof(UniformBufferUploads), dataPrefix));
        harmony.Patch(subDataTarget, prefix: new HarmonyMethod(typeof(UniformBufferUploads), subPrefix));
        harmony.Patch(PointerGetter, postfix: new HarmonyMethod(typeof(UniformBufferUploads), nameof(ObservePointer)));
        harmony.Patch(ProviderDispose, prefix: new HarmonyMethod(typeof(UniformBufferUploads), nameof(ForgetProvider)));
    }
    private static bool Upload(BufferTarget target, IntPtr offset, IntPtr size, IntPtr data)
    {
        if (!ShaderConsumerPatches.GraphicsRoutingEnabled || (int)target != 35345) return true;
        UBO buffer = bound ?? throw new InvalidOperationException("Active uniform upload has no bound renderer UBO.");
        var owner = GameGraphicsAdapter.UniformBufferOwner(buffer);
        int byteOffset = checked((int)offset.ToInt64()), byteSize = checked((int)size.ToInt64());
        if (observedHandles?.TryGetValue(data, out var observation) == true)
        {
            if (!ReferenceEquals(observation.Buffer, buffer))
                throw new InvalidOperationException("Generic UBO helper belongs to a different bound buffer.");
            // The original helper exposes a handle token. Only a token observed
            // from that helper is converted; arbitrary native pointers are never decoded.
            object payload = observation.Provider.Handle.Target ??
                throw new InvalidOperationException("Generic UBO helper has no payload.");
            owner.ObserveAnimationPayload(buffer, payload, byteOffset, byteSize);
            GCHandle pin = GCHandle.Alloc(payload, GCHandleType.Pinned);
            try { owner.UploadUniformBuffer(buffer, pin.AddrOfPinnedObject(), byteOffset, byteSize); }
            finally { pin.Free(); }
        }
        else owner.UploadUniformBuffer(buffer, data, byteOffset, byteSize);
        return false;
    }
    private static void ObservePointer(GCHandleProvider __instance, IntPtr __result)
    {
        if (!ShaderConsumerPatches.GraphicsRoutingEnabled || bound is null) return;
        GameGraphicsAdapter.UniformBufferOwner(bound);
        (observedHandles ??= new())[__result] = new ObservedHandle(__instance, bound);
    }
    private static void ForgetProvider(GCHandleProvider __instance)
    {
        if (observedHandles is null) return;
        IntPtr? token = null;
        foreach (var entry in observedHandles)
            if (ReferenceEquals(entry.Value.Provider, __instance)) { token = entry.Key; break; }
        if (token.HasValue) observedHandles.Remove(token.Value);
    }
    private static bool DataInt(BufferTarget __0, int __1, IntPtr __2, BufferUsageHint __3) => Upload(__0, IntPtr.Zero, (IntPtr)__1, __2);
    private static bool DataPointer(BufferTarget __0, IntPtr __1, IntPtr __2, BufferUsageHint __3) => Upload(__0, IntPtr.Zero, __1, __2);
    private static bool SubInt(BufferTarget __0, IntPtr __1, int __2, IntPtr __3) => Upload(__0, __1, (IntPtr)__2, __3);
    private static bool SubPointer(BufferTarget __0, IntPtr __1, IntPtr __2, IntPtr __3) => Upload(__0, __1, __2, __3);
}
