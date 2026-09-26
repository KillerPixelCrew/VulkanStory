// Small ABI boundary for the signed AMD FidelityFX SDK 1.1.4 Vulkan runtime.
// The SDK headers and binary are supplied from _ref/fsr-vulkan at build time.
#include <windows.h>
#include <cstdint>
#include <cstddef>
#include <cmath>
#include <new>
#include <cstring>
#include <cstdio>
#include "ffx_api/ffx_api.h"
#include "ffx_api/ffx_upscale.h"
#include "ffx_api/ffx_framegeneration.h"
#include "ffx_api/vk/ffx_api_vk.h"

struct OptimumFsr3Image {
    VkImage image;
    uint32_t width, height;
    VkFormat format;
};
struct OptimumFsr3Frame {
    VkCommandBuffer commands;
    OptimumFsr3Image color, depth, motion, output;
    float jitterX, jitterY, deltaMs, nearPlane, farPlane, fovRadians;
    uint32_t reset;
};
static_assert(sizeof(OptimumFsr3Image) == 24);
static_assert(sizeof(OptimumFsr3Frame) == 136);
static_assert(offsetof(OptimumFsr3Frame, jitterX) == 104);
struct OptimumFsr3Context {
    ffxContext effect = nullptr;
    ffxCreateContextDescUpscale upscale{};
    ffxCreateBackendVKDesc backend{};
    uint32_t width = 0, height = 0;
};

struct OptimumFsr3FgFrame {
    VkCommandBuffer commands;
    OptimumFsr3Image color, depth, motion, output, hudless;
    float jitterX, jitterY, deltaMs, nearPlane, farPlane, fovRadians;
    uint64_t frameId;
    uint32_t reset;
    float cameraPos[3], cameraUp[3], cameraRight[3], cameraForward[3];
    OptimumFsr3Image ui;
    void* swapchainContext;
};
static_assert(sizeof(OptimumFsr3FgFrame) == 248);

struct OptimumFsr3Swapchain;
struct OptimumFsr3FgContext {
    ffxContext effect = nullptr;
    ffxCreateContextDescFrameGeneration frameGeneration{};
    ffxCreateContextDescFrameGenerationHudless hudlessFormat{};
    ffxCreateBackendVKDesc backend{};
    uint32_t width = 0, height = 0;
    VkSwapchainKHR chain = VK_NULL_HANDLE;
    OptimumFsr3Swapchain* swapchain = nullptr;
};

// FFX owns all five Vulkan swapchain entry points while its proxy is active.
// Keep the queried function table and context together so callers cannot pair
// native acquire/present with images returned by the proxy.
struct OptimumFsr3Swapchain {
    ffxContext context = nullptr;
    ffxQueryDescSwapchainReplacementFunctionsVK functions{};
    VkDevice device = VK_NULL_HANDLE;
    VkSwapchainKHR chain = VK_NULL_HANDLE;
    OptimumFsr3FgContext* activeFg = nullptr;
};

extern "C" __declspec(dllexport) int OptimumFsr3FgDisable(
    OptimumFsr3FgContext* context, OptimumFsr3Swapchain* swapchain);

static HMODULE sdk;
static PfnFfxCreateContext createContext;
static PfnFfxDestroyContext destroyContext;
static PfnFfxQuery query;
static PfnFfxDispatch dispatch;
static PfnFfxConfigure configure;
static PFN_vkGetDeviceProcAddr vulkanGetDeviceProcAddr;

static PFN_vkVoidFunction VKAPI_PTR Fsr3GetDeviceProcAddr(VkDevice device, const char* name) {
    auto function = vulkanGetDeviceProcAddr(device, name);
    // The SDK 1.1.4 Vulkan backend calls this KHR alias unconditionally. Vulkan
    // 1.1+ devices may expose only the promoted core entry point.
    if (!function && std::strcmp(name, "vkGetBufferMemoryRequirements2KHR") == 0)
        function = vulkanGetDeviceProcAddr(device, "vkGetBufferMemoryRequirements2");
    return function;
}

