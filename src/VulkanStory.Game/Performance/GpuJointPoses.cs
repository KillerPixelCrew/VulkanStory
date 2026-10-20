using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;
using VulkanStory.Contracts;

namespace VulkanStory.Game.Performance;

/// <summary>Defers visual inverse-bind products while retaining stock hierarchy and attachment evaluation.</summary>
internal static class GpuJointPoses
{
    private sealed class State(ClientAnimator owner)
    {
        internal readonly ClientAnimator Owner = owner;
        internal readonly Dictionary<float[], ElementPose> Nodes = new(ReferenceEqualityComparer.Instance);
        internal float[] Pairs = [], Output = [];
        internal bool[] Seen = [];
        internal JointPoseSnapshot? Snapshot;
        internal bool Pending;
        internal bool CpuArrayPublished;
        ~State() => Snapshot?.Release();

        internal bool Begin()
        {
            float[] output = Owner.TransformationMatrices;
            if (output == null || output.Length == 0 || output.Length != Owner.MaxJointId * 16 || output.Length > 256 * 16) return false;
            if (!ReferenceEquals(output, Output)) { Materialize(); Output = output; }
            int count = output.Length / 16;
            if (Pairs.Length != count * 32) { Pairs = new float[count * 32]; Seen = new bool[count]; }
            Array.Clear(Pairs); Array.Clear(Seen); Nodes.Clear();
            for (int i = 0; i < count; i++)
                for (int d = 0; d < 4; d++) { Pairs[i * 32 + d * 5] = 1; Pairs[i * 32 + 16 + d * 5] = 1; }
            if (!Walk(Owner.RootPoses, 0)) return false;
            foreach (int id in Owner.jointsById.Keys)
                if (id < 0 || id >= count || (id > 0 && !Seen[id])) return false;
            Array.Clear(Seen);
            if (Owner.jointsById.ContainsKey(0))
            {
                if (Snapshot != null && Snapshot.JointCount == count) Snapshot.Pairs[..32].CopyTo(Pairs);
                else output.AsSpan(0, 16).CopyTo(Pairs);
            }
            if (!arrays.TryGetValue(output, out var prior)) arrays.Add(output, this);
            else if (!ReferenceEquals(prior, this)) return false;
            return true;
        }

        private bool Walk(List<ElementPose> poses, int depth)
        {
            if (depth > 128 || poses == null) return false;
            foreach (var pose in poses)
            {
                if (Nodes.Count >= 2048 || pose.ForElement?.GetType() != typeof(ShapeElement) || pose.AnimModelMatrix?.Length != 16 ||
                    pose.ForElement.JointId >= Seen.Length ||
                    (pose.ForElement.JointId > 0 && pose.ForElement.inverseModelTransform?.Length != 16) ||
                    !Nodes.TryAdd(pose.AnimModelMatrix, pose)) return false;
                if (pose.ForElement.JointId > 0) Seen[pose.ForElement.JointId] = true;
                if (pose.ChildElementPoses != null && !Walk(pose.ChildElementPoses, depth + 1)) return false;
            }
            return true;
        }

        internal bool Record(float[] model, float[] inverse)
        {
            if (!Nodes.TryGetValue(model, out var pose) || !ReferenceEquals(pose.ForElement.inverseModelTransform, inverse)) return false;
            int id = pose.ForElement.JointId;
            if (id <= 0 || id >= Seen.Length) return false;
            model.AsSpan().CopyTo(Pairs.AsSpan(id * 32, 16));
            inverse.AsSpan().CopyTo(Pairs.AsSpan(id * 32 + 16, 16));
            Seen[id] = true; DeferredJointProducts++;
            return true;
        }

        internal void Finish(bool success)
        {
            bool complete = success;
            if (complete) foreach (int id in Owner.jointsById.Keys) if (id != 0 && !Seen[id]) { complete = false; break; }
            if (complete)
            {
                if (Snapshot == null || !Snapshot.Matches(Pairs))
                { var next = JointPoseSnapshot.Rent(Pairs); Snapshot?.Release(); Snapshot = next; }
                Pending = true;
            }
            else
            {
                // Only recorded products were replaced by transport placeholders.
                if (Pending && Snapshot != null) Snapshot.CopyMatrices(Output);
                for (int i = 1; i < Seen.Length; i++)
                    if (Seen[i]) JointPoseSnapshot.Multiply(Pairs.AsSpan(i * 32, 16), Pairs.AsSpan(i * 32 + 16, 16), Output.AsSpan(i * 16, 16));
                Pending = false; Snapshot?.Release(); Snapshot = null;
            }
        }

        internal void Materialize()
        {
            if (!Pending || Snapshot == null) return;
            Snapshot.CopyMatrices(Output);
            Pending = false; CpuMaterializations++;
        }
    }

