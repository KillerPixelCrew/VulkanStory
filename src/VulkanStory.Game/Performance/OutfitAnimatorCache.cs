using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace VulkanStory.Game.Performance;

/// <summary>Optimum's outfit animator build cache. Immutable pose layouts are shared by outfit; mutable poses belong to
/// each entity. Fresh shape elements, animations and joint maps are always bound after the original InitForAnimations.</summary>
internal static class OutfitAnimatorCache
{
    private sealed record Layout(Layout[] Children);
    private sealed class Owned { internal string? Key; internal List<ElementPose>? Poses; }
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Layout[]> Layouts = new(StringComparer.Ordinal);
    private static ConditionalWeakTable<Entity, Owned> entities = new();
    private static Type? dressed;
    private static PropertyInfo? config, codes, slots;
    private static readonly MethodInfo Factory = AccessTools.Method(typeof(ClientAnimator), nameof(ClientAnimator.CreateForEntity),
        [typeof(Entity), typeof(Animation[]), typeof(ShapeElement[]), typeof(Dictionary<int, AnimationJoint>)])!;
    private static MethodInfo Target() => AccessTools.Method(typeof(AnimationManager), nameof(AnimationManager.LoadAnimator))!;

    internal static PerformanceFeature Create(Assembly survival) => new("outfit-animator-cache", s => s.OutfitAnimatorCache, () =>
    {
        dressed = survival.GetType("Vintagestory.GameContent.EntityDressedHumanoid", true)!;
        config = AccessTools.Property(dressed, "OutfitConfigFileName");
        codes = AccessTools.Property(dressed, "OutfitCodes"); slots = AccessTools.Property(dressed, "OutfitSlots");
        if (config == null || codes == null || slots == null || Factory == null ||
            PatchProcessor.GetOriginalInstructions(Target()).Count(i => i.Calls(Factory)) != 1)
            throw new InvalidOperationException("Outfit animator build anchors changed.");
    }, harmony => harmony.Patch(Target(), transpiler: new HarmonyMethod(typeof(OutfitAnimatorCache), nameof(Transpiler))),
        WorldLeft: Clear);

    private static void Clear()
    {
        lock (Gate) Layouts.Clear();
        entities = new();
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(Factory)) instruction.operand = AccessTools.Method(typeof(OutfitAnimatorCache), nameof(CreateAnimator));
            yield return instruction;
        }
    }
    private static ClientAnimator CreateAnimator(Entity entity, Animation[] animations, ShapeElement[] elements, Dictionary<int, AnimationJoint> joints)
    {
        if (PerformancePatchSet.Current?.OutfitAnimatorCache != true || dressed?.IsInstanceOfType(entity) != true)
            return ClientAnimator.CreateForEntity(entity, animations, elements, joints);
        string[]? outfit = codes!.GetValue(entity) as string[];
        if (outfit == null) return ClientAnimator.CreateForEntity(entity, animations, elements, joints);
        var key = new StringBuilder();
        key.Append(entity.Code).Append('\u001f').Append(config!.GetValue(entity)).Append('\u001f');
        foreach (string slot in slots!.GetValue(entity) as string[] ?? []) key.Append(slot?.Length ?? 0).Append(':').Append(slot);
        key.Append('\u001f');
        foreach (string code in outfit) key.Append(code?.Length ?? 0).Append(':').Append(code);
        // Pose topology is independent of element geometry. Include names and child structure so behavior-added bones
        // and asset reloads cannot reuse a mismatched layout; every pose references the NEW shape's element below.
        Topology(elements, key);
        string signature = key.ToString();
        Layout[] layout;
        lock (Gate)
        {
            if (!Layouts.TryGetValue(signature, out layout!))
            {
                if (Layouts.Count >= 128) Layouts.Clear();
                Layouts.Add(signature, layout = Describe(elements));
            }
        }
        Owned owner = entities.GetOrCreateValue(entity);
        if (owner.Key != signature || owner.Poses == null)
        {
            owner.Key = signature; owner.Poses = Instantiate(layout, elements);
        }
        else Rebind(owner.Poses, elements);
        // Constructor rebuilds attachment-point bindings and per-animator matrices/state; none is shared between entities.
        return ClientAnimator.CreateForEntity(entity, owner.Poses, animations, elements, joints);
    }
    private static void Topology(ShapeElement[] elements, StringBuilder key)
    {
        key.Append('[').Append(elements.Length).Append(':');
        foreach (ShapeElement element in elements)
        {
            key.Append(element.Name?.Length ?? 0).Append(':').Append(element.Name);
            Topology(element.Children ?? [], key);
        }
        key.Append(']');
    }
    private static Layout[] Describe(ShapeElement[] elements)
    {
        var result = new Layout[elements.Length];
        for (int i = 0; i < result.Length; i++) result[i] = new(Describe(elements[i].Children ?? []));
        return result;
    }
    private static List<ElementPose> Instantiate(Layout[] layout, ShapeElement[] elements)
    {
        var poses = new List<ElementPose>(layout.Length);
        for (int i = 0; i < layout.Length; i++)
            poses.Add(new ElementPose { ForElement = elements[i], AnimModelMatrix = Mat4f.Create(),
                ChildElementPoses = Instantiate(layout[i].Children, elements[i].Children ?? []) });
        return poses;
    }
    private static void Rebind(List<ElementPose> poses, ShapeElement[] elements)
    {
        for (int i = 0; i < poses.Count; i++)
        {
            ElementPose pose = poses[i];
            pose.ForElement = elements[i]; pose.Clear();
            pose.degOffX = pose.degOffY = pose.degOffZ = 0;
            pose.RotShortestDistanceX = pose.RotShortestDistanceY = pose.RotShortestDistanceZ = false;
            Mat4f.Identity(pose.AnimModelMatrix);
            Rebind(pose.ChildElementPoses, elements[i].Children ?? []);
        }
    }
}