extern "C" __declspec(dllexport) int OptimumFsr3Open(HMODULE runtime) {
    if (!runtime) return -1;
    if (sdk) return sdk == runtime ? 0 : -2;
    auto create = reinterpret_cast<PfnFfxCreateContext>(GetProcAddress(runtime, "ffxCreateContext"));
    auto destroy = reinterpret_cast<PfnFfxDestroyContext>(GetProcAddress(runtime, "ffxDestroyContext"));
    auto queryFunction = reinterpret_cast<PfnFfxQuery>(GetProcAddress(runtime, "ffxQuery"));
    auto dispatchFunction = reinterpret_cast<PfnFfxDispatch>(GetProcAddress(runtime, "ffxDispatch"));
    auto configureFunction = reinterpret_cast<PfnFfxConfigure>(GetProcAddress(runtime, "ffxConfigure"));
    if (!create || !destroy || !queryFunction || !dispatchFunction || !configureFunction) return -2;
    sdk = runtime;
    createContext = create;
    destroyContext = destroy;
    query = queryFunction;
    dispatch = dispatchFunction;
    configure = configureFunction;
    return 0;
}

extern "C" __declspec(dllexport) int OptimumFsr3Plan(
    uint32_t displayWidth, uint32_t displayHeight, uint32_t quality,
    uint32_t* renderWidth, uint32_t* renderHeight) {
    if (!query || !renderWidth || !renderHeight) return -1;
    ffxQueryDescUpscaleGetRenderResolutionFromQualityMode desc{};
    desc.header.type = FFX_API_QUERY_DESC_TYPE_UPSCALE_GETRENDERRESOLUTIONFROMQUALITYMODE;
    desc.displayWidth = displayWidth;
    desc.displayHeight = displayHeight;
    desc.qualityMode = quality;
    desc.pOutRenderWidth = renderWidth;
    desc.pOutRenderHeight = renderHeight;
    return static_cast<int>(query(nullptr, &desc.header));
}

extern "C" __declspec(dllexport) int OptimumFsr3Create(
    VkDevice device, VkPhysicalDevice physical, uint32_t width, uint32_t height,
    OptimumFsr3Context** result) {
    if (!createContext || !result || !device || !physical || !width || !height) return -1;
    auto* value = new (std::nothrow) OptimumFsr3Context();
    if (!value) return -2;
    value->width = width;
    value->height = height;
    value->upscale.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE;
    value->upscale.header.pNext = &value->backend.header;
    value->upscale.flags = FFX_UPSCALE_ENABLE_HIGH_DYNAMIC_RANGE | FFX_UPSCALE_ENABLE_AUTO_EXPOSURE;
    value->upscale.maxRenderSize = {width, height};
    value->upscale.maxUpscaleSize = {width, height};
    value->backend.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_VK;
    value->backend.vkDevice = device;
    value->backend.vkPhysicalDevice = physical;
    HMODULE vulkan = GetModuleHandleW(L"vulkan-1.dll");
    vulkanGetDeviceProcAddr = vulkan ? reinterpret_cast<PFN_vkGetDeviceProcAddr>(
        GetProcAddress(vulkan, "vkGetDeviceProcAddr")) : nullptr;
    if (!vulkanGetDeviceProcAddr || !Fsr3GetDeviceProcAddr(device, "vkGetBufferMemoryRequirements2KHR")) {
        delete value;
        return -3;
    }
    value->backend.vkDeviceProcAddr = Fsr3GetDeviceProcAddr;
    int code = static_cast<int>(createContext(&value->effect, &value->upscale.header, nullptr));
    if (code != 0) { delete value; return code; }
    *result = value;
    return 0;
}

static FfxApiResource Resource(OptimumFsr3Image image, bool output = false) {
    FfxApiResource resource{};
    resource.resource = reinterpret_cast<void*>(image.image);
    resource.description.type = FFX_API_RESOURCE_TYPE_TEXTURE2D;
    resource.description.width = image.width;
    resource.description.height = image.height;
    resource.description.depth = 1;
    resource.description.mipCount = 1;
    resource.description.usage = output ? FFX_API_RESOURCE_USAGE_UAV :
        image.format == VK_FORMAT_D32_SFLOAT ? FFX_API_RESOURCE_USAGE_DEPTHTARGET :
        FFX_API_RESOURCE_USAGE_READ_ONLY;
    resource.state = output ? FFX_API_RESOURCE_STATE_UNORDERED_ACCESS : FFX_API_RESOURCE_STATE_COMPUTE_READ;
    switch (image.format) {
    case VK_FORMAT_R16G16B16A16_SFLOAT: resource.description.format = FFX_API_SURFACE_FORMAT_R16G16B16A16_FLOAT; break;
    case VK_FORMAT_R16G16_SFLOAT: resource.description.format = FFX_API_SURFACE_FORMAT_R16G16_FLOAT; break;
    case VK_FORMAT_D32_SFLOAT: resource.description.format = FFX_API_SURFACE_FORMAT_R32_FLOAT; break;
    case VK_FORMAT_R8G8B8A8_UNORM: resource.description.format = FFX_API_SURFACE_FORMAT_R8G8B8A8_UNORM; break;
    default: resource.description.format = FFX_API_SURFACE_FORMAT_UNKNOWN; break;
    }
    return resource;
}

