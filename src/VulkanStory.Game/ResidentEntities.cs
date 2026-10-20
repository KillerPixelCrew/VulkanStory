using System.Numerics;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client.NoObf;
using VulkanStory.Render.Vulkan;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    internal readonly record struct ResidentEntityScope(EntityRenderer? Renderer, int PoseOffset);
    private ResidentEntityScope residentEntityScope;
    private readonly HashSet<int> residentEntityReads = new();
    private bool residentEntitySetting, residentEntityStageActive, residentEntityStageCaptured;
    private int residentEntityPassKind, residentEntityTarget;
    private TerrainView residentEntityView;

    internal void ResetResidentEntities()
    {
        device?.ResetEntityScene();
        residentEntityScope = default;
        EndResidentEntityStage();
    }

    internal void ReleaseResidentEntity(long entityKey) => device?.ReleaseEntityRows(entityKey);

    internal void BeginResidentEntityStage(MethodBase stage)
    {
        var renderer = RequireDevice();
        if (residentEntitySetting != renderer.ResidentEntitiesEnabled)
        {
            ResetResidentEntities();
            residentEntitySetting = renderer.ResidentEntitiesEnabled;
        }
        EndResidentEntityStage();
        int kind = modRenderStage switch
        {
            EnumRenderStage.Opaque => 0,
            EnumRenderStage.AfterOIT => 1,
            EnumRenderStage.ShadowNear => 2,
            EnumRenderStage.ShadowFar => 3,
            _ => -1,
        };
        if (!renderer.ResidentEntitiesEnabled || !NativeEntitiesEnabled || kind < 0 || CurrentModPass != null ||
            stage is not MethodInfo method || ForeignEntityHooks(method) || currentFramebuffer == null ||
            aoTemporal?.CurrentClient is not ClientMain client || client.EntityPlayer?.CameraPos is not { } origin)
            return;

        // These are the same camera-relative matrices the stock stage gives its shaders.
        // In stereo CameraMatrixOriginf already includes the physical-eye translation.
        bool shadow = kind >= 2;
        float[] view = shadow ? client.CurrentModelViewMatrix : client.api.Render.CameraMatrixOriginf;
        float[] projection = client.CurrentProjectionMatrix;
        if (view.Length != 16 || projection.Length != 16 || !CaptureEntityCullPlanes(client)) return;
        var vp = Vintagestory.API.MathTools.Mat4f.Mul(new float[16], projection, view);
        if (vp.Any(value => !float.IsFinite(value)) || !double.IsFinite(origin.X) ||
            !double.IsFinite(origin.Y) || !double.IsFinite(origin.Z)) return;
        var planes = entityCullPlaneSnapshot;
        // SystemRenderShadowMap recalculates this same stock culler for each
        // cascade before the entity stage; retain its double coefficients.
        TerrainPlane Plane(int index) => new(planes[index].X, planes[index].Y,
            planes[index].Z, planes[index].D);
        residentEntityView = new TerrainView
        {
            Eye = renderer.TerrainEyeIndex,
            CameraX = origin.X, CameraY = origin.Y, CameraZ = origin.Z,
            ViewProjection = new Matrix4x4(vp[0], vp[1], vp[2], vp[3], vp[4], vp[5], vp[6], vp[7],
                vp[8], vp[9], vp[10], vp[11], vp[12], vp[13], vp[14], vp[15]),
            ViewDistance = ClientSettings.ViewDistance,
            CullMode = (int)(shadow ? kind == 2 ? EnumFrustumCullMode.CullInstantShadowPassNear :
                EnumFrustumCullMode.CullInstantShadowPassFar : EnumFrustumCullMode.CullNormal),
            Shadow = shadow,
            Plane0 = Plane(0), Plane1 = Plane(1), Plane2 = Plane(2),
            Plane3 = Plane(3), Plane4 = Plane(4), Plane5 = Plane(5),
        };
        residentEntityTarget = currentFramebuffer.FboId;
        residentEntityPassKind = kind;
        residentEntityStageActive = true;
    }

    internal void DrawResidentEntityStage()
    {
        if (!residentEntityStageActive || !residentEntityStageCaptured) return;
        var renderer = RequireDevice();
        var pass = NativeEntityPassFor(residentEntityTarget);
        pass.Reads = residentEntityReads.ToArray();
        var previousCategory = renderer.SceneDrawCategory;
        renderer.SceneDrawCategory = SceneDrawCategory.Entity;
        try
        {
            if (!renderer.BeginNativePass(pass) || !renderer.DrawEntityPass(residentEntityPassKind, residentEntityView))
                throw new InvalidOperationException("An admitted resident entity pass could not be submitted.");
        }
        finally
        {
            renderer.EndNativePass(keepScope: true);
            renderer.SceneDrawCategory = previousCategory;
        }
    }

    internal void EndResidentEntityStage()
    {
        residentEntityStageActive = residentEntityStageCaptured = false;
        residentEntityReads.Clear();
        residentEntityScope = default;
    }

    internal ResidentEntityScope EnterResidentEntityScope(EntityRenderer renderer, bool shadow)
    {
        var previous = residentEntityScope;
        residentEntityScope = new(residentEntityStageActive && RequireDevice().ResidentEntitiesEnabled &&
            shadow == (residentEntityPassKind >= 2) && SupportedEntityRenderer(renderer) ? renderer : null, -1);
        return previous;
    }

    internal void ExitResidentEntityScope(ResidentEntityScope previous) => residentEntityScope = previous;

    private bool ResidentEntityProgram(out ShaderProgramBase program)
    {
        program = ShaderProgramBase.CurrentShaderProgram!;
        return residentEntityScope.Renderer != null && residentEntityStageActive && device?.ResidentEntitiesEnabled == true &&
            currentFramebuffer?.FboId == residentEntityTarget && program != null && StatedProgram == program.ProgramId &&
            ReferenceEquals(program, residentEntityPassKind >= 2 ? ShaderPrograms.Shadowmapentityanimated : ShaderPrograms.Entityanimated) &&
            IsNativeEntityProgram(program) && !ShaderOverrides.IsOverridden(program.PassName);
    }

    private bool CaptureResidentEntityMesh(NativePipeline pipeline, VAO mesh, MultiTextureMeshRef? shape, int subMesh)
    {
        if (shape?.meshrefs == null || subMesh < 0 || subMesh >= shape.meshrefs.Length ||
            !ReferenceEquals(shape.meshrefs[subMesh], mesh) || !ResidentEntityProgram(out var program) ||
            (program.ubos.ContainsKey("Animation") && residentEntityScope.PoseOffset < 0)) return false;
        var entity = residentEntityScope.Renderer!.entity;
        double x = (float)entity.Pos.X, y = (float)entity.Pos.InternalY, z = (float)entity.Pos.Z;
        float radius = residentEntityPassKind >= 2 ? 3f : (float)entity.FrustumSphereRadius;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z) || !float.IsFinite(radius) || radius < 0 ||
            !RequireDevice().CaptureEntityRow(pipeline, mesh.VaoId, nativeEntityTextures, entity.EntityId, subMesh,
                x, y, z, radius, residentEntityScope.PoseOffset)) return false;
        residentEntityStageCaptured = true;
        foreach (int texture in nativeEntityReads) residentEntityReads.Add(texture);
        return true;
    }

    private bool CaptureResidentEntityPose(UBO buffer, ReadOnlySpan<float> bones, int offset, int size)
    {
        if (!ResidentEntityProgram(out var program) || !UniformBuffers.TryGetValue(buffer, out var owner) ||
            !ReferenceEquals(owner.Adapter, this) || owner.Block is not ("Animation" or "AnimationPrev") ||
            !program.ubos.TryGetValue(owner.Block, out var declared) || !ReferenceEquals(declared, buffer)) return false;
        if (offset != 0 || size <= 0 || size % 64 != 0 || size > buffer.Size || bones.Length < size / sizeof(float))
        {
            if (owner.Block == "Animation") residentEntityScope = residentEntityScope with { PoseOffset = -1 };
            return false;
        }
        if (owner.Block == "AnimationPrev")
            return residentEntityScope.PoseOffset >= 0; // Previous poses come from the same row in the previous frame.
        residentEntityScope = residentEntityScope with { PoseOffset = -1 };
        int count = size / sizeof(float);
        int pose = RequireDevice().BeginEntityPose(count);
        if (pose < 0) return false;
        Span<float> destination = RequireDevice().EntityPoseSpan(pose, count);
        if (destination.Length != count) return false;
        bones[..count].CopyTo(destination);
        residentEntityScope = residentEntityScope with { PoseOffset = pose };
        return true;
    }

    private unsafe bool TryUploadResidentEntityPose(UBO buffer, object data, int offset, int size)
    {
        if (data is not float[] bones || !ResidentEntityProgram(out _)) return false;
        Performance.GpuJointPoses.MaterializePayload(data);
        if (!CaptureResidentEntityPose(buffer, bones, offset, size)) return false;
        InvalidateAnimationPayload(buffer);
        // Keep only the ordinary CPU shadow for refused captures and nonresident draws.
        // No per-draw animation snapshot is allocated unless the compat lane consumes it.
        fixed (float* source = bones)
            RequireDevice().UpdateUniformBuffer(UniformBufferHandle(buffer), (IntPtr)source, offset, size);
        return true;
    }
}
