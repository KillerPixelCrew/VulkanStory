using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VulkanStory.Game.Performance;

/// <summary>One instanced GUI draw of the visible 256x256 map pages from a Vulkan array texture.
/// Slots visible in a frame are never overwritten by another page in that frame.</summary>
internal sealed class MapPageRenderer : IDisposable
{
    private sealed class Slot(int index) { internal int Index = index; internal long Revision = -1, LastUse; }
    private readonly ICoreClientAPI api;
    private readonly GameGraphicsAdapter graphics;
    private readonly int texture;
    private readonly IShaderProgram shader;
    private readonly MeshData mesh;
    private readonly MeshRef uploaded;
    private readonly Dictionary<(int X, int Z), Slot> slots = new();
    private Vec2f view = new();
    private readonly Vec3d world = new();
    private long frame;
    internal MapPageRenderer(ICoreClientAPI api, GameGraphicsAdapter graphics)
    {
        this.api = api; this.graphics = graphics;
        texture = graphics.CreateMapPageArray(MapPageStore.Size, MapPageStore.Capacity);
        shader = api.Shader.NewShaderProgram();
        shader.VertexShader = api.Shader.NewShader(EnumShaderType.VertexShader);
        shader.VertexShader.Code = Vertex;
        shader.FragmentShader = api.Shader.NewShader(EnumShaderType.FragmentShader);
        shader.FragmentShader.Code = Fragment;
        api.Shader.RegisterMemoryShaderProgram("vulkanstorymappages", shader);
        if (!shader.Compile())
        { shader.Dispose(); graphics.DeleteTexture(texture); throw new InvalidOperationException("Map page shader compilation failed."); }
        mesh = new MeshData(4, 6, withNormals: false, withUv: false, withRgba: false, withFlags: false)
        {
            xyz = [0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0], VerticesCount = 4,
            Indices = [0, 1, 2, 0, 2, 3], IndicesCount = 6,
            CustomFloats = new CustomMeshDataPartFloat(MapPageStore.Capacity * 5)
            {
                Count = MapPageStore.Capacity * 5, Instanced = true,
                InterleaveSizes = [4, 1], InterleaveOffsets = [0, 16], InterleaveStride = 20,
            },
        };
        try { uploaded = api.Render.UploadMesh(mesh); }
        catch { shader.Dispose(); graphics.DeleteTexture(texture); throw; }
    }
    internal void Draw(MapPageStore store, HashSet<(int X, int Z)> visible, GuiElementMap map)
    {
        frame++;
        int count = 0;
        foreach (var key in visible)
        {
            slots.TryGetValue(key, out Slot? slot);
            MapPageStore.Image? image = store.Snapshot(key, slot?.Revision ?? -1);
            if (image == null) continue;
            if (slot == null)
            {
                if (slots.Count < MapPageStore.Capacity) slot = new Slot(slots.Count);
                else
                {
                    var victim = slots.Where(p => !visible.Contains(p.Key)).MinBy(p => p.Value.LastUse);
                    slot = victim.Value; slots.Remove(victim.Key);
                    slot.Revision = -1;
                }
                slots.Add(key, slot);
            }
            if (slot.Revision != image.Value.Revision)
            {
                graphics.UploadMapPage(texture, slot.Index, MapPageStore.Size, image.Value.Pixels);
                slot.Revision = image.Value.Revision;
            }
            slot.LastUse = frame;
            world.X = key.X * MapPageStore.Size; world.Z = key.Z * MapPageStore.Size;
            map.TranslateWorldPosToViewPos(world, ref view);
            int offset = count++ * 5;
            var values = mesh.CustomFloats.Values;
            values[offset] = (float)map.Bounds.renderX + view.X; values[offset + 1] = (float)map.Bounds.renderY + view.Y;
            values[offset + 2] = values[offset + 3] = MapPageStore.Size * map.ZoomLevel;
            values[offset + 4] = slot.Index;
        }
        if (count == 0) return;
        mesh.CustomFloats.Count = count * 5;
        api.Render.UpdateMesh(uploaded, mesh);
        IShaderProgram? previous = api.Render.CurrentActiveShader;
        int oldTexture = graphics.Stated.TextureAt(0), oldSampler = graphics.Stated.SamplerAt(0);
        bool blend = graphics.Stated.BlendEnabled;
        previous?.Stop();
        try
        {
            api.Render.GlToggleBlend(true);
            shader.Use();
            shader.UniformMatrix("projectionMatrix", api.Render.CurrentProjectionMatrix);
            shader.UniformMatrix("modelViewMatrix", api.Render.CurrentModelviewMatrix);
            shader.BindTexture2D("mapPages", texture, 0);
            api.Render.RenderMeshInstanced(uploaded, count);
        }
        finally
        {
            shader.Stop(); api.Render.GlToggleBlend(blend);
            graphics.Stated.BindTexture(0, oldTexture); graphics.Stated.BindSampler(0, oldSampler);
            previous?.Use();
        }
    }
    public void Dispose() { uploaded.Dispose(); mesh.Dispose(); shader.Dispose(); graphics.DeleteTexture(texture); }
    private const string Vertex = """
        #version 330 core
        layout(location=0) in vec3 vertexPosition;
        layout(location=1) in vec4 instanceRect;
        layout(location=2) in float instanceLayer;
        uniform mat4 projectionMatrix;
        uniform mat4 modelViewMatrix;
        out vec2 texCoord;
        flat out float layerIndex;
        void main() {
            vec2 screen = instanceRect.xy + vertexPosition.xy * instanceRect.zw;
            gl_Position = projectionMatrix * modelViewMatrix * vec4(screen, 50.0, 1.0);
            texCoord = vertexPosition.xy;
            layerIndex = instanceLayer;
        }
        """;
    private const string Fragment = """
        #version 330 core
        uniform sampler2DArray mapPages;
        in vec2 texCoord;
        flat in float layerIndex;
        out vec4 outColor;
        void main() { outColor = texture(mapPages, vec3(texCoord, layerIndex)); }
        """;
}