extern "C" __declspec(dllexport) int OptimumFsr3Evaluate(
    OptimumFsr3Context* context, const OptimumFsr3Frame* frame) {
    if (!context || !frame || !dispatch) return -1;
    if (frame->color.format != VK_FORMAT_R16G16B16A16_SFLOAT ||
        frame->depth.format != VK_FORMAT_D32_SFLOAT ||
        frame->motion.format != VK_FORMAT_R16G16_SFLOAT ||
        frame->output.format != VK_FORMAT_R16G16B16A16_SFLOAT) return -2;
    ffxDispatchDescUpscale desc{};
    desc.header.type = FFX_API_DISPATCH_DESC_TYPE_UPSCALE;
    desc.commandList = frame->commands;
    desc.color = Resource(frame->color);
    desc.depth = Resource(frame->depth);
    desc.motionVectors = Resource(frame->motion);
    desc.output = Resource(frame->output, true);
    desc.jitterOffset = {frame->jitterX, frame->jitterY};
    desc.motionVectorScale = {1.0f, 1.0f};
    desc.renderSize = {frame->color.width, frame->color.height};
    desc.upscaleSize = {frame->output.width, frame->output.height};
    desc.frameTimeDelta = frame->deltaMs > 0 ? frame->deltaMs : 16.667f;
    desc.preExposure = 1.0f;
    desc.reset = frame->reset != 0;
    desc.cameraNear = frame->nearPlane;
    desc.cameraFar = frame->farPlane;
    desc.cameraFovAngleVertical = frame->fovRadians;
    desc.viewSpaceToMetersFactor = 1.0f;
    int code = static_cast<int>(dispatch(&context->effect, &desc.header));
    return code;
}

extern "C" __declspec(dllexport) int OptimumFsr3Destroy(OptimumFsr3Context* context) {
    if (!context) return 0;
    int code = context->effect && destroyContext
        ? static_cast<int>(destroyContext(&context->effect, nullptr)) : 0;
    delete context;
    return code;
}

extern "C" __declspec(dllexport) int OptimumFsr3SwapchainCreate(
    VkPhysicalDevice physical, VkDevice device, VkQueue gameQueue, VkQueue asyncQueue,
    VkQueue presentQueue, VkQueue acquireQueue, uint32_t queueFamily,
    const VkSwapchainCreateInfoKHR* info, OptimumFsr3Swapchain** result) {
    if (!createContext || !query || !destroyContext || !physical || !device ||
        !gameQueue || !asyncQueue || !presentQueue || !acquireQueue ||
        !info || !result || info->oldSwapchain) return -1;
    *result = nullptr;
    auto* value = new (std::nothrow) OptimumFsr3Swapchain();
    if (!value) return -2;
    value->device = device;
    ffxCreateContextDescFrameGenerationSwapChainVK desc{};
    desc.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FGSWAPCHAIN_VK;
    desc.physicalDevice = physical;
    desc.device = device;
    desc.swapchain = &value->chain;
    desc.createInfo = *info;
    desc.createInfo.oldSwapchain = VK_NULL_HANDLE;
    // The FFX Vulkan proxy requires four distinct VkQueue handles even when
    // async frame-generation workloads are disabled. They can share one family.
    desc.gameQueue = {gameQueue, queueFamily, nullptr};
    desc.asyncComputeQueue = {asyncQueue, queueFamily, nullptr};
    desc.presentQueue = {presentQueue, queueFamily, nullptr};
    desc.imageAcquireQueue = {acquireQueue, queueFamily, nullptr};
    int code = static_cast<int>(createContext(&value->context, &desc.header, nullptr));
    if (code != 0) { delete value; return code; }
    value->functions.header.type = FFX_API_QUERY_DESC_TYPE_FGSWAPCHAIN_FUNCTIONS_VK;
    code = static_cast<int>(query(&value->context, &value->functions.header));
    if (code != 0 || !value->functions.pOutCreateSwapchainFFXAPI ||
        !value->functions.pOutDestroySwapchainFFXAPI ||
        !value->functions.pOutGetSwapchainImagesKHR ||
        !value->functions.pOutAcquireNextImageKHR ||
        !value->functions.pOutQueuePresentKHR) {
        destroyContext(&value->context, nullptr);
        delete value;
        return code != 0 ? code : -3;
    }
    *result = value;
    return 0;
}

