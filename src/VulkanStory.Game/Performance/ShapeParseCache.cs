using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.Common;

namespace VulkanStory.Game.Performance;

/// <summary>Private static-shape templates for fruiting-bush tessellation. Every caller receives independent mutable data.</summary>
internal static class ShapeParseCache
{
    private sealed record Entry(byte[] Bytes, string Domain, Shape Template);
    private static readonly object Gate = new();
    private static readonly Dictionary<IAsset, Entry> Entries = new(ReferenceEqualityComparer.Instance);
    private const int MaxEntries = 128;
    private const long MaxSnapshotBytes = 16 * 1024 * 1024;
    private static long snapshotBytes;
    private static readonly System.Func<object, object> Shallow = typeof(object)
        .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<System.Func<object, object>>();
    private static MethodInfo? target;

    private static bool IsRead(CodeInstruction instruction) => instruction.operand is MethodInfo method &&
        method.DeclaringType == typeof(IAssetManager) && method.Name == "Get" && method.IsGenericMethod &&
        method.GetGenericArguments().SequenceEqual([typeof(Shape)]) &&
        method.GetParameters().Select(p => p.ParameterType).SequenceEqual([typeof(AssetLocation)]);

    internal static PerformanceFeature Create(Assembly survival) => new("shape-parse-cache", s => s.ShapeParseCache, () =>
    {
        Type bush = survival.GetType("Vintagestory.GameContent.BEBehaviorFruitingBushMesh", true)!;
        var methods = bush.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Append(bush)
            .SelectMany(type => AccessTools.GetDeclaredMethods(type)).Where(method => method.GetMethodBody() != null &&
                PatchProcessor.GetOriginalInstructions(method).Any(IsRead)).ToArray();
        if (methods.Length != 1 || PatchProcessor.GetOriginalInstructions(methods[0]).Count(IsRead) != 1)
            throw new InvalidOperationException("Fruiting-bush shape read anchor changed.");
        target = methods[0];
    }, harmony => harmony.Patch(target!, transpiler: new HarmonyMethod(typeof(ShapeParseCache), nameof(Transpiler))),
        WorldLeft: Clear);
    private static void Clear() { lock (Gate) { Entries.Clear(); snapshotBytes = 0; } }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (IsRead(instruction))
            { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(ShapeParseCache), nameof(Read)); }
            yield return instruction;
        }
    }
    private static Shape Read(IAssetManager assets, AssetLocation location)
    {
        IAsset asset = assets.Get(location);
        if (PerformancePatchSet.Current?.ShapeParseCache != true || JsonConvert.DefaultSettings != null || asset.Data == null || asset.GetType() != typeof(Asset))
            return asset.ToObject<Shape>();
        Shape template;
        lock (Gate)
        {
            if (Entries.TryGetValue(asset, out var entry) && entry.Domain == asset.Location.Domain && asset.Data.AsSpan().SequenceEqual(entry.Bytes))
            {
                template = entry.Template;
                Interlocked.Increment(ref PerformanceCounters.ShapeParseHits);
            }
            else
            {
                // Parse the same owned bytes we keep for invalidation, even if a reload replaces Asset.Data concurrently.
                byte[] bytes = (byte[])asset.Data.Clone();
                string domain = asset.Location.Domain;
                template = new Asset(bytes, asset.Location.Clone(), asset.Origin).ToObject<Shape>();
                Interlocked.Increment(ref PerformanceCounters.ShapeParseMisses);
                if (template == null) return null!;
                // Animated or custom-deserialized shapes keep their complete original parsing path.
                if (template.GetType() != typeof(Shape) || template.Animations is { Length: > 0 } || !Ordinary(template.Elements)) return template;
                if (Entries.Remove(asset, out var previous)) snapshotBytes -= previous.Bytes.Length;
                if (bytes.Length > MaxSnapshotBytes) return template;
                if (Entries.Count >= MaxEntries || snapshotBytes + bytes.Length > MaxSnapshotBytes)
                { Entries.Clear(); snapshotBytes = 0; }
                Entries.Add(asset, new(bytes, domain, template)); snapshotBytes += bytes.Length;
            }
        }
        return Copy(template);
    }
    private static bool Ordinary(ShapeElement[]? elements) => elements == null || elements.All(element =>
        element.GetType() == typeof(ShapeElement) && Ordinary(element.Children) &&
        (element.FacesResolved == null || element.FacesResolved.All(face => face == null || face.GetType() == typeof(ShapeElementFace))) &&
        (element.AttachmentPoints == null || element.AttachmentPoints.All(point => point.GetType() == typeof(AttachmentPoint))));
    private static Shape Copy(Shape source)
    {
        Shape result = (Shape)Shallow(source);
        result.Elements = source.Elements?.Select(CopyElement).ToArray();
        result.Animations = source.Animations == null ? null : [];
        result.AnimationsByCrc32 = new(); result.JointsById = new();
        result.Textures = source.Textures?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), source.Textures.Comparer);
        result.TextureSizes = source.TextureSizes?.ToDictionary(pair => pair.Key, pair => (int[])pair.Value.Clone(), source.TextureSizes.Comparer);
        return result;
    }
    private static ShapeElement CopyElement(ShapeElement source)
    {
        ShapeElement result = (ShapeElement)Shallow(source);
        result.From = source.From == null ? null : (double[])source.From.Clone();
        result.To = source.To == null ? null : (double[])source.To.Clone();
        result.RotationOrigin = source.RotationOrigin == null ? null : (double[])source.RotationOrigin.Clone();
        result.inverseModelTransform = source.inverseModelTransform == null ? null : (float[])source.inverseModelTransform.Clone();
        result.ParentElement = null; // Fresh deserialization has not resolved parents/joints yet.
        result.Children = source.Children?.Select(CopyElement).ToArray();
        result.Faces = null; // Original OnDeserialized resolves these into FacesResolved and clears Faces.
        result.FacesResolved = source.FacesResolved?.Select(face => face == null ? null : CopyFace(face)).ToArray();
        result.AttachmentPoints = source.AttachmentPoints?.Select(point =>
        {
            var copy = (AttachmentPoint)Shallow(point); copy.ParentElement = null; return copy;
        }).ToArray();
        return result;
    }
    private static ShapeElementFace CopyFace(ShapeElementFace source)
    {
        ShapeElementFace result = (ShapeElementFace)Shallow(source);
        result.Uv = source.Uv == null ? null : (float[])source.Uv.Clone();
        result.WindMode = source.WindMode == null ? null : (sbyte[])source.WindMode.Clone();
        result.WindData = source.WindData == null ? null : (sbyte[])source.WindData.Clone();
        return result;
    }
}
