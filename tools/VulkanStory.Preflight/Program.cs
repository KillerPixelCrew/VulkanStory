using VulkanStory.Render.Vulkan.Core;

bool sdlSurface = args.Length == 1 && args[0] == "--sdl-surface";
bool sdlSwapchain = args.Length == 1 && args[0] == "--sdl-swapchain";
bool sdlPresent = args.Length == 1 && args[0] == "--sdl-present";
bool sdlDraw = args.Length == 1 && args[0] == "--sdl-draw";
bool meshBuffers = args.Length == 1 && args[0] == "--mesh-buffers";
bool sdlMesh = args.Length == 1 && args[0] == "--sdl-mesh";
bool meshPixels = args.Length == 1 && args[0] == "--sdl-mesh-readback";
bool meshQueries = args.Length == 1 && args[0] == "--sdl-mesh-query";
bool uniformDraw = args.Length == 1 && args[0] == "--sdl-uniform-snapshots";
bool fullDevice = args.Length == 1 && args[0] == "--sdl-device";
if (args.Length > 0 && !sdlSurface && !sdlSwapchain && !sdlPresent && !sdlDraw && !meshBuffers && !sdlMesh && !meshPixels && !meshQueries && !uniformDraw && !fullDevice)
{
    Console.Error.WriteLine("Usage: VulkanStory.Preflight [--sdl-surface|--sdl-swapchain|--sdl-present|--sdl-draw|--mesh-buffers|--sdl-mesh|--sdl-mesh-readback|--sdl-mesh-query|--sdl-uniform-snapshots|--sdl-device]");
    return 2;
}

string? failure = fullDevice ? VulkanDevicePreflight.Check() :
    meshBuffers ? VulkanMeshPreflight.Check() :
    uniformDraw ? VulkanPresentPreflight.Check(verifyUniforms: true) :
    meshQueries ? VulkanPresentPreflight.Check(verifyQueries: true) :
    meshPixels ? VulkanPresentPreflight.Check(verifyPixels: true) :
    sdlMesh ? VulkanPresentPreflight.Check(meshDraw: true) :
    sdlDraw ? VulkanPresentPreflight.Check(draw: true) :
    sdlPresent ? VulkanPresentPreflight.Check() :
    sdlSwapchain ? VulkanSwapchainPreflight.Check() :
    sdlSurface ? VulkanWindowPreflight.Check() : VulkanPreflight.Check();
if (failure is not null)
{
    Console.Error.WriteLine("VulkanStory preflight failed: " + failure);
    return 1;
}

Console.WriteLine(fullDevice
    ? "VulkanStory preflight: full device initialized, cleared/read back/presented four frames, and tore down before SDL."
    : uniformDraw
    ? "VulkanStory preflight: distinct uniform draw snapshots survived partial submission and pixel readback, then presented."
    : meshQueries
    ? "VulkanStory preflight: segmented draw and empty occlusion queries verified with indexed pixels and presentation."
    : meshPixels
    ? "VulkanStory preflight: indexed triangle and clear pixels verified through retained readback, then presented."
    : sdlMesh
    ? "VulkanStory preflight: retained indexed mesh draw submitted and presented through SDL3 Vulkan."
    : meshBuffers
    ? "VulkanStory preflight: retained mesh allocation, mapped writes and SSBO quad indices passed."
    : sdlDraw
    ? "VulkanStory preflight: a Vulkan shader pipeline drew into an SDL3 render target and presented."
    : sdlPresent
    ? "VulkanStory preflight: a colored SDL3 Vulkan render target was submitted and presented."
    : sdlSwapchain
    ? "VulkanStory preflight: SDL3 Vulkan swapchain was created and released."
    : sdlSurface
        ? "VulkanStory preflight: SDL3 Vulkan surface and device were created and released."
        : "VulkanStory preflight: a headless Vulkan device was created and released.");
return 0;