extern "C" __declspec(dllexport) VkSwapchainKHR OptimumFsr3SwapchainHandle(
    OptimumFsr3Swapchain* value) { return value ? value->chain : VK_NULL_HANDLE; }

extern "C" __declspec(dllexport) VkResult OptimumFsr3SwapchainRecreate(
    OptimumFsr3Swapchain* value, const VkSwapchainCreateInfoKHR* info,
    VkSwapchainKHR* chain) {
    if (!value || !info || !chain) return VK_ERROR_INITIALIZATION_FAILED;
    VkResult code = value->functions.pOutCreateSwapchainFFXAPI(
        value->device, info, nullptr, chain, value->context);
    if (code == VK_SUCCESS) value->chain = *chain;
    return code;
}

extern "C" __declspec(dllexport) VkResult OptimumFsr3SwapchainGetImages(
    OptimumFsr3Swapchain* value, VkSwapchainKHR chain, uint32_t* count, VkImage* images) {
    return value && count ? value->functions.pOutGetSwapchainImagesKHR(
        value->device, chain, count, images) : VK_ERROR_INITIALIZATION_FAILED;
}

extern "C" __declspec(dllexport) VkResult OptimumFsr3SwapchainAcquire(
    OptimumFsr3Swapchain* value, VkSwapchainKHR chain, uint64_t timeout,
    VkSemaphore semaphore, VkFence fence, uint32_t* imageIndex) {
    return value && imageIndex ? value->functions.pOutAcquireNextImageKHR(
        value->device, chain, timeout, semaphore, fence, imageIndex) : VK_ERROR_INITIALIZATION_FAILED;
}

extern "C" __declspec(dllexport) VkResult OptimumFsr3SwapchainPresent(
    OptimumFsr3Swapchain* value, VkQueue queue, const VkPresentInfoKHR* info) {
    return value && info ? value->functions.pOutQueuePresentKHR(queue, info) :
        VK_ERROR_INITIALIZATION_FAILED;
}

extern "C" __declspec(dllexport) void OptimumFsr3SwapchainDestroyChain(
    OptimumFsr3Swapchain* value, VkSwapchainKHR chain) {
    if (value && chain) {
        if (value->activeFg) {
            int disabled = OptimumFsr3FgDisable(value->activeFg, value);
            if (disabled != 0)
                std::fprintf(stderr, "[FidelityFX] disable before swapchain destroy returned %d\n", disabled);
        }
        ffxDispatchDescFrameGenerationSwapChainWaitForPresentsVK wait{};
        wait.header.type = FFX_API_DISPATCH_DESC_TYPE_FGSWAPCHAIN_WAIT_FOR_PRESENTS_VK;
        int code = dispatch ? static_cast<int>(dispatch(&value->context, &wait.header)) : -1;
        if (code != 0)
            std::fprintf(stderr, "[FidelityFX] wait for presents before swapchain destroy returned %d\n", code);
        value->functions.pOutDestroySwapchainFFXAPI(value->device, chain, nullptr, value->context);
    }
    if (value && value->chain == chain) value->chain = VK_NULL_HANDLE;
}

extern "C" __declspec(dllexport) uint64_t OptimumFsr3SwapchainLastPresentCount(
    OptimumFsr3Swapchain* value, VkSwapchainKHR chain) {
    return value && chain && value->functions.pOutGetLastPresentCountFFXAPI ?
        value->functions.pOutGetLastPresentCountFFXAPI(chain) : 0;
}

extern "C" __declspec(dllexport) int OptimumFsr3SwapchainDestroy(
    OptimumFsr3Swapchain* value) {
    if (!value) return 0;
    if (value->activeFg) OptimumFsr3FgDisable(value->activeFg, value);
    int code = value->context && destroyContext ?
        static_cast<int>(destroyContext(&value->context, nullptr)) : 0;
    delete value;
    return code;
}

