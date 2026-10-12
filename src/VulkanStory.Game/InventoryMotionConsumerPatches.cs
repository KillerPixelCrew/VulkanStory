using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

/// <summary>Associates stock ground-storage draws with their actual inventory slots, including shared cached meshes.</summary>
internal static class InventoryMotionConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-motion-inventory";
    private static ProcessRuntime? runtime;
    private static System.Func<object, ICoreClientAPI>? clientApi;
    private static ConditionalWeakTable<object, ConditionalWeakTable<ItemSlot, SlotIdentity>> identities = new();
    private readonly record struct Context(object? Renderer, GameRenderSession? Session, long CapturedAfter,
        MultiTextureMeshRef? FirstMesh = null, ItemSlot? FirstSlot = null,
        bool OwnsRigidScope = false, GameGraphicsAdapter.RigidEntityScope PreviousRigidScope = default,
        GameGraphicsAdapter.MotionWriteSuspension? Suspension = null);
    [ThreadStatic] private static Context context;

    private sealed class SlotIdentity(ItemStack stack)
    {
        internal ItemStack Stack = stack;
        internal object Identity = new();
    }

    private static readonly MethodInfo ItemStackGetter = AccessTools.PropertyGetter(typeof(ItemSlot), nameof(ItemSlot.Itemstack))!;
    private static readonly MethodInfo Draw = AccessTools.Method(typeof(IRenderAPI), nameof(IRenderAPI.RenderMultiTextureMesh),
        [typeof(MultiTextureMeshRef), typeof(string), typeof(int)])!;

    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner, Assembly survival)
    {
        Type type = survival.GetType("Vintagestory.GameContent.GroundStorageRenderer", true)!;
        MethodInfo target = AccessTools.Method(type, "OnRenderFrame", [typeof(float), typeof(EnumRenderStage)]) ??
            throw new MissingMethodException(type.FullName, "OnRenderFrame");
        FieldInfo field = AccessTools.Field(type, "capi") ?? throw new MissingFieldException(type.FullName, "capi");
        if (field.FieldType != typeof(ICoreClientAPI))
            throw new InvalidOperationException("Original ground-storage client API field changed.");
        var renderer = Expression.Parameter(typeof(object), "renderer");
        var getter = Expression.Lambda<System.Func<object, ICoreClientAPI>>(
            Expression.Field(Expression.Convert(renderer, type), field), renderer).Compile();
        var harmony = new Harmony(Owner); bool attempted = false;
        return new StartupPatchGroup("graphics-motion-inventory", () =>
        {
            if (target.ReturnType != typeof(void) || target.GetMethodBody() == null)
                throw new InvalidOperationException("Original ground-storage render signature changed.");
            Check(PatchProcessor.GetOriginalInstructions(target));
        }, () =>
        {
            if (runtime != null) throw new InvalidOperationException("Inventory motion already has an owner.");
            runtime = owner; clientApi = getter; attempted = true;
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(InventoryMotionConsumerPatches), nameof(Enter)) { priority = Priority.First },
                transpiler: new HarmonyMethod(typeof(InventoryMotionConsumerPatches), nameof(Transpiler)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(InventoryMotionConsumerPatches), nameof(Exit)) { priority = Priority.Last });
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner);
            if (ReferenceEquals(runtime, owner))
            { runtime = null; clientApi = null; identities = new(); context = default; }
            attempted = false;
        });
    }

    private static void Check(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList();
        if (body.Count(instruction => instruction.Calls(ItemStackGetter)) != 1 ||
            body.Count(instruction => instruction.Calls(Draw)) != 1)
            throw new InvalidOperationException("Original/incoming ground-storage slot and draw anchors changed.");
    }

    private static void MoveEntryMarkers(CodeInstruction original, CodeInstruction inserted)
    {
        inserted.labels.AddRange(original.labels); original.labels.Clear();
        inserted.blocks.AddRange(original.blocks.Where(block => block.blockType != ExceptionBlockType.EndExceptionBlock));
        original.blocks.RemoveAll(block => block.blockType != ExceptionBlockType.EndExceptionBlock);
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var body = instructions.ToList(); Check(body);
        LocalBuilder slot = generator.DeclareLocal(typeof(ItemSlot));
        LocalBuilder stack = generator.DeclareLocal(typeof(ItemStack));
        foreach (var instruction in body)
        {
            bool readsStack = instruction.Calls(ItemStackGetter);
            if (readsStack)
            {
                // Keep the exact receiver used by the original getter, including
                // its null-conditional branch. The getter still executes once.
                var duplicate = new CodeInstruction(OpCodes.Dup);
                MoveEntryMarkers(instruction, duplicate);
                yield return duplicate;
                yield return new CodeInstruction(OpCodes.Stloc, slot);
            }
            if (instruction.Calls(Draw))
            {
                var renderer = new CodeInstruction(OpCodes.Ldarg_0);
                MoveEntryMarkers(instruction, renderer);
                yield return renderer;
                yield return new CodeInstruction(OpCodes.Ldloc, slot);
                yield return new CodeInstruction(OpCodes.Ldloc, stack);
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(InventoryMotionConsumerPatches), nameof(DrawInventory));
            }
            if (readsStack)
            {
                // Preserve the value the original renderer received, even if a
                // custom getter or later callback changes the slot afterwards.
                var store = new CodeInstruction(OpCodes.Stloc, stack);
                store.blocks.AddRange(instruction.blocks.Where(block => block.blockType == ExceptionBlockType.EndExceptionBlock));
                instruction.blocks.RemoveAll(block => block.blockType == ExceptionBlockType.EndExceptionBlock);
                yield return instruction;
                yield return new CodeInstruction(OpCodes.Dup);
                yield return store;
            }
            else yield return instruction;
        }
    }

    private static void Enter(object __instance, out Context __state)
    {
        __state = context;
        context = default;
        if (runtime?.Routing.RoutingEnabled != true) return;
        if (ScreenManager.Platform is not ClientPlatformWindows platform || !runtime.TrySession(platform, out var session))
            throw new InvalidOperationException("Active inventory motion lost its session.");
        if (session.Temporal.CurrentClient == null || !ReferenceEquals(session.Temporal.CurrentClient, clientApi!(__instance).World)) return;
        var graphics = session.Graphics;
        var previousRigid = graphics.EnterRigidEntityMotionScope(null);
        context = new(__instance, session, session.Temporal.EntityMotion.ModelCaptureSerial,
            OwnsRigidScope: true, PreviousRigidScope: previousRigid);
        // Protect the entire producer, including its shader/model/blend setup.
        // Nested inventory shading cannot inherit an enclosing object's mask.
        if (graphics.FrameState.MotionWriteActive)
            context = context with { Suspension = graphics.SuspendMotionWrites() };
    }

    private static Exception? Exit(Exception? __exception, Context __state)
    {
        var leaving = context;
        try
        {
            if (leaving.OwnsRigidScope && leaving.Session is { } session)
            {
                try
                {
                    if (leaving.Suspension is { } suspension) session.Graphics.RestoreMotionWrites(suspension);
                }
                finally { session.Graphics.ExitRigidEntityMotionScope(leaving.PreviousRigidScope); }
            }
        }
        finally
        {
            // A nested renderer can change model state; the resumed producer
            // must upload again, even if restoration of GPU state failed.
            context = __state with { CapturedAfter = __state.Session?.Temporal.EntityMotion.ModelCaptureSerial ?? 0 };
        }
        return __exception;
    }

    private static void DrawInventory(IRenderAPI render, MultiTextureMeshRef mesh, string sampler, int unit,
        object renderer, ItemSlot? slot, ItemStack? stack)
    {
        var current = context;
        bool opened = false;
        bool sharedGeometry = false;
        if (ReferenceEquals(current.Renderer, renderer) && current.Session is { } session &&
            slot != null && stack != null && runtime?.Routing.RoutingEnabled == true &&
            session.Temporal.CurrentClient != null &&
            ReferenceEquals(session.Temporal.CurrentClient, clientApi!(renderer).World))
        {
            if (current.FirstSlot == null)
                context = current with { FirstMesh = mesh, FirstSlot = slot };
            else sharedGeometry = !ReferenceEquals(current.FirstSlot, slot) && ReferenceEquals(current.FirstMesh, mesh);

            // The accepted model belongs to the stack returned to the original
            // renderer. A reentrant inventory edit cannot transfer that history.
            if (ReferenceEquals(slot.Itemstack, stack))
            {
                var slots = identities.GetValue(renderer, static _ => new());
                var identity = slots.GetValue(slot, _ => new(stack));
                if (!ReferenceEquals(identity.Stack, stack))
                {
                    identity.Stack = stack;
                    identity.Identity = new();
                }
                opened = session.Graphics.BeginInventoryMotionWrite(identity.Identity, mesh, current.CapturedAfter);
            }
        }
        try
        {
            if (opened && sharedGeometry) current.Session!.Graphics.NoteInventorySharedGeometryDraw();
            render.RenderMultiTextureMesh(mesh, sampler, unit);
        }
        finally { if (opened) current.Session!.Graphics.EndMotionWrite(); }
    }
}
