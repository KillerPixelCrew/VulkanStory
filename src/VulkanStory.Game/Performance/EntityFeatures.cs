using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace VulkanStory.Game.Performance;

/// <summary>E1: the per-entity collision-box animation check walks the dictionary with its struct enumerator instead of LINQ's boxed one.</summary>
/// <remarks>The original predicate is still invoked for every entry, so the result is identical.</remarks>
internal static class AnimationLinqFree
{
    private static MethodInfo[] Targets() =>
    [
        AccessTools.Method(typeof(AnimationManager), nameof(AnimationManager.OnClientFrame), [typeof(float)]) ??
            throw new MissingMethodException(typeof(AnimationManager).FullName, "OnClientFrame"),
        AccessTools.Method(typeof(AnimationManager), nameof(AnimationManager.OnServerTick), [typeof(float)]) ??
            throw new MissingMethodException(typeof(AnimationManager).FullName, "OnServerTick"),
    ];
    private static bool IsAnimationAny(CodeInstruction instruction) =>
        instruction.opcode == OpCodes.Call && instruction.operand is MethodInfo { Name: nameof(Enumerable.Any), IsGenericMethod: true } method &&
        method.DeclaringType == typeof(Enumerable) && method.GetParameters().Length == 2 &&
        method.GetGenericArguments()[0] == typeof(KeyValuePair<string, AnimationMetaData>);

    internal static PerformanceFeature Create() => new("animation-linq-free", settings => settings.AnimationLinqFree, () =>
    {
        foreach (var target in Targets())
            if (PatchProcessor.GetOriginalInstructions(target).Count(IsAnimationAny) != 1)
                throw new InvalidOperationException("Animation collision-box check anchor changed: " + target.Name);
    }, harmony =>
    {
        foreach (var target in Targets()) harmony.Patch(target, transpiler: new HarmonyMethod(typeof(AnimationLinqFree), nameof(Transpiler)));
    });

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (IsAnimationAny(instruction)) instruction.operand = AccessTools.Method(typeof(AnimationLinqFree), nameof(Any));
            yield return instruction;
        }
    }

    private static bool Any(IEnumerable<KeyValuePair<string, AnimationMetaData>> source, System.Func<KeyValuePair<string, AnimationMetaData>, bool> predicate)
    {
        if (PerformancePatchSet.Current?.AnimationLinqFree != true || source is not Dictionary<string, AnimationMetaData> active)
            return source.Any(predicate);
        foreach (var pair in active) if (predicate(pair)) return true;
        return false;
    }
}

/// <summary>E2: fixes the original falling-block mesh path, which appends every tessellation into one shared buffer, can alias the
/// tessellator's shared default block mesh, and uploads an <c>IFallingBlockMeshSource</c> mesh twice (leaking the first reference).</summary>
/// <remarks>A fresh buffer per call fixes both the accumulation and the aliasing, since the shared default mesh is then never appended to.
/// The second of the three original uploads is the duplicate; the final upload stores the same mesh into the cache.</remarks>
internal static class FallingBlockMeshFix
{
    private static readonly MethodInfo Upload = AccessTools.Method(typeof(IRenderAPI), nameof(IRenderAPI.UploadMultiTextureMesh), [typeof(MeshData)])!;
    private static FieldInfo? meshField;

    internal static PerformanceFeature Create(Assembly essentials)
    {
        MethodInfo? target = null;
        return new("falling-block-mesh-fix", settings => settings.FallingBlockMeshFix, () =>
        {
            Type system = essentials.GetType("Vintagestory.GameContent.ModSystemRenderFallingBlocksFast", true)!;
            target = AccessTools.Method(system, "genMesh") ?? throw new MissingMethodException(system.FullName, "genMesh");
            FieldInfo mesh = AccessTools.Field(system, "mesh") ?? throw new MissingFieldException(system.FullName, "mesh");
            if (mesh.FieldType != typeof(MeshData) || target.ReturnType != typeof(void))
                throw new InvalidOperationException("Original falling-block mesh members changed.");
            if (PatchProcessor.GetOriginalInstructions(target).Count(instruction => instruction.Calls(Upload)) != 3)
                throw new InvalidOperationException("Original falling-block upload anchors changed.");
            meshField = mesh;
        }, harmony => harmony.Patch(target!,
            prefix: new HarmonyMethod(typeof(FallingBlockMeshFix), nameof(Prefix)),
            transpiler: new HarmonyMethod(typeof(FallingBlockMeshFix), nameof(Transpiler))));
    }