    private static ConditionalWeakTable<ClientAnimator, State> states = new();
    private static ConditionalWeakTable<float[], State> arrays = new();
    [ThreadStatic] private static State? current;
    [ThreadStatic] private static int visualRead;
    private static bool armed, readersChecked;
    private static readonly HashSet<Assembly> stock = new();
    private static readonly MethodInfo Root = AccessTools.Method(typeof(ClientAnimator), "calculateMatrices", [typeof(float)])!;
    private static readonly MethodInfo Inner = AccessTools.GetDeclaredMethods(typeof(ClientAnimator))
        .Single(method => method.Name == "calculateMatrices" && method.GetParameters().Length == 8);
    private static readonly MethodInfo Mul = AccessTools.Method(typeof(Mat4f), nameof(Mat4f.Mul), [typeof(float[]), typeof(float[]), typeof(float[])])!;
    private static readonly MethodInfo Matrices = AccessTools.PropertyGetter(typeof(AnimatorBase), nameof(AnimatorBase.Matrices))!;
    private static readonly MethodInfo VisualGetter = AccessTools.PropertyGetter(typeof(IAnimator), nameof(IAnimator.Matrices))!;
    private static readonly MethodInfo Visual = AccessTools.Method(typeof(EntityShapeRenderer), nameof(EntityShapeRenderer.DoRender3DOpaqueBatched),
        [typeof(float), typeof(bool)])!;
    private static readonly List<MethodInfo> visualConsumers = new();
    internal static long DeferredJointProducts { get; private set; }
    internal static long CpuMaterializations { get; private set; }
    internal static long CpuFacadeChecks { get; private set; }
    internal static bool CpuFacadeValid { get; private set; } = true;
    private static bool Enabled => armed && readersChecked && PerformancePatchSet.Current?.GpuJointPosePreparation == true;

    internal static PerformanceFeature Create(Assembly essentials, Assembly survival) => new("gpu-joint-poses",
        settings => settings.GpuJointPosePreparation, () =>
        {
            visualConsumers.Clear(); visualConsumers.Add(Visual);
            var blockRenderer = typeof(AnimationUtil).GetField(nameof(AnimationUtil.renderer))!.FieldType;
            visualConsumers.AddRange(AccessTools.GetDeclaredMethods(blockRenderer).Where(method => method.GetMethodBody() != null &&
                PatchProcessor.GetOriginalInstructions(method).Any(IsMatricesRead)));
            if (Root == null || Inner == null || Mul.ReturnType != typeof(float[]) ||
                PatchProcessor.GetOriginalInstructions(Inner).Count(instruction => instruction.Calls(Mul)) != 2 ||
                visualConsumers.Count < 2 || PatchProcessor.GetOriginalInstructions(Visual).Count(IsMatricesRead) != 1)
                throw new InvalidOperationException("Stock visual joint finalization anchors changed.");
        }, harmony =>
        {
            stock.Add(typeof(ClientAnimator).Assembly); stock.Add(typeof(ClientMain).Assembly);
            stock.Add(essentials); stock.Add(survival); stock.Add(typeof(GpuJointPoses).Assembly);
            harmony.Patch(Root, prefix: new HarmonyMethod(typeof(GpuJointPoses), nameof(BeforeRoot)),
                finalizer: new HarmonyMethod(typeof(GpuJointPoses), nameof(AfterRoot)));
            harmony.Patch(Inner, transpiler: new HarmonyMethod(typeof(GpuJointPoses), nameof(JointProductTranspiler)));
            harmony.Patch(Matrices, prefix: new HarmonyMethod(typeof(GpuJointPoses), nameof(CpuGetter)));
            foreach (var consumer in visualConsumers)
                harmony.Patch(consumer, transpiler: new HarmonyMethod(typeof(GpuJointPoses), nameof(VisualTranspiler)));
            AppDomain.CurrentDomain.AssemblyLoad += AssemblyLoaded;
            armed = true;
        }, Uninstall: () =>
        {
            armed = readersChecked = false; MaterializeAll();
            AppDomain.CurrentDomain.AssemblyLoad -= AssemblyLoaded; stock.Clear();
        }, WorldReady: CheckReaders, WorldLeft: () =>
        {
            // CPU consumers may retain returned arrays across world changes.
            readersChecked = false; MaterializeAll();
        });

