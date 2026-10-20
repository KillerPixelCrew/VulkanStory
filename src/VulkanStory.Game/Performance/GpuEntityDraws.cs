using Vintagestory.API.Common.Entities;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;

namespace VulkanStory.Game.Performance;

internal static class GpuEntityDraws
{
    private static GameGraphicsAdapter? graphics;
    internal static bool Armed { get; private set; }
    private static readonly HashSet<Assembly> stockRendererAssemblies = new();
    internal static bool OwnsRenderer(Type type) => stockRendererAssemblies.Contains(type.Assembly);
    internal static void Attach(GameGraphicsAdapter? adapter) => graphics = adapter;
    internal static bool Handles(Entity entity) => graphics?.CanGpuCullEntity(entity) == true;
    private static readonly MethodInfo FallingRender = AccessTools.DeclaredMethod(typeof(ModSystemRenderFallingBlocksFast),
        "OnRenderFrame", [typeof(float), typeof(EnumRenderStage)])!;
    private static readonly MethodInfo FallingAdmission = AccessTools.DeclaredMethod(typeof(ModSystemRenderFallingBlocksFast),
        "IsRendered", [typeof(EntityBlockFalling)])!;
    private static readonly MethodInfo FallingMesh = AccessTools.DeclaredMethod(typeof(ModSystemRenderFallingBlocksFast),
        "genMesh", [typeof(EntityBlockFalling)])!;
    private static readonly MethodInfo Sphere = AccessTools.Method(typeof(FrustumCulling), "SphereInFrustum",
        [typeof(double), typeof(double), typeof(double), typeof(double)])!;
    private static readonly MethodInfo[] FallingMethods = [FallingRender, FallingAdmission, FallingMesh, Sphere];
    [ThreadStatic] private static ModSystemRenderFallingBlocksFast? fallingFrame;

    // No frame-cached permission: a foreign patch installed between calls must
    // immediately restore the stock sphere test as well as direct submission.
    internal static bool OwnsFallingFrame(object renderer)
    {
        if (!Armed || !ReferenceEquals(fallingFrame, renderer) || renderer.GetType() != typeof(ModSystemRenderFallingBlocksFast))
            return false;
        foreach (var method in FallingMethods)
            if (method == null || Harmony.GetPatchInfo(method)?.Owners.Any(owner => owner is not
                ("vulkanstory.performance.gpu-entity-draws" or "vulkanstory.performance.falling-block-mesh-fix" or
                 "vulkanstory.performance.simd-frustum-tests" or
                 "vulkanstory.routing.graphics-motion-dropped-falling")) == true) return false;
        return true;
    }

    private static void ValidateFalling()
    {
        if (FallingRender?.ReturnType != typeof(void) || FallingAdmission?.ReturnType != typeof(bool) ||
            FallingMesh?.ReturnType != typeof(void) ||
            FallingRender.DeclaringType?.Assembly != typeof(EntityBlockFalling).Assembly)
            throw new InvalidOperationException("Stock falling-block visibility members changed.");
        CheckFalling(PatchProcessor.GetOriginalInstructions(FallingAdmission));
        var render = PatchProcessor.GetOriginalInstructions(FallingRender).ToList();
        if (render.Count(i => i.Calls(FallingAdmission)) != 1 || render.Count(i => i.Calls(FallingMesh)) != 1 ||
            render.FindIndex(i => i.Calls(FallingAdmission)) >= render.FindIndex(i => i.Calls(FallingMesh)))
            throw new InvalidOperationException("Stock falling-block admission/generation order changed.");
    }

    private static void CheckFalling(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList();
        int at = body.FindIndex(i => i.Calls(Sphere));
        if (at < 1 || body.Count(i => i.Calls(Sphere)) != 1 || at + 1 >= body.Count ||
            (body[at + 1].opcode != OpCodes.Brtrue && body[at + 1].opcode != OpCodes.Brtrue_S))
            throw new InvalidOperationException("Stock falling-block sphere anchor changed.");
    }

    private static IEnumerable<CodeInstruction> FallingTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList(); CheckFalling(body);
        foreach (var instruction in body)
        {
            if (instruction.Calls(Sphere))
            {
                var load = new CodeInstruction(OpCodes.Ldarg_0);
                load.labels.AddRange(instruction.labels); instruction.labels.Clear();
                load.blocks.AddRange(instruction.blocks.Where(b => b.blockType != ExceptionBlockType.EndExceptionBlock));
                instruction.blocks.RemoveAll(b => b.blockType != ExceptionBlockType.EndExceptionBlock);
                yield return load;
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(GpuEntityDraws), nameof(FallingSphere));
            }
            yield return instruction;
        }
    }

    private static bool FallingSphere(FrustumCulling culler, double x, double y, double z, double radius,
        ModSystemRenderFallingBlocksFast renderer, EntityBlockFalling entity) =>
        (OwnsFallingFrame(renderer) && graphics?.CanGpuCullFalling(entity, culler) == true) ||
        culler.SphereInFrustum(x, y, z, radius);

    private static void FallingPrefix(ModSystemRenderFallingBlocksFast __instance, EnumRenderStage __1,
        out ModSystemRenderFallingBlocksFast? __state)
    {
        __state = fallingFrame;
        fallingFrame = __1 == EnumRenderStage.Opaque ? __instance : null;
    }

    private static void FallingFinalizer(ModSystemRenderFallingBlocksFast? __state) => fallingFrame = __state;

    internal static PerformanceFeature Create(Assembly essentials, Assembly survival) => new("gpu-entity-draws",
        settings => settings.GpuEntityDrawPreparation, () =>
        { GameGraphicsAdapter.ValidateEntityVisibilityBindings(); ValidateFalling(); }, harmony =>
        {
            harmony.Patch(FallingAdmission, transpiler: new HarmonyMethod(typeof(GpuEntityDraws), nameof(FallingTranspiler)));
            harmony.Patch(FallingRender, prefix: new HarmonyMethod(typeof(GpuEntityDraws), nameof(FallingPrefix)),
                finalizer: new HarmonyMethod(typeof(GpuEntityDraws), nameof(FallingFinalizer)));
            stockRendererAssemblies.Add(typeof(EntityShapeRenderer).Assembly);
            stockRendererAssemblies.Add(essentials); stockRendererAssemblies.Add(survival);
            Armed = true;
        }, Uninstall: () => { Armed = false; fallingFrame = null; stockRendererAssemblies.Clear(); },
        WorldLeft: () => { fallingFrame = null; graphics?.ResetResidentEntities(); });
}