    private static void Prefix(object __instance)
    {
        // Same layout as the original constructor's buffer.
        if (PerformancePatchSet.Current?.FallingBlockMeshFix != true) return;
        meshField!.SetValue(__instance, new MeshData(4, 3, false, true, true, true));
        Interlocked.Increment(ref PerformanceCounters.FallingMeshesBuilt);
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        int seen = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(Upload) && ++seen == 2)
            { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(FallingBlockMeshFix), nameof(DuplicateUpload)); }
            yield return instruction;
        }
        if (seen != 3) throw new InvalidOperationException("Incoming falling-block upload anchors changed.");
    }

    private static MultiTextureMeshRef? DuplicateUpload(IRenderAPI render, MeshData mesh)
    {
        if (PerformancePatchSet.Current?.FallingBlockMeshFix != true) return render.UploadMultiTextureMesh(mesh);
        // The method's final statement uploads this same mesh into the cache and the entity.
        Interlocked.Increment(ref PerformanceCounters.FallingMeshUploadsSaved);
        return null;
    }
}

/// <summary>E3: caches the assembled outfit shape and texture set of villagers/traders.</summary>
/// <remarks>
/// The cache stores the shape and textures right before the original hands them to <c>Entity.OnTesselation(ref Shape, string, ref bool)</c>,
/// so behaviour-driven edits after that point still run on every tessellation. Hits return a clone. An entry is only reused for the
/// same incoming base shape instance, so asset reloads miss naturally. Outfit textures are pre-inserted gradually by
/// <see cref="OutfitTexturePrewarm" />.
/// </remarks>
internal static class OutfitShapeCache
{
    private const int MaxEntries = 512;
    private delegate void BaseTesselate(Entity entity, ref Shape shape, string path, ref bool cloned);
    private sealed record Entry(Shape Source, Shape Shape, KeyValuePair<string, CompositeTexture>[] Textures);
    private sealed class Pending { internal string? Key; internal Entity? Owner; internal Shape? Source; }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    [ThreadStatic] private static Pending? pending;
    private static BaseTesselate? baseTesselate;
    private static System.Func<Entity, AssetLocation?>? configName;
    private static System.Func<Entity, string[]?>? slots, codes;
    private static Type? dressed;

    private static MethodInfo BaseMethod() => AccessTools.Method(typeof(Entity), "OnTesselation",
        [typeof(Shape).MakeByRefType(), typeof(string), typeof(bool).MakeByRefType()]) ??
        throw new MissingMethodException(typeof(Entity).FullName, "OnTesselation(ref Shape, string, ref bool)");

    internal static PerformanceFeature Create(Assembly survival)
    {
        MethodInfo? target = null;
        return new("outfit-shape-cache", settings => settings.OutfitShapeCache, () =>
        {
            dressed = survival.GetType("Vintagestory.GameContent.EntityDressedHumanoid", true)!;
            target = AccessTools.Method(dressed, "OnTesselation", [typeof(Shape).MakeByRefType(), typeof(string)]) ??
                throw new MissingMethodException(dressed.FullName, "OnTesselation");
            MethodInfo inner = BaseMethod();
            if (PatchProcessor.GetOriginalInstructions(target).Count(instruction => instruction.opcode == OpCodes.Call && Equals(instruction.operand, inner)) != 1)
                throw new InvalidOperationException("Outfit tessellation hand-off anchor changed.");
            configName = Getter<AssetLocation?>(dressed, "OutfitConfigFileName");
            slots = Getter<string[]?>(dressed, "OutfitSlots");
            codes = Getter<string[]?>(dressed, "OutfitCodes");
            baseTesselate = NonVirtual(inner);
        }, harmony => harmony.Patch(target!,
            prefix: new HarmonyMethod(typeof(OutfitShapeCache), nameof(Prefix)),
            transpiler: new HarmonyMethod(typeof(OutfitShapeCache), nameof(Transpiler))),
        WorldReady: OutfitTexturePrewarm.Start, WorldLeft: () => { Clear(); OutfitTexturePrewarm.Stop(); });
    }