    private static void CheckReaders(ICoreClientAPI api)
    {
        readersChecked = false;
        foreach (var method in new[] { Root, Inner, Matrices, Mul, Visual,
            AccessTools.Method(typeof(Mat4f), nameof(Mat4f.Multiply), [typeof(float[]), typeof(float[]), typeof(float[])])! })
            if (Harmony.GetPatchInfo(method)?.Owners.Any(owner => !PerformancePatchSet.IsOwnOwner(owner)) == true) return;
        foreach (var method in visualConsumers)
            if (Harmony.GetPatchInfo(method)?.Owners.Any(owner => !PerformancePatchSet.IsOwnOwner(owner)) == true) return;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (stock.Contains(assembly) || assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) continue;
            try
            {
                if (!assembly.GetReferencedAssemblies().Any(reference => reference.Name == "VintagestoryAPI")) continue;
                using var image = Mono.Cecil.AssemblyDefinition.ReadAssembly(assembly.Location);
                if (image.MainModule.GetMemberReferences().OfType<Mono.Cecil.FieldReference>()
                    .Any(field => field.Name is "TransformationMatrices" or "TransformationMatricesDefaultPose"))
                {
                    api.Logger.Notification("VulkanStory: joint pose finalization stays on the CPU because {0} reads raw animator arrays.", assembly.GetName().Name);
                    return;
                }
            }
            catch { return; } // Unknown raw readers retain the original CPU representation.
        }
        readersChecked = true;
    }

    private static void AssemblyLoaded(object? sender, AssemblyLoadEventArgs args)
    {
        if (!readersChecked || stock.Contains(args.LoadedAssembly)) return;
        try
        {
            if (!args.LoadedAssembly.GetReferencedAssemblies().Any(reference => reference.Name == "VintagestoryAPI")) return;
        }
        catch { }
        readersChecked = false; MaterializeAll();
    }

    private static void MaterializeAll() { foreach (var pair in states) pair.Value.Materialize(); }
    private static void BeforeRoot(ClientAnimator __instance, out State? __state)
    {
        __state = current; current = null;
        if (!Enabled || __instance.GetType() != typeof(ClientAnimator) || !__instance.CalculateMatrices)
        {
            if (states.TryGetValue(__instance, out var old)) old.Materialize();
            return;
        }
        var state = states.GetValue(__instance, animator => new State(animator));
        if (state.CpuArrayPublished) { state.Materialize(); return; }
        if (!state.Begin())
        { state.Materialize(); return; }
        current = state;
    }

    private static Exception? AfterRoot(State? __state, Exception? __exception)
    {
        var state = current; current = __state;
        state?.Finish(__exception == null);
        if (__exception == null && HeadlessHarnessOptions.Enabled && CpuFacadeChecks == 0 &&
            state?.Pending == true && state.Owner.ActiveAnimationCount > 0)
        {
            var expected = new float[state.Output.Length];
            for (int i = 0; i < state.Snapshot!.JointCount; i++)
            {
                var matrix = Mat4f.Mul(new float[16], state.Snapshot.Pairs.Slice(i * 32, 16).ToArray(),
                    state.Snapshot.Pairs.Slice(i * 32 + 16, 16).ToArray());
                matrix.CopyTo(expected, i * 16);
            }
            var observed = state.Owner.Matrices;
            CpuFacadeValid &= observed.AsSpan().SequenceEqual(expected) && !state.Pending;
            CpuFacadeChecks++;
        }
        return __exception;
    }

    private static IEnumerable<CodeInstruction> JointProductTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        int calls = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(Mul) && ++calls == 2)
                instruction.operand = AccessTools.Method(typeof(GpuJointPoses), nameof(Product));
            yield return instruction;
        }
    }

    private static float[] Product(float[] output, float[] model, float[] inverse)
    {
        if (current?.Record(model, inverse) != true) return Mat4f.Mul(output, model, inverse);
        Array.Clear(output, 0, 16); output[0] = output[5] = output[10] = output[15] = 1;
        return output;
    }

    private static void CpuGetter(AnimatorBase __instance)
    {
        if (visualRead != 0 || __instance is not ClientAnimator animator) return;
        var state = states.GetValue(animator, owner => new State(owner));
        state.Materialize(); state.CpuArrayPublished = true;
    }

    private static IEnumerable<CodeInstruction> VisualTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (IsMatricesRead(instruction))
            {
                instruction.opcode = System.Reflection.Emit.OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(GpuJointPoses), nameof(VisualMatrices));
            }
            yield return instruction;
        }
    }

    private static bool IsMatricesRead(CodeInstruction instruction) => instruction.Calls(VisualGetter) || instruction.Calls(Matrices);

    private static float[] VisualMatrices(IAnimator animator)
    {
        if ((!Enabled || ShaderProgramBase.CurrentShaderProgram?.HasUniform("vsComputeDeformation") != true) &&
            animator is ClientAnimator client && states.TryGetValue(client, out var state)) state.Materialize();
        visualRead++;
        try { return animator.Matrices; }
        finally { visualRead--; }
    }

    internal static JointPoseSnapshot? PoseFor(object data) => data is float[] output &&
        arrays.TryGetValue(output, out var state) && state.Pending ? state.Snapshot : null;
    internal static void MaterializePayload(object data)
    { if (data is float[] output && arrays.TryGetValue(output, out var state)) state.Materialize(); }
}
