using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace VulkanStory.Game;

/// <summary>Harness-only stand-in for a mod block that supplies its own falling-block mesh (<c>IFallingBlockMeshSource</c>).</summary>
/// <remarks>
/// No stock block implements that interface, so the falling-block renderer's mesh-source branch only runs with mods. Once armed by
/// the <c>!fallingsource</c> directive, every falling block's block-entity lookup made inside the renderer's <c>genMesh</c> returns a
/// runtime-emitted block entity implementing the interface: a unique cache key per fall, and the block's default mesh as its
/// tessellation. Public because the emitted type calls back into it from a dynamic assembly.
/// </remarks>
public static class HeadlessFallingMeshSource
{
    private const string Owner = "vulkanstory.harness.falling-mesh-source";
    [ThreadStatic] private static bool insideGenMesh;
    private static BlockEntity? standIn;
    private static ICoreClientAPI? client;
    private static PropertyInfo? fallingBlock;
    private static int keys;
    /// <summary>Times the stand-in tessellated a falling block (harness diagnostics).</summary>
    public static int Tessellations;

    internal static void Arm(ICoreClientAPI api)
    {
        if (!HeadlessHarnessOptions.Enabled) throw new InvalidOperationException("The falling mesh-source stand-in is harness-only.");
        if (standIn != null) return;
        client = api;
        Assembly essentials = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "VSEssentials");
        Type source = essentials.GetType("Vintagestory.GameContent.IFallingBlockMeshSource", true)!;
        Type falling = essentials.GetType("Vintagestory.GameContent.EntityBlockFalling", true)!;
        Type renderer = essentials.GetType("Vintagestory.GameContent.ModSystemRenderFallingBlocksFast", true)!;
        fallingBlock = AccessTools.Property(falling, "Block") ?? throw new MissingMemberException(falling.FullName, "Block");
        standIn = (BlockEntity)Activator.CreateInstance(Emit(source, falling))!;
        var harmony = new Harmony(Owner);
        harmony.Patch(AccessTools.Method(renderer, "genMesh") ?? throw new MissingMethodException(renderer.FullName, "genMesh"),
            prefix: new HarmonyMethod(typeof(HeadlessFallingMeshSource), nameof(Enter)),
            finalizer: new HarmonyMethod(typeof(HeadlessFallingMeshSource), nameof(Leave)));
        // The non-generic overload (a generic GetBlockEntity<T>(BlockPos) shares its parameter list), taken from the type that
        // declares it: Harmony patches only declared implementations.
        static bool NonGeneric(MethodInfo method) => method.Name == nameof(IBlockAccessor.GetBlockEntity) && !method.IsGenericMethodDefinition &&
            method.GetParameters() is [{ ParameterType: var parameter }] && parameter == typeof(BlockPos);
        Type declaring = api.World.BlockAccessor.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).Single(NonGeneric).DeclaringType!;
        MethodInfo lookup = declaring.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Single(NonGeneric);
        harmony.Patch(lookup, prefix: new HarmonyMethod(typeof(HeadlessFallingMeshSource), nameof(Substitute)));
        api.Logger.Notification("VulkanStory headless: falling mesh-source stand-in armed.");
    }

    /// <summary>Emits <c>BlockEntity</c> + <c>IFallingBlockMeshSource</c> whose members call back into this class.</summary>
    private static Type Emit(Type source, Type falling)
    {
        var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("VulkanStoryHarnessFallingSource"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("VulkanStoryHarnessFallingSource");
        TypeBuilder type = module.DefineType("VulkanStoryHarnessFallingSource", TypeAttributes.Public | TypeAttributes.Class,
            typeof(BlockEntity), [source]);
        type.DefineDefaultConstructor(MethodAttributes.Public);
        const MethodAttributes Implementation = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final |
            MethodAttributes.HideBySig | MethodAttributes.NewSlot;
        MethodBuilder key = type.DefineMethod("GetCacheKey", Implementation, typeof(string), [falling]);
        var il = key.GetILGenerator();
        il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Call, typeof(HeadlessFallingMeshSource).GetMethod(nameof(CacheKey))!); il.Emit(OpCodes.Ret);
        type.DefineMethodOverride(key, source.GetMethod("GetCacheKey")!);
        MethodBuilder tessellate = type.DefineMethod("OnFallingBlockTesselation", Implementation, typeof(void),
            [falling, typeof(ITerrainMeshPool), typeof(ITesselatorAPI)]);
        il = tessellate.GetILGenerator();
        il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, typeof(HeadlessFallingMeshSource).GetMethod(nameof(Tessellate))!); il.Emit(OpCodes.Ret);
        type.DefineMethodOverride(tessellate, source.GetMethod("OnFallingBlockTesselation")!);
        // Unplaced stand-in: its ordinary terrain tessellation contributes nothing (the base walks behaviours it does not have).
        MethodBuilder terrain = type.DefineMethod(nameof(BlockEntity.OnTesselation), MethodAttributes.Public | MethodAttributes.Virtual |
            MethodAttributes.HideBySig, typeof(bool), [typeof(ITerrainMeshPool), typeof(ITesselatorAPI)]);
        il = terrain.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
        return type.CreateType()!;
    }

    private static void Enter() => insideGenMesh = true;

    private static Exception? Leave(Exception? __exception) { insideGenMesh = false; return __exception; }

    private static bool Substitute(ref BlockEntity __result)
    {
        if (!insideGenMesh || standIn == null) return true;
        __result = standIn;
        return false;
    }

    /// <summary>A unique key per fall, so every fall reaches the mesh-source branch instead of the mesh cache.</summary>
    public static string CacheKey(object entity) => "vulkanstory-harness-falling-" + Interlocked.Increment(ref keys);

    /// <summary>Supplies the falling block's default mesh, as a simple mod source would.</summary>
    public static void Tessellate(object entity, ITerrainMeshPool mesher)
    {
        if (client == null || fallingBlock?.GetValue(entity) is not Block block) return;
        mesher.AddMeshData(client.TesselatorManager.GetDefaultBlockMesh(block));
        Interlocked.Increment(ref Tessellations);
    }
}