    private static System.Func<Entity, T> Getter<T>(Type type, string name)
    {
        PropertyInfo property = AccessTools.Property(type, name) ?? throw new MissingMemberException(type.FullName, name);
        var entity = Expression.Parameter(typeof(Entity));
        return Expression.Lambda<System.Func<Entity, T>>(Expression.Convert(
            Expression.Property(Expression.Convert(entity, type), property), typeof(T)), entity).Compile();
    }

    /// <summary>The original calls the protected base overload non-virtually; reproduce that exactly.</summary>
    private static BaseTesselate NonVirtual(MethodInfo method)
    {
        var dynamic = new DynamicMethod("VulkanStoryOutfitBaseTesselate", typeof(void),
            [typeof(Entity), typeof(Shape).MakeByRefType(), typeof(string), typeof(bool).MakeByRefType()], typeof(Entity), true);
        var il = dynamic.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Call, method); il.Emit(OpCodes.Ret);
        return (BaseTesselate)dynamic.CreateDelegate(typeof(BaseTesselate));
    }

    private static string? Key(Entity entity, string path)
    {
        string[]? outfitCodes = codes!(entity), outfitSlots = slots!(entity);
        if (outfitCodes == null || entity.Code == null) return null;
        return string.Join('\u001f', entity.Code.ToString(), path ?? "", configName!(entity)?.ToString() ?? "",
            string.Join('\u001e', outfitSlots ?? []), string.Join('\u001e', outfitCodes));
    }

    private static bool Prefix(Entity __instance, ref Shape entityShape, string shapePathForLogging)
    {
        if (PerformancePatchSet.Current?.OutfitShapeCache != true || __instance.Api is not ICoreClientAPI || entityShape == null) return true;
        string? key = Key(__instance, shapePathForLogging);
        if (key == null) return true;
        Entry? entry;
        lock (Gate) Entries.TryGetValue(key, out entry);
        if (entry != null && ReferenceEquals(entry.Source, entityShape))
        {
            var textures = new FastSmallDictionary<string, CompositeTexture>(entry.Textures.Length);
            foreach (var (name, texture) in entry.Textures) textures[name] = texture;
            __instance.Properties.Client.Textures = textures;
            entityShape = entry.Shape.Clone();
            bool cloned = true;
            baseTesselate!(__instance, ref entityShape, shapePathForLogging, ref cloned);
            Interlocked.Increment(ref PerformanceCounters.OutfitShapeHits);
            return false;
        }
        var current = pending ??= new Pending();
        current.Key = key; current.Owner = __instance; current.Source = entityShape;
        Interlocked.Increment(ref PerformanceCounters.OutfitShapeMisses);
        return true;
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo inner = BaseMethod();
        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Call && Equals(instruction.operand, inner))
                instruction.operand = AccessTools.Method(typeof(OutfitShapeCache), nameof(HandOff));
            yield return instruction;
        }
    }

    // Replaces the original's non-virtual base call: stores a pending miss, then performs that same call.
    private static void HandOff(Entity entity, ref Shape shape, string path, ref bool cloned)
    {
        if (pending is { Key: { } key, Source: { } source } current && ReferenceEquals(current.Owner, entity))
        {
            var textures = entity.Properties.Client.Textures?.ToArray() ?? [];
            lock (Gate)
            {
                if (Entries.Count >= MaxEntries) Entries.Clear();
                Entries[key] = new Entry(source, shape.Clone(), textures);
            }
            current.Key = null; current.Owner = null; current.Source = null;
        }
        baseTesselate!(entity, ref shape, path, ref cloned);
    }

    private static void Clear() { lock (Gate) Entries.Clear(); }
}

/// <summary>E3 prewarm: inserts every outfit texture of dressed-humanoid entity types into the entity atlas after a world loads,
/// spread over game ticks within a small time budget, so a villager or trader first coming into view needs no atlas insert.</summary>
/// <remarks>The atlas API inserts one texture at a time and each insert costs milliseconds, so an up-front batch stalls world entry.
/// Discovery is lazy as well: configs and gear shapes load as the walk reaches them. Runs on the client main thread inside the
/// game frame, like the original's own lazy inserts.</remarks>
internal static class OutfitTexturePrewarm
{
    private const double BudgetMilliseconds = 1;
    private static ICoreClientAPI? api;
    private static IEnumerator<AssetLocation>? pending;
    private static long listener = -1;
    private static readonly System.Diagnostics.Stopwatch Spent = new();

