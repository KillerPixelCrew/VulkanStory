using Silk.NET.Vulkan;
using VulkanStory.Platform.Sdl;
using VulkanStory.Render.Vulkan.Shaders;
using VulkanStory.Contracts;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>
/// Records one colored render-target frame through the retained frame-ring,
/// dynamic-rendering, blit and swapchain path. The optional draw adds the
/// retained translator, program resources and graphics pipeline cache. The SDL
/// window stays hidden, so this is not a visual acceptance test.
/// </summary>
public static unsafe class VulkanPresentPreflight
{
    /// <summary>Runs the explicit hidden rendering, presentation and selected pixel/query/uniform checks preflight.</summary>
    /// <remarks>This method creates native resources and may submit GPU work; it is an opt-in validation entry point.</remarks>
    /// <returns>Null on successful completion, otherwise the failure detail.</returns>
    public static string? Check(bool draw = false, bool meshDraw = false, bool verifyPixels = false,
        bool verifyQueries = false, bool verifyUniforms = false)
    {
        if (verifyQueries && verifyUniforms) return "Select one extended draw scenario per preflight";
        verifyPixels |= verifyUniforms;
        verifyPixels |= verifyQueries;
        meshDraw |= verifyPixels;
        draw |= meshDraw;
        try
        {
            using SdlWindowHost window = SdlWindowHost.Create(
                "VulkanStory present preflight", 128, 96, hidden: true);
            var source = new SdlVulkanWindowSurface(window);
            var options = new VulkanContextOptions
            {
                RequiredInstanceExtensions = source.RequiredInstanceExtensions()
            };
            if (!VulkanContext.TryCreate(options, out VulkanContext? context, out string? reason))
                return reason ?? "Vulkan context creation failed";

            SurfaceKHR surface = default;
            FrameRing? frames = null;
            Swapchain? swapchain = null;
            TextureManager? textures = null;
            RenderTargetManager? targets = null;
            BindlessTextureTable? bindless = null;
            SharedPipelineLayout? sharedLayout = null;
            ShaderProgramResources? program = null;
            GraphicsPipelineCache? pipelines = null;
            MeshManager? meshes = null;
            ReadbackManager? readbacks = null;
            QueryRing? queries = null;
            DescriptorArena? uniformDescriptors = null;
            VulkanBuffer? uniformPlaceholder = null;
            int drawQuery = 0, emptyQuery = 0;
            int meshId = 0;
            try
            {
                if (!source.TryCreate(context!, out surface, out reason))
                    return reason ?? "SDL3 Vulkan surface creation failed";

                frames = new FrameRing(context!);
                // Swapchain.TryCreate owns the surface on entry, including failure.
                SurfaceKHR handoff = surface;
                surface = default;
                if (!Swapchain.TryCreate(context!, handoff, 128, 96, true, frames.Timeline,
                        out swapchain, out reason))
                    return reason ?? "Vulkan swapchain creation failed";
                Swapchain activeSwapchain = swapchain ??
                    throw new InvalidOperationException("Vulkan swapchain creation returned no owner");

                textures = new TextureManager(context!, frames.Uploads);
                bindless = new BindlessTextureTable(context!, textures, frames.Timeline);
                sharedLayout = new SharedPipelineLayout(context!, bindless.Layout);
                RenderTargetManager activeTargets = new(context!, textures);
                targets = activeTargets;
                textures.ScopeOpen = _ => activeTargets.RenderingActive;
                var blit = new BlitPresentPath(context!, textures);
                int colorId = textures.Create(128, 96, Format.R8G8B8A8Unorm);
                VulkanTexture color = textures.Get(colorId)!;
                int framebufferId = activeTargets.Create(128, 96);
                activeTargets.Attach(framebufferId, 0, colorId);

                Pipeline drawPipeline = default;
                if (draw)
                {
                    VertexLayoutDescription vertexLayout = VertexLayoutDescription.Empty;
                    int layoutId = MeshManager.EmptyLayoutId;
                    if (meshDraw)
                    {
                        meshes = new MeshManager(context!, frames.Uploads, frames);
                        meshId = meshes.CreateEmpty(36, 0, 0, 0, 0, 12,
                            null, null, null, null, MeshDrawMode.Triangles, false, false);
                        float[] vertices = [-0.8f, -0.8f, 0, 0.8f, -0.8f, 0, 0, 0.8f, 0];
                        int[] indices = [0, 1, 2];
                        fixed (float* vertexData = vertices)
                            meshes.Write(meshId, MeshManager.BufferXyz, 0, (IntPtr)vertexData, vertices.Length * sizeof(float));
                        fixed (int* indexData = indices)
                            meshes.Write(meshId, -1, 0, (IntPtr)indexData, indices.Length * sizeof(int));
                        layoutId = meshes.LayoutIdOf(meshId);
                        vertexLayout = meshes.LayoutOf(layoutId);
                    }
                    using var compiler = new ShaderCompiler();
                    TranslatedProgram translated = ShaderTranslator.Translate(
                        new[]
                        {
                            new ShaderStageSource
                            {
                                Stage = ShaderStageKind.VertexShader,
                                Filename = "preflight.vert",
                                Code = meshDraw ? """
                                    #version 330 core
                                    layout(location = 0) in vec3 vertexPosition;
                                    void main() { gl_Position = vec4(vertexPosition, 1.0); }
                                    """ : """
                                    #version 330 core
                                    void main() {
                                        vec2 position = vec2(-1.0 + float((gl_VertexID & 1) << 2),
                                                             -1.0 + float((gl_VertexID & 2) << 1));
                                        gl_Position = vec4(position, 0.0, 1.0);
                                    }
                                    """,
                            },
                            new ShaderStageSource
                            {
                                Stage = ShaderStageKind.FragmentShader,
                                Filename = "preflight.frag",
                                Code = verifyUniforms ? """
                                    #version 330 core
                                    layout(std140) uniform TintBlock { vec4 tint; };
                                    layout(location = 0) out vec4 color;
                                    void main() { color = tint; }
                                    """ : """
                                    #version 330 core
                                    layout(location = 0) out vec4 color;
                                    void main() { color = vec4(0.95, 0.55, 0.10, 1.0); }
                                    """,
                            },
                        }, compiler);
                    if (!translated.Success)
                        return "preflight shader translation failed: " + string.Join("; ", translated.Errors);
                    if (!translated.Layout.WrittenFragmentOutputs.Contains(0))
                        return "preflight fragment output 0 was not recorded";

                    program = new ShaderProgramResources(context!, 1, translated, sharedLayout.Layout);
                    pipelines = new GraphicsPipelineCache(context!);
                    var key = new PipelineKey(1, layoutId, 0, 0, PolygonMode.Fill,
                        GlEnums.TopologyClassOf(PrimitiveTopology.TriangleList));
                    var request = new GraphicsPipelineCache.PipelineRequest
                    {
                        Program = program,
                        VertexLayout = vertexLayout,
                        Targets = new RenderTargetFormats(new[] { Format.R8G8B8A8Unorm }, Format.Undefined),
                        Blend = new[] { AttachmentBlend.Default },
                        PolygonMode = PolygonMode.Fill,
                        Topology = PrimitiveTopology.TriangleList,
                    };
                    drawPipeline = pipelines.Get(key, request);
                }

                FrameSlot frame = frames.BeginFrame();
                if (verifyQueries)
                {
                    queries = new QueryRing(context!, frames.Timeline, frames.FramesInFlight);
                    drawQuery = queries.Create();
                    emptyQuery = queries.Create();
                    activeTargets.ScopeOpened = queries.OnScopeOpened;
                    activeTargets.ScopeClosing = queries.OnScopeClosing;
                    activeTargets.ScopeClosed = queries.OnScopeClosed;
                    queries.BeginSlot(frame.Index, frame.CommandBuffer);
                    queries.AddPool(frame.CommandBuffer);
                    queries.Begin(drawQuery, frame.CommandBuffer, scopeOpen: false);
                }
                if (verifyPixels)
                {
                    readbacks = new ReadbackManager(context!, textures, frames);
                    readbacks.BeginSlot(frame.Index);
                }
                bindless.BeginFrame();
                activeTargets.Bind(frame.CommandBuffer, framebufferId);
                activeTargets.ClearColor(frame.CommandBuffer, 0, 0.10f, 0.35f, 0.75f, 1f);
                if (verifyUniforms)
                {
                    uniformDescriptors = new DescriptorArena(context!);
                    uniformPlaceholder = new VulkanBuffer(context!, 16,
                        BufferUsageFlags.UniformBufferBit | BufferUsageFlags.StorageBufferBit,
                        MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
                    new Span<byte>((void*)uniformPlaceholder.Mapped, 16).Clear();
                    var uniformBuffers = new ClientUniformBufferManager();
                    int tintId = uniformBuffers.CreateUniformBuffer(1, 0, "TintBlock", 16);
                    ClientUniformBuffer tint = uniformBuffers.Buffers[tintId];
                    int binding = program!.Interface.UniformBlocks.Single(block => block.BlockName == "TintBlock").Binding;
                    float[] first = [1, 0, 0, 1], second = [0, 1, 0, 1];
                    fixed (float* data = first) uniformBuffers.UpdateUniformBuffer(tintId, (IntPtr)data, 0, 16);
                    uint firstOffset = BindUniformSnapshot(context!, frames, sharedLayout!, uniformDescriptors,
                        uniformPlaceholder, tint, binding);
                    RecordDraw(context!, frame.CommandBuffer, drawPipeline, meshes, meshId,
                        new Rect2D(new Offset2D(0, 0), new Extent2D(64, 96)));
                    activeTargets.EndRendering(frame.CommandBuffer);
                    frames.SubmitPartial();
                    if (!tint.HasSnapshotFor(1)) return "Partial submission invalidated the same-frame uniform snapshot";
                    uint reused = BindUniformSnapshot(context!, frames, sharedLayout!, uniformDescriptors,
                        uniformPlaceholder, tint, binding);
                    if (reused != firstOffset) return "Unchanged uniform block allocated a second snapshot";
                    fixed (float* data = second) uniformBuffers.UpdateUniformBuffer(tintId, (IntPtr)data, 0, 16);
                    uint secondOffset = BindUniformSnapshot(context!, frames, sharedLayout!, uniformDescriptors,
                        uniformPlaceholder, tint, binding);
                    if (secondOffset == firstOffset) return "Changed uniform block aliased an earlier draw's snapshot";
                    activeTargets.EnsureRendering(frame.CommandBuffer);
                    RecordDraw(context!, frame.CommandBuffer, drawPipeline, meshes, meshId,
                        new Rect2D(new Offset2D(64, 0), new Extent2D(64, 96)));
                }
                else if (draw) RecordDraw(context!, frame.CommandBuffer, drawPipeline, meshes, meshId);
                activeTargets.EndRendering(frame.CommandBuffer);
                if (queries != null)
                {
                    // The same query resumes on a new segment in a new scope.
                    activeTargets.EnsureRendering(frame.CommandBuffer);
                    RecordDraw(context!, frame.CommandBuffer, drawPipeline, meshes, meshId);
                    activeTargets.EndRendering(frame.CommandBuffer);
                    queries.End(drawQuery, frame.FrameValue, frame.CommandBuffer);
                    queries.Begin(emptyQuery, frame.CommandBuffer, scopeOpen: false);
                    activeTargets.EnsureRendering(frame.CommandBuffer);
                    activeTargets.EndRendering(frame.CommandBuffer);
                    queries.End(emptyQuery, frame.FrameValue, frame.CommandBuffer);
                    if (queries.IsResultAvailable(drawQuery))
                        return "Query reported availability before its command buffer was submitted";
                }
                ReadbackTicket? ticket = verifyPixels ? readbacks!.CopyToHost(color, 0, 0, 128, 96,
                    ImageAspectFlags.ColorBit, 128UL * 96 * 4) : null;
                ulong renderValue = frames.EndFrame();
                if (ticket is { } copy)
                {
                    var pixels = new byte[128 * 96 * 4];
                    fixed (byte* destination = pixels) readbacks!.WaitAndCopy(copy, (IntPtr)destination);
                    TextureCaptureData? capture = TextureDump.ToParityReadback(color.Format,
                        TextureDump.GlInternalFormatOf(color.Format), 128, 96, pixels);
                    if (capture?.Bytes is not { } decoded) return "RGBA8 capture decoding did not return colour bytes";
                    pixels = decoded;
                    string? pixelError = (verifyUniforms
                        ? CheckPixel(pixels, 48, 48, 255, 0, 0, 255, "first uniform snapshot") ??
                          CheckPixel(pixels, 80, 48, 0, 255, 0, 255, "second uniform snapshot")
                        : CheckPixel(pixels, 64, 48, 242, 140, 26, 255, "triangle centre")) ??
                        CheckPixel(pixels, 2, 2, 26, 89, 191, 255, "clear outside triangle");
                    if (pixelError != null) return pixelError;
                    if (queries != null)
                    {
                        if (!queries.IsResultAvailable(drawQuery) || !queries.IsResultAvailable(emptyQuery))
                            return "Submitted occlusion query did not become available after readback completion";
                        int samples = queries.GetResult(drawQuery);
                        if (samples <= 0 || samples == int.MaxValue || queries.GetResult(emptyQuery) != 0)
                            return "Occlusion query did not distinguish indexed draws from an empty scope";
                        queries.Delete(drawQuery);
                        queries.Delete(emptyQuery);
                        if (queries.IsResultAvailable(drawQuery)) return "Deleted query remains available";
                    }
                }

                if (!activeSwapchain.TryAcquire(out PresentTarget target))
                    return activeSwapchain.RebuildFailure ?? "Vulkan swapchain image acquisition failed";

                CommandBuffer presentCommands = frames.BeginPresentCommands();
                blit.Record(presentCommands, target, color);
                ulong presentValue = frames.SubmitPresent(
                    target.AcquireSemaphore, blit.AcquireWaitStage, renderValue, target.PresentSemaphore);
                activeSwapchain.NotePresentSubmitted(target, presentValue);
                activeSwapchain.Present(target);
                return null;
            }
            finally
            {
                swapchain?.Dispose();
                // The indexed buffers must outlive the submitted draw. The
                // preflight owns this device and deliberately waits at teardown.
                if (meshes != null) VulkanResult.Check(context!.WaitDeviceIdle(), "mesh preflight teardown");
                meshes?.Dispose();
                readbacks?.Dispose();
                queries?.Dispose();
                uniformDescriptors?.Dispose();
                uniformPlaceholder?.Dispose();
                if (surface.Handle != 0) WindowSurface.Destroy(context!, surface);
                targets?.Dispose();
                pipelines?.Dispose();
                program?.Dispose();
                sharedLayout?.Dispose();
                bindless?.Dispose();
                textures?.Dispose();
                frames?.Dispose();
                context!.Dispose();
            }
        }
        catch (Exception error)
        {
            return error.GetType().Name + ": " + error.Message;
        }
    }

    private static string? CheckPixel(byte[] pixels, int x, int y,
        byte r, byte g, byte b, byte a, string label)
    {
        int offset = (y * 128 + x) * 4;
        byte[] expected = [r, g, b, a];
        for (int channel = 0; channel < 4; channel++)
            if (Math.Abs(pixels[offset + channel] - expected[channel]) > 2)
                return $"{label} pixel mismatch: actual RGBA={pixels[offset]},{pixels[offset + 1]}," +
                    $"{pixels[offset + 2]},{pixels[offset + 3]}; expected={r},{g},{b},{a}";
        return null;
    }

    private static uint BindUniformSnapshot(VulkanContext context, FrameRing frames, SharedPipelineLayout shared,
        DescriptorArena arena, VulkanBuffer placeholder, ClientUniformBuffer block, int blockBinding)
    {
        if (!block.TrySnapshot(1, frames.Current, out uint offset))
            throw new InvalidOperationException("Uniform preflight arena exhausted");
        var buffers = new BufferBindingValue[SetConvention.StorageSetBindingCount];
        for (int binding = 0; binding < buffers.Length; binding++)
            buffers[binding] = new((uint)binding, placeholder.Handle, 0, placeholder.Size, placeholder.Id);
        buffers[blockBinding] = new((uint)blockBinding, frames.UniformBuffer, offset, (ulong)block.Shadow.Length);
        DescriptorSet set = arena.Get(new DescriptorSetContents(0, SetConvention.StorageSet,
            Array.Empty<SamplerBindingValue>(), buffers), shared.StorageSetLayout);
        uint recordOffset = 0;
        context.Api.CmdBindDescriptorSets(frames.Current.CommandBuffer, PipelineBindPoint.Graphics,
            shared.Layout, SetConvention.StorageSet, 1, &set, 1, &recordOffset);
        return offset;
    }

    private static void RecordDraw(VulkanContext context, CommandBuffer commandBuffer, Pipeline pipeline,
        MeshManager? meshes, int meshId, Rect2D? clip = null)
    {
        Vk api = context.Api;
        api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline);
        var viewport = new Viewport(0, 0, 128, 96, 0f, 1f);
        var scissor = clip ?? new Rect2D(new Offset2D(0, 0), new Extent2D(128, 96));
        api.CmdSetViewport(commandBuffer, 0, 1, &viewport);
        api.CmdSetScissor(commandBuffer, 0, 1, &scissor);
        api.CmdSetCullMode(commandBuffer, CullModeFlags.None);
        api.CmdSetFrontFace(commandBuffer, RenderLimits.FrontFace);
        api.CmdSetPrimitiveTopology(commandBuffer, PrimitiveTopology.TriangleList);
        api.CmdSetDepthTestEnable(commandBuffer, false);
        api.CmdSetDepthWriteEnable(commandBuffer, false);
        api.CmdSetDepthCompareOp(commandBuffer, CompareOp.Always);
        api.CmdSetStencilTestEnable(commandBuffer, false);
        api.CmdSetStencilOp(commandBuffer, StencilFaceFlags.FaceFrontAndBack,
            StencilOp.Keep, StencilOp.Keep, StencilOp.Keep, CompareOp.Always);
        api.CmdSetStencilCompareMask(commandBuffer, StencilFaceFlags.FaceFrontAndBack, 0xff);
        api.CmdSetStencilWriteMask(commandBuffer, StencilFaceFlags.FaceFrontAndBack, 0xff);
        api.CmdSetStencilReference(commandBuffer, StencilFaceFlags.FaceFrontAndBack, 0);
        api.CmdSetLineWidth(commandBuffer, 1f);
        if (meshes is null) api.CmdDraw(commandBuffer, 3, 1, 0, 0);
        else meshes.Draw(commandBuffer, meshId);
    }
}