extern "C" __declspec(dllexport) int OptimumFsr3FgCreate(
    VkDevice device, VkPhysicalDevice physical, uint32_t width, uint32_t height, VkFormat backBufferFormat,
    OptimumFsr3FgContext** result) {
    if (!createContext || !result || !device || !physical || !width || !height) return -1;
    auto* value = new (std::nothrow) OptimumFsr3FgContext();
    if (!value) return -2;
    value->width = width;
    value->height = height;
    value->frameGeneration.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION;
    value->frameGeneration.header.pNext = &value->hudlessFormat.header;
    value->frameGeneration.displaySize = {width, height};
    value->frameGeneration.maxRenderSize = {width, height};
    value->frameGeneration.backBufferFormat = backBufferFormat == VK_FORMAT_B8G8R8A8_UNORM ?
        FFX_API_SURFACE_FORMAT_B8G8R8A8_UNORM : FFX_API_SURFACE_FORMAT_R8G8B8A8_UNORM;
    // The proxy's real backbuffer may be BGRA, while the Vulkan renderer's
    // upright HUD-less image is RGBA. FFX keeps a previous HUD-less source in
    // this format; without the linked descriptor it assumes backBufferFormat.
    value->hudlessFormat.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION_HUDLESS;
    value->hudlessFormat.header.pNext = &value->backend.header;
    value->hudlessFormat.hudlessBackBufferFormat = FFX_API_SURFACE_FORMAT_R8G8B8A8_UNORM;
    value->backend.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_VK;
    value->backend.vkDevice = device;
    value->backend.vkPhysicalDevice = physical;
    HMODULE vulkan = GetModuleHandleW(L"vulkan-1.dll");
    vulkanGetDeviceProcAddr = vulkan ? reinterpret_cast<PFN_vkGetDeviceProcAddr>(
        GetProcAddress(vulkan, "vkGetDeviceProcAddr")) : nullptr;
    if (!vulkanGetDeviceProcAddr) { delete value; return -3; }
    value->backend.vkDeviceProcAddr = Fsr3GetDeviceProcAddr;
    int code = static_cast<int>(createContext(&value->effect, &value->frameGeneration.header, nullptr));
    if (code != 0) { delete value; return code; }
    *result = value;
    return 0;
}

static ffxReturnCode_t Fsr3GenerateOnPresent(ffxDispatchDescFrameGeneration* params, void* user) {
    auto* context = static_cast<OptimumFsr3FgContext*>(user);
    return context && params && dispatch ?
        dispatch(&context->effect, &params->header) : FFX_API_RETURN_ERROR_PARAMETER;
}

extern "C" __declspec(dllexport) int OptimumFsr3FgDisable(
    OptimumFsr3FgContext* context, OptimumFsr3Swapchain* swapchain) {
    if (!context || !context->effect || !configure || !context->chain) return 0;
    if (swapchain != context->swapchain) swapchain = context->swapchain;
    ffxConfigureDescFrameGeneration config{};
    config.header.type = FFX_API_CONFIGURE_DESC_TYPE_FRAMEGENERATION;
    config.swapChain = reinterpret_cast<void*>(context->chain);
    config.frameGenerationEnabled = false;
    int code = static_cast<int>(configure(&context->effect, &config.header));
    if (swapchain && swapchain->context && swapchain->chain == context->chain) {
        ffxDispatchDescFrameGenerationSwapChainWaitForPresentsVK wait{};
        wait.header.type = FFX_API_DISPATCH_DESC_TYPE_FGSWAPCHAIN_WAIT_FOR_PRESENTS_VK;
        int waited = dispatch ? static_cast<int>(dispatch(&swapchain->context, &wait.header)) : -1;
        if (code == 0) code = waited;
        ffxConfigureDescFrameGenerationSwapChainRegisterUiResourceVK ui{};
        ui.header.type = FFX_API_CONFIGURE_DESC_TYPE_FGSWAPCHAIN_REGISTERUIRESOURCE_VK;
        int cleared = static_cast<int>(configure(&swapchain->context, &ui.header));
        if (code == 0) code = cleared;
    }
    if (swapchain && swapchain->activeFg == context) swapchain->activeFg = nullptr;
    context->swapchain = nullptr;
    context->chain = VK_NULL_HANDLE;
    return code;
}