    internal static void Start(ICoreClientAPI client)
    {
        Stop();
        if (PerformancePatchSet.Current?.OutfitShapeCache != true) return;
        api = client;
        pending = Textures(client).GetEnumerator();
        Spent.Reset();
        listener = client.Event.RegisterGameTickListener(Step, 0);
    }

    internal static void Stop()
    {
        if (listener >= 0) api?.Event.UnregisterGameTickListener(listener);
        listener = -1; pending?.Dispose(); pending = null; api = null;
    }

    private static void Step(float dt)
    {
        if (api == null || pending == null) return;
        if (PerformancePatchSet.Current?.OutfitShapeCache != true) { Stop(); return; }
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        Spent.Start();
        try
        {
            // At least one texture per tick; more while the budget lasts.
            do
            {
                if (!pending.MoveNext())
                {
                    api.Logger.Notification("VulkanStory: prewarmed {0} outfit textures in {1:0} ms of tick time.",
                        Interlocked.Read(ref PerformanceCounters.OutfitTexturesPrewarmed), Spent.Elapsed.TotalMilliseconds);
                    Stop();
                    return;
                }
                var texture = new CompositeTexture(pending.Current);
                texture.Bake(api.Assets);
                if (texture.Baked?.TextureFilenames is { Length: > 0 } files &&
                    api.EntityTextureAtlas.GetOrInsertTexture(new AssetLocationAndSource(files[0], "VulkanStory outfit prewarm of", pending.Current), out _, out _))
                    Interlocked.Increment(ref PerformanceCounters.OutfitTexturesPrewarmed);
            }
            while (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds < BudgetMilliseconds);
        }
        catch (Exception error)
        {
            api.Logger.Warning("VulkanStory: outfit texture prewarm stopped: {0}", error.Message);
            Stop();
        }
        finally { Spent.Stop(); }
    }

    /// <summary>Walks dressed-humanoid entity types, their outfit configs and gear shapes, yielding each distinct texture once.</summary>
    private static IEnumerable<AssetLocation> Textures(ICoreClientAPI client)
    {
        Type? dressed = AccessTools.TypeByName("Vintagestory.GameContent.EntityDressedHumanoid");
        if (dressed == null || client.ModLoader.GetModSystem("Vintagestory.GameContent.HumanoidOutfits") is not { } outfits) yield break;
        MethodInfo? getConfig = AccessTools.Method(outfits.GetType(), "GetConfig", [typeof(string)]);
        if (getConfig == null) yield break;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var configs = new HashSet<string>(StringComparer.Ordinal);
        var classes = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (EntityProperties type in client.World.EntityTypes.ToArray())
        {
            if (type?.Class == null || type.Code == null) continue;
            // The API has no class-to-type lookup; one unattached instance per class only runs constructors.
            if (!classes.TryGetValue(type.Class, out bool isDressed))
                classes[type.Class] = isDressed = dressed.IsInstanceOfType(client.ClassRegistry.CreateEntity(type.Class));
            if (!isDressed) continue;
            string config = AssetLocation.Create(type.Attributes?["outfitConfigFileName"].AsString("traderaccessories") ?? "traderaccessories",
                type.Code.Domain).ToString();
            if (!configs.Add(config)) continue;
            if (getConfig.Invoke(outfits, [config]) is not { } properties ||
                AccessTools.Field(properties.GetType(), "Variants")?.GetValue(properties) is not System.Collections.IDictionary variants) continue;
            foreach (object? variant in variants.Values.Cast<object?>().ToArray())
            {
                if (variant is not CompositeShape shape) continue;
                foreach (string field in new[] { "Textures", "OverrideTextures" })
                    if (AccessTools.Field(variant.GetType(), field)?.GetValue(variant) is IDictionary<string, AssetLocation> textures)
                        foreach (var location in textures.Values.ToArray())
                            if (seen.Add(location.ToString())) yield return location;
                if (shape.Base == null) continue;
                AssetLocation path = shape.Base.CopyWithPathPrefixAndAppendixOnce("shapes/", ".json");
                if (!seen.Add("shape:" + path) || Shape.TryGet(client, path)?.Textures is not { } gear) continue;
                foreach (var location in gear.Values.ToArray())
                    if (seen.Add(location.ToString())) yield return location;
            }
        }
    }
}
