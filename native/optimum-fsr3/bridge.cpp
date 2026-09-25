// Small ABI boundary for the signed AMD FidelityFX SDK 1.1.4 Vulkan runtime.
// The SDK headers and binary are supplied from _ref/fsr-vulkan at build time.
#include <windows.h>
#include <cstdint>
#include <cstddef>
#include <cmath>
#include <new>
#include <cstring>
#include "ffx_api/ffx_api.h"
#include "ffx_api/ffx_upscale.h"
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

static HMODULE sdk;
static PfnFfxCreateContext createContext;
static PfnFfxDestroyContext destroyContext;
static PfnFfxQuery query;
static PfnFfxDispatch dispatch;
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
    if (!create || !destroy || !queryFunction || !dispatchFunction) return -2;
    sdk = runtime;
    createContext = create;
    destroyContext = destroy;
    query = queryFunction;
    dispatch = dispatchFunction;
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
