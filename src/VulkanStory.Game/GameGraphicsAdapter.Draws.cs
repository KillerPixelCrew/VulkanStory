using Vintagestory.Client;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    internal int CurrentTargetId { get; set; } = PassDeclaration.DefaultFramebuffer;
    internal PassDeclaration? StatedPass { get; set; }
    internal long StatedDraws { get; private set; }
    private readonly HashSet<int> reportedDrawRefusals = new();
    private string? lastStatedDrawRefusal;

    private bool RecordStatedDraw(VAO? mesh, int instances, int[]? starts, int[]? sizes, int groups, bool requirePipeline = false)
    {
        var renderer = RequireDevice();
        lastStatedDrawRefusal = null;
        if (mesh is not null && (mesh.VaoId == 0 || mesh.Disposed)) return false;
        if (StatedProgram <= 0) { RejectSceneDraw("stated draw has no program"); return false; }
        int id = mesh is null ? 0 : MeshHandle(mesh);
        RuntimeStats.drawCallsCount++;
        PassDeclaration? declaration = StatedPass?.FramebufferId == CurrentTargetId ? StatedPass : StageDrawDeclaration();
        bool drawn = StatedDraw.Record(renderer, Stated, StatedProgram, CurrentTargetId,
            id, instances, starts, sizes, groups, out string? refusal, declaration, requirePipeline);
        lastStatedDrawRefusal = refusal;
        if (drawn) { StatedDraws++; return true; }
        RuntimeStats.drawCallsCount--;
        RejectSceneDraw("stated draw: " + (refusal ?? "no refusal detail"));
        if (refusal != null && reportedDrawRefusals.Add(StatedProgram))
            Console.Error.WriteLine("VulkanStory: draw of program #" + StatedProgram + " dropped: " + refusal);
        return false;
    }
    internal void RenderMesh(MeshRef mesh)
    {
        RequireDevice();
        RuntimeStats.drawCallsCount++;
        var vao = (VAO)mesh;
        if (vao.VaoId == 0 || vao.Disposed)
            throw new ArgumentException(vao.VaoId == 0 ? "Fatal: Trying to render an uninitialized mesh" : "Fatal: Trying to render a disposed mesh");
        RuntimeStats.drawCallsCount--; // Retained stated helper counts only draws it records.
        RecordStatedDraw(vao, 1, null, null, 0);
    }
    internal void RenderMesh(MeshRef mesh, int[] starts, int[] sizes, int groups, bool useSsbo)
    {
        RequireDevice();
        // SSBO storage/index selection is part of the retained mesh's native layout.
        if (TryDrawChunkPoolNative((VAO)mesh, starts, sizes, groups)) return;
        if (TryDrawDecalPoolNative(mesh, starts, sizes, groups)) return;
        RecordStatedDraw((VAO)mesh, 1, starts, sizes, groups);
    }
    internal void RenderMeshInstanced(MeshRef mesh, int quantity)
    {
        RequireDevice();
        bool motion = false;
        if (quantity > 0 && aoTemporal?.EntityMotion.Enabled == true && aoTemporal.State.JitterActive &&
            Vintagestory.Client.NoObf.ShaderProgramBase.CurrentShaderProgram is { PassName: "instanced" } program &&
            program.HasUniform("prevProjectionMatrix"))
        {
            aoTemporal.InstanceMotion.ApplyPassUniforms(program);
            motion = BeginMotionWrite(aoTemporal);
        }
        try { if (quantity > 0) RecordStatedDraw((VAO)mesh, quantity, null, null, 0); }
        finally { if (motion) EndMotionWrite(); }
    }
    internal void RenderFullscreenTriangle(MeshRef mesh) => RecordStatedDraw(null, 1, null, null, 0);
}
