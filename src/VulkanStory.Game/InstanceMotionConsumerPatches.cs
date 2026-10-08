using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

// Original mechanical renderer transforms remain intact; only instance storage changes.
/// <summary>Extends pinned Survival instance producers with current and previous transforms under scoped device identities.</summary>
/// <remarks>Patch discovery and installation belong to startup. Callbacks use the committed routing predicate; game object identity remains in the integration assembly.</remarks>
internal static class InstanceMotionConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-motion-instances";
    private static ProcessRuntime? runtime;
    private static Func<object, float[]>? transform;
    private static Dictionary<MethodBase, (int Allocations, int Strides)> expected = new();
    private static readonly MethodInfo Allocation = AccessTools.Method(typeof(CustomMeshDataPart<float>),
        nameof(CustomMeshDataPart<float>.SetAllocationSize), [typeof(int)])!;

    /// <summary>Creates the dormant patch group for this consumer path; validation and installation remain separate transaction steps.</summary>
    /// <param name="owner">Process runtime that owns this group and its session.</param>
    /// <param name="survival">Original Survival assembly containing the pinned built-in consumers.</param>
    /// <returns>Validation, installation and removal callbacks for the startup transaction.</returns>
    /// <remarks>Binding or IL-anchor mismatches reject the group. Creating the group does not enable graphics routing.</remarks>
    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner, Assembly survival)
    {
        const string ns = "Vintagestory.GameContent.Mechanics.";
        Type baseType = survival.GetType(ns + "MechBlockRenderer", true)!;
        FieldInfo matrix = AccessTools.Field(baseType, "tmpMat") ?? throw new MissingFieldException(baseType.FullName, "tmpMat");
        var instance = Expression.Parameter(typeof(object), "renderer");
        Func<object, float[]> getter = Expression.Lambda<Func<object, float[]>>(
            Expression.Field(Expression.Convert(instance, baseType), matrix), instance).Compile();
        var targets = new Dictionary<MethodBase, (int Allocations, int Strides)>();
        var identities = new List<MethodInfo>();
        var writers = new List<MethodInfo>();
        var profile = new (string Name, int Allocations, int Counts, bool Helper, bool Writer)[]
        {
            ("GenericMechBlockRenderer", 1, 1, false, false),
            ("AngledCageGearRenderer", 1, 1, false, false),
            ("AngledGearsBlockRenderer", 1, 2, false, false),
            ("TransmissionBlockRenderer", 2, 2, false, false),
            ("ClutchBlockRenderer", 2, 2, false, true),
            ("CreativeRotorRenderer", 1, 5, true, true),
            ("PulverizerRenderer", 1, 3, true, true),
        };
        const BindingFlags declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        MethodInfo Find(Type type, string name, Func<MethodInfo, bool> match)
        {
            var matches = type.GetMethods(declared).Where(method => method.Name == name && match(method)).ToArray();
            if (matches.Length != 1)
                throw new MissingMethodException($"Original mechanical target {type.FullName}.{name}: expected one matching declaration, found {matches.Length}.");
            return matches[0];
        }
        MethodInfo Writer(Type type) => Find(type, "UpdateLightAndTransformMatrix",
            method => method.GetParameters() is { Length: >= 4 } args && args[0].ParameterType == typeof(float[]) &&
                args[1].ParameterType == typeof(int) && args[3].ParameterType == typeof(Vec4f));
        MethodInfo baseWriter = Writer(baseType);
        targets.Add(baseWriter, (0, 1)); writers.Add(baseWriter);
        foreach (var item in profile)
        {
            Type type = survival.GetType(ns + item.Name, true)!;
            MethodBase allocation = item.Helper ? Find(type, "createCustomFloats", method =>
                method.ReturnType == typeof(CustomMeshDataPartFloat) && method.GetParameters().Length == 1) :
                type.GetConstructors(declared).Single();
            targets.Add(allocation, (item.Allocations, 0));
            targets.Add(Find(type, "OnRenderFrame", method => method.GetParameters().Length == 2), (0, item.Counts));
            identities.Add(Find(type, "UpdateLightAndTransformMatrix", method =>
                method.GetParameters() is { Length: 4 } args && args[0].ParameterType == typeof(int) &&
                args[1].ParameterType == typeof(Vec3f) && args[2].ParameterType == typeof(float) && !args[3].ParameterType.IsValueType));
            if (item.Writer) { MethodInfo writer = Writer(type); targets.Add(writer, (0, 1)); writers.Add(writer); }
        }
        var harmony = new Harmony(Owner); bool attempted = false;
        return new StartupPatchGroup("graphics-motion-instances", () =>
        {
            if (matrix.FieldType != typeof(float[])) throw new InvalidOperationException("Mechanical transform field changed.");
            foreach (var target in targets) Check(target.Key, PatchProcessor.GetOriginalInstructions(target.Key), target.Value);
            foreach (var identity in identities)
                if (identity.ReturnType != typeof(void) || identity.GetMethodBody() == null)
                    throw new InvalidOperationException("Mechanical device producer changed: " + identity);
        }, () =>
        {
            if (runtime != null) throw new InvalidOperationException("Instance routing already has an owner.");
            runtime = owner; transform = getter; expected = targets; attempted = true;
            foreach (var target in targets.Keys)
                harmony.Patch(target, transpiler: new HarmonyMethod(typeof(InstanceMotionConsumerPatches), nameof(Transpiler)) { priority = Priority.First });
            foreach (var identity in identities)
                harmony.Patch(identity, prefix: new HarmonyMethod(typeof(InstanceMotionConsumerPatches), nameof(DeviceEntering)),
                    finalizer: new HarmonyMethod(typeof(InstanceMotionConsumerPatches), nameof(DeviceLeaving)));
            foreach (var writer in writers)
                harmony.Patch(writer, postfix: new HarmonyMethod(typeof(InstanceMotionConsumerPatches), nameof(Written)));
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner);
            if (ReferenceEquals(runtime, owner)) { runtime = null; transform = null; expected = new(); }
            attempted = false;
        });
    }

    private static bool IsTwenty(CodeInstruction instruction) =>
        (instruction.opcode == OpCodes.Ldc_I4 || instruction.opcode == OpCodes.Ldc_I4_S) &&
        Convert.ToInt32(instruction.operand) == 20;
    private static void Check(MethodBase method, IEnumerable<CodeInstruction> instructions, (int Allocations, int Strides) counts)
    {
        var body = instructions.ToList();
        int strides = 0;
        for (int index = 0; index + 1 < body.Count; index++)
            if (IsTwenty(body[index]) && body[index + 1].opcode == OpCodes.Mul) strides++;
        if (body.Count(instruction => instruction.Calls(Allocation)) != counts.Allocations || strides != counts.Strides)
            throw new InvalidOperationException("Original/incoming instance storage anchors changed: " + method);
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var body = instructions.ToList(); Check(__originalMethod, body, expected[__originalMethod]);
        for (int index = 0; index < body.Count; index++)
        {
            var instruction = body[index];
            if (instruction.Calls(Allocation))
            { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(InstanceMotionConsumerPatches), nameof(Allocate)); }
            else if (index + 1 < body.Count && IsTwenty(instruction) && body[index + 1].opcode == OpCodes.Mul)
            { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(InstanceMotionConsumerPatches), nameof(Stride)); }
            yield return instruction;
        }
    }
    private static InstanceMotionHistory? History()
    {
        if (runtime?.Routing.RoutingEnabled != true) return null;
        if (ScreenManager.Platform is not ClientPlatformWindows platform || !runtime.TrySession(platform, out var session))
            throw new InvalidOperationException("Active mechanical instance routing lost its session.");
        return session.Temporal.InstanceMotion;
    }
    private static int Stride() => History() == null ? 20 : InstanceMotionHistory.InstanceFloats;
    private static void Allocate(CustomMeshDataPart<float> part, int count)
    {
        if (History() is not { } history) { part.SetAllocationSize(count); return; }
        if (part is not CustomMeshDataPartFloat || !part.Instanced || part.InterleaveStride != 80 ||
            count <= 0 || count % 20 != 0 || part.Count != 0 || part.Values.Length != count)
            throw new InvalidOperationException("Mechanical instance layout changed before upload.");
        // Preserve the object referenced by both the renderer field and MeshData.
        var expanded = history.CreateInstanceFloats(count / 20);
        part.Values = expanded.Values; part.InterleaveOffsets = expanded.InterleaveOffsets;
        part.InterleaveSizes = expanded.InterleaveSizes; part.InterleaveStride = expanded.InterleaveStride;
        part.SetAllocationSize(expanded.AllocationSize);
    }
    private sealed record DeviceScope(InstanceMotionHistory History, object? Previous);
    private static void DeviceEntering(object __3, out DeviceScope? __state)
    {
        __state = History() is { } history ? new DeviceScope(history, history.ExchangeDevice(__3)) : null;
    }
    private static Exception? DeviceLeaving(Exception? __exception, DeviceScope? __state)
    { if (__state != null) __state.History.ExchangeDevice(__state.Previous!); return __exception; }
    private static void Written(object __instance, float[] __0, int __1, Vec4f __3)
    {
        if (History() is { } history) history.WriteInstance(__0, __1, __3, transform!(__instance));
    }
}