extern "C" __declspec(dllexport) int OptimumFsr3FgEvaluate(
    OptimumFsr3FgContext* context, const OptimumFsr3FgFrame* frame) {
    if (!context || !frame || !dispatch || !configure) return -1;
    auto* swapchain = static_cast<OptimumFsr3Swapchain*>(frame->swapchainContext);
    if (frame->color.format != VK_FORMAT_R8G8B8A8_UNORM ||
        frame->ui.format != VK_FORMAT_R8G8B8A8_UNORM ||
        frame->depth.format != VK_FORMAT_D32_SFLOAT ||
        frame->motion.format != VK_FORMAT_R16G16_SFLOAT ||
        !frame->commands || !swapchain || !swapchain->context || !swapchain->chain) return -2;

    if (context->swapchain && context->swapchain != swapchain) {
        int disabled = OptimumFsr3FgDisable(context, context->swapchain);
        if (disabled != 0) return disabled;
    }

    ffxConfigureDescFrameGeneration config{};
    config.header.type = FFX_API_CONFIGURE_DESC_TYPE_FRAMEGENERATION;
    config.swapChain = reinterpret_cast<void*>(swapchain->chain);
    config.frameGenerationCallback = Fsr3GenerateOnPresent;
    config.frameGenerationCallbackUserContext = context;
    config.frameGenerationEnabled = true;
    config.HUDLessColor = frame->hudless.image ? Resource(frame->hudless) : FfxApiResource{};
    config.generationRect = {0, 0, static_cast<int32_t>(context->width), static_cast<int32_t>(context->height)};
    config.frameID = frame->frameId;
    int code = static_cast<int>(configure(&context->effect, &config.header));
    if (code != 0) return code;
    context->chain = swapchain->chain;
    context->swapchain = swapchain;
    swapchain->activeFg = context;

    ffxDispatchDescFrameGenerationPrepare prepare{};
    prepare.header.type = FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION_PREPARE;
    ffxDispatchDescFrameGenerationPrepareCameraInfo camera{};
    camera.header.type = FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION_PREPARE_CAMERAINFO;
    camera.cameraPosition[0] = frame->cameraPos[0];
    camera.cameraPosition[1] = frame->cameraPos[1];
    camera.cameraPosition[2] = frame->cameraPos[2];
    camera.cameraUp[0] = frame->cameraUp[0];
    camera.cameraUp[1] = frame->cameraUp[1];
    camera.cameraUp[2] = frame->cameraUp[2];
    camera.cameraRight[0] = frame->cameraRight[0];
    camera.cameraRight[1] = frame->cameraRight[1];
    camera.cameraRight[2] = frame->cameraRight[2];
    camera.cameraForward[0] = frame->cameraForward[0];
    camera.cameraForward[1] = frame->cameraForward[1];
    camera.cameraForward[2] = frame->cameraForward[2];
    prepare.header.pNext = &camera.header;
    prepare.frameID = frame->frameId;
    prepare.commandList = frame->commands;
    prepare.renderSize = {frame->depth.width, frame->depth.height};
    prepare.jitterOffset = {frame->jitterX, frame->jitterY};
    // The Vulkan bridge flips the GL-oriented motion image before this call.
    // Negate its vertical component to keep vectors in upright present space.
    prepare.motionVectorScale = {1.0f, -1.0f};
    prepare.frameTimeDelta = frame->deltaMs > 0 ? frame->deltaMs : 16.667f;
    prepare.cameraNear = frame->nearPlane;
    prepare.cameraFar = frame->farPlane;
    prepare.cameraFovAngleVertical = frame->fovRadians;
    prepare.viewSpaceToMetersFactor = 1.0f;
    prepare.depth = Resource(frame->depth);
    prepare.motionVectors = Resource(frame->motion);
    code = static_cast<int>(dispatch(&context->effect, &prepare.header));
    if (code != 0) return code;

    ffxConfigureDescFrameGenerationSwapChainRegisterUiResourceVK ui{};
    ui.header.type = FFX_API_CONFIGURE_DESC_TYPE_FGSWAPCHAIN_REGISTERUIRESOURCE_VK;
    ui.uiResource = Resource(frame->ui);
    ui.flags = FFX_FRAMEGENERATION_UI_COMPOSITION_FLAG_USE_PREMUL_ALPHA |
        FFX_FRAMEGENERATION_UI_COMPOSITION_FLAG_ENABLE_INTERNAL_UI_DOUBLE_BUFFERING;
    return static_cast<int>(configure(&swapchain->context, &ui.header));
}

extern "C" __declspec(dllexport) int OptimumFsr3FgDestroy(OptimumFsr3FgContext* context) {
    if (!context) return 0;
    int disabled = OptimumFsr3FgDisable(context, context->swapchain);
    int code = context->effect && destroyContext
        ? static_cast<int>(destroyContext(&context->effect, nullptr)) : 0;
    delete context;
    return disabled != 0 ? disabled : code;
}
