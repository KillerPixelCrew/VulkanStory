using System.Globalization;
using Vintagestory.API.Client;
using VulkanStory.Game.ModRendering;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    // Host migrated from VulkanClientPlatform.ModPasses.cs at
    // 386e0d05386d0b228b439d09aeca851428f7bbf3. State belongs to this sidecar.
    private const PassFlags ModPassFlags = PassFlags.OpenSampling | PassFlags.AllowSplit;
    private readonly Dictionary<VulkanStoryPassDecl, ModPassPlan> modPassPlans = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<VulkanStoryPassDecl> modPassFailuresLogged = new(ReferenceEqualityComparer.Instance);
    private List<FrameBufferRef>? modPassPlansFor;
    private int modPassPlansMotionIndex = int.MinValue;
    private long modPassRegistryVersion = -1;
    private EnumRenderStage? modRenderStage;
    private readonly Dictionary<EnumRenderStage, (string Begin, string After)> stageGpuLabels = new();
    private PassDeclaration? stageStatedDeclaration;
    private EnumRenderStage? stageStatedSlot;
    private bool modMotionWriterActive;
    private int modMotionFramebuffer;
    private uint modMotionDrawBuffers;
    internal long ModPassesRun { get; private set; }
    internal long ModPassesSkipped { get; private set; }
    internal string? CurrentModPass { get; private set; }

    private sealed class ModPassPlan
    {
        internal FrameBufferRef? Target;
        internal bool Runnable;
        internal string SkipReason = "";
        internal PassDeclaration Declaration = new();
    }

    private void InstallModPassHooks()
    {
        VulkanStoryModPasses.MotionBeginHook = BeginModMotionWriter;
        VulkanStoryModPasses.MotionEndHook = EndModMotionWriter;
    }

    private void RemoveModPassHooks()
    {
        if (VulkanStoryModPasses.MotionBeginHook?.Target == this) VulkanStoryModPasses.MotionBeginHook = null;
        if (VulkanStoryModPasses.MotionEndHook?.Target == this) VulkanStoryModPasses.MotionEndHook = null;
        VulkanStoryModPasses.Clear();
        ClearModPassPlans();
    }

    internal void ClearModPassPlans()
    {
        modPassPlans.Clear();
        modPassFailuresLogged.Clear();
        modPassPlansFor = null;
        modRenderStage = null;
        modMotionWriterActive = false;
        stageStatedDeclaration = null;
    }

    internal EnumRenderStage? EnterModRenderStage(EnumRenderStage stage)
    {
        var renderer = RequireDevice();
        ((VulkanStory.Contracts.ILatencyStageListener)renderer).OnFrameRenderStart();
        renderer.GpuMark(StageGpuLabel(stage, begin: true));
        EnumRenderStage? previous = modRenderStage;
        modRenderStage = stage;
        return previous;
    }

    internal void EndModRenderStage(EnumRenderStage stage, EnumRenderStage? previous)
    {
        try
        {
            var renderer = RequireDevice();
            renderer.EndStagePass();
            renderer.GpuMark(StageGpuLabel(stage, begin: false));
        }
        finally
        {
            modRenderStage = previous;
            stageStatedDeclaration = null;
        }
    }

    private string StageGpuLabel(EnumRenderStage stage, bool begin)
    {
        if (!stageGpuLabels.TryGetValue(stage, out var labels))
        {
            string name = stage.ToString();
            labels = ("stage_" + name, "after_" + name);
            stageGpuLabels.Add(stage, labels);
        }
        return begin ? labels.Begin : labels.After;
    }

    private PassDeclaration? StageDrawDeclaration()
    {
        if (modRenderStage is not { } stage) return null;
        if (stageStatedDeclaration?.FramebufferId == CurrentTargetId && stageStatedSlot == stage)
            return stageStatedDeclaration;
        PassFlags flags = stage is EnumRenderStage.Before or EnumRenderStage.ShadowFar or
            EnumRenderStage.ShadowFarDone or EnumRenderStage.ShadowNear or EnumRenderStage.ShadowNearDone or
            EnumRenderStage.Opaque or EnumRenderStage.OIT
            ? PassFlags.AllowSplit : PassFlags.OpenSampling | PassFlags.AllowSplit;
        stageStatedSlot = stage;
        stageStatedDeclaration = new PassDeclaration
        {
            Name = stage + "/" + (CurrentTargetId == PassDeclaration.DefaultFramebuffer
                ? "Default" : "fbo" + CurrentTargetId.ToString(CultureInfo.InvariantCulture)),
            FramebufferId = CurrentTargetId,
            ColorSlots = uint.MaxValue,
            Flags = flags,
        };
        return stageStatedDeclaration;
    }

    private bool BeginModMotionWriter(VulkanStoryMotionWriterDecl writer)
    {
        RequireDevice();
        if (aoTemporal == null || modRenderStage is not { } stage ||
            !VulkanStoryPassContract.IsMotionWindowSlot((EnumVulkanStoryPass)(int)stage)) return false;
        int target = CurrentTargetId;
        uint buffers = Stated.DrawBuffers(target);
        bool opened = BeginMotionWrite(aoTemporal, writer.Mode == EnumVulkanStoryMotionWrite.MotionOnly);
        if (opened)
        {
            modMotionFramebuffer = target;
            modMotionDrawBuffers = buffers;
            modMotionWriterActive = true;
        }
        return opened;
    }

    private void EndModMotionWriter()
    {
        RequireDevice();
        if (!modMotionWriterActive) return;
        modMotionWriterActive = false;
        try { EndMotionWrite(); }
        finally { Stated.SetDrawBuffers(modMotionFramebuffer, modMotionDrawBuffers); }
    }

    /// <summary>Executes registrations for the current stage using validated attachment plans and restores render state after each callback.</summary>
    /// <param name="stage">Original render stage whose bracket just completed.</param>
    internal void RunModPasses(EnumRenderStage stage)
    {
        var renderer = RequireDevice();
        if (modPassRegistryVersion != VulkanStoryModPasses.Version)
        {
            // Also release plans when the last pass was unregistered. No
            // PlanFor call follows an empty slot snapshot.
            modPassPlans.Clear();
            modPassFailuresLogged.Clear();
            modPassRegistryVersion = VulkanStoryModPasses.Version;
        }
        VulkanStoryPassRegistration[] passes = VulkanStoryModPasses.ForSlot((EnumVulkanStoryPass)(int)stage);
        if (passes.Length == 0) return;
        FrameBufferRef? saved = currentFramebuffer;
        PassDeclaration? outer = StatedPass;
        var viewport = Stated.Viewport;
        try
        {
            foreach (var registration in passes) RunModPass(registration);
        }
        finally
        {
            renderer.EndStagePass();
            StatedPass = outer;
            SetFramebuffer(saved, keepViewport: true);
            Stated.Viewport = viewport;
        }
    }

    private void RunModPass(VulkanStoryPassRegistration registration)
    {
        VulkanStoryPassDecl decl = registration.Decl;
        ModPassPlan plan = PlanFor(registration);
        if (!plan.Runnable)
        {
            ModPassesSkipped++;
            if (modPassFailuresLogged.Add(decl))
                platform!.Logger.Warning("VulkanStory mod pass '{0}' of {1} skipped: {2}", decl.Name, registration.ModId, plan.SkipReason);
            return;
        }
        int targetId = plan.Target?.FboId ?? PassDeclaration.DefaultFramebuffer;
        uint savedDrawBuffers = Stated.DrawBuffers(targetId);
        var savedViewport = Stated.Viewport;
        bool motion = false;
        try
        {
            SetFramebuffer(plan.Target, keepViewport: true);
            Stated.SetDrawBuffers(targetId, plan.Declaration.ColorSlots);
            CurrentModPass = plan.Declaration.Name;
            StatedPass = plan.Declaration;
            if (decl.MotionWriter != null) motion = BeginModMotionWriter(decl.MotionWriter);
            decl.Draw(decl);
            ModPassesRun++;
        }
        catch (Exception error)
        {
            if (modPassFailuresLogged.Add(decl))
                platform!.Logger.Error("VulkanStory mod pass '{0}' of {1} threw: {2}", decl.Name, registration.ModId, error);
        }
        finally
        {
            try { if (motion) EndModMotionWriter(); }
            finally
            {
                RequireDevice().EndStagePass();
                CurrentModPass = null;
                StatedPass = null;
                Stated.SetDrawBuffers(targetId, savedDrawBuffers);
                Stated.Viewport = savedViewport;
            }
        }
    }

    private ModPassPlan PlanFor(VulkanStoryPassRegistration registration)
    {
        List<FrameBufferRef> buffers = platform!.FrameBuffers;
        long version = VulkanStoryModPasses.Version;
        if (!ReferenceEquals(buffers, modPassPlansFor) || modPassPlansMotionIndex != FrameState.MotionAttachment ||
            modPassRegistryVersion != version)
        {
            modPassPlans.Clear();
            modPassFailuresLogged.Clear();
            modPassPlansFor = buffers;
            modPassPlansMotionIndex = FrameState.MotionAttachment;
            modPassRegistryVersion = version;
        }
        if (!modPassPlans.TryGetValue(registration.Decl, out ModPassPlan? plan))
        {
            plan = BuildModPassPlan(registration);
            modPassPlans[registration.Decl] = plan;
        }
        return plan;
    }

    /// <summary>Resolves the declared handles to the target, its colour slots and the read textures.</summary>
    private ModPassPlan BuildModPassPlan(VulkanStoryPassRegistration registration)
    {
        VulkanStoryPassDecl decl = registration.Decl;
        var plan = new ModPassPlan();
        EnumVulkanStoryTarget target = VulkanStoryPassContract.TargetOf(decl);
        int targetIndex = target switch
        {
            EnumVulkanStoryTarget.Primary => PrimaryIndex,
            EnumVulkanStoryTarget.Transparent => (int)EnumFrameBuffer.Transparent,
            _ => -1,
        };

        string targetName;
        uint colorSlots = 0;
        if (target == EnumVulkanStoryTarget.Default)
        {
            plan.Target = null;
            targetName = "Default";
            colorSlots = uint.MaxValue;
        }
        else
        {
            List<FrameBufferRef> buffers = platform!.FrameBuffers;
            FrameBufferRef? buffer = buffers != null && targetIndex >= 0 && targetIndex < buffers.Count ? buffers[targetIndex] : null;
            if (buffer == null)
            {
                plan.SkipReason = target + " does not exist";
                return plan;
            }
            plan.Target = buffer;
            targetName = targetIndex.ToString(CultureInfo.InvariantCulture);
            foreach (EnumVulkanStoryAttachment write in decl.Writes)
            {
                if (write == EnumVulkanStoryAttachment.PrimaryDepth)
                {
                    if (buffer.DepthTextureId <= 0)
                    {
                        plan.SkipReason = "PrimaryDepth does not exist";
                        return plan;
                    }
                    continue;
                }
                int slot = VulkanStoryPassContract.ColorSlotOf(write);
                if (slot < 0 || buffer.ColorTextureIds == null || slot >= buffer.ColorTextureIds.Length ||
                    buffer.ColorTextureIds[slot] <= 0 || (target == EnumVulkanStoryTarget.Primary && slot == FrameState.MotionAttachment))
                {
                    plan.SkipReason = write + " does not exist this session";
                    return plan;
                }
                colorSlots |= 1u << slot;
            }
            if (decl.MotionWriter != null)
            {
                // The window writes the motion attachment on top of the declared slots; without one
                // the begin call refuses and the draw falls back to camera reprojection.
                // Undeclared colour slots stay out of the scope even though the window's draw-buffer
                // mask names them, so a pass can still sample them (the final composition's shape).
                if (FrameState.MotionAttachment > -1) colorSlots |= 1u << FrameState.MotionAttachment;
            }
        }

        var reads = new List<int>();
        foreach (EnumVulkanStoryAttachment read in decl.Reads)
        {
            int id = TextureOf(read);
            if (id > 0 && !reads.Contains(id)) reads.Add(id);
        }

        plan.Declaration = new PassDeclaration
        {
            Name = "Mod/" + registration.ModId + "/" + decl.Name + "/" + targetName,
            FramebufferId = target == EnumVulkanStoryTarget.Default
                ? PassDeclaration.DefaultFramebuffer
                : plan.Target!.FboId,
            ColorSlots = colorSlots,
            Reads = reads.ToArray(),
            Flags = ModPassFlags,
        };
        plan.Runnable = true;
        return plan;
    }

    /// <summary>The texture behind a handle in the current framebuffer set, 0 when it does not exist.</summary>
    internal int TextureOf(EnumVulkanStoryAttachment attachment)
    {
        switch (attachment)
        {
        case EnumVulkanStoryAttachment.PrimaryMotion:
            return FrameState.MotionAttachment >= 0 ? ColourOf(PrimaryIndex, FrameState.MotionAttachment) : 0;
        case EnumVulkanStoryAttachment.PrimaryDepth:
            return DepthOf(PrimaryIndex);
        case EnumVulkanStoryAttachment.LiquidDepth:
            return DepthOf((int)EnumFrameBuffer.LiquidDepth);
        case EnumVulkanStoryAttachment.ShadowFarDepth:
            return DepthOf((int)EnumFrameBuffer.ShadowmapFar);
        case EnumVulkanStoryAttachment.ShadowNearDepth:
            return DepthOf((int)EnumFrameBuffer.ShadowmapNear);
        case EnumVulkanStoryAttachment.GodRays:
            return ColourOf(GodRaysIndex, 0);
        case EnumVulkanStoryAttachment.BloomLowRes:
            return ColourOf(BlurVerticalLowResIndex, 0);
        case EnumVulkanStoryAttachment.Luma:
            return ColourOf(LumaIndex, 0);
        case EnumVulkanStoryAttachment.SsaoBlurred:
            return ColourOf((int)EnumFrameBuffer.SSAOBlurVertical, 0);
        }
        EnumVulkanStoryTarget target = VulkanStoryPassContract.TargetOf(attachment);
        int slot = VulkanStoryPassContract.ColorSlotOf(attachment);
        if (slot < 0) return 0;
        if (target == EnumVulkanStoryTarget.Primary)
        {
            // Absent G-buffer: slot 2 is the motion attachment, which is not this handle.
            if (slot == FrameState.MotionAttachment) return 0;
            return ColourOf(PrimaryIndex, slot);
        }
        return target == EnumVulkanStoryTarget.Transparent ? ColourOf((int)EnumFrameBuffer.Transparent, slot) : 0;
    }

    private int ColourOf(int index, int slot)
    {
        List<FrameBufferRef> buffers = platform!.FrameBuffers;
        if (buffers == null || index < 0 || index >= buffers.Count) return 0;
        FrameBufferRef buffer = buffers[index];
        if (buffer?.ColorTextureIds == null || slot < 0 || slot >= buffer.ColorTextureIds.Length) return 0;
        return buffer.ColorTextureIds[slot];
    }

    private int DepthOf(int index)
    {
        List<FrameBufferRef> buffers = platform!.FrameBuffers;
        if (buffers == null || index < 0 || index >= buffers.Count || buffers[index] == null) return 0;
        return buffers[index].DepthTextureId;
    }
}
