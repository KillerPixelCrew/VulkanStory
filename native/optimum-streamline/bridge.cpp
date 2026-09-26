// Streamline's Vulkan entry points must come from sl.interposer.dll.  Keep its
// C++ ABI and frame tokens on this side of the managed/native boundary.
#define WIN32_LEAN_AND_MEAN
#define VK_USE_PLATFORM_WIN32_KHR
#include <windows.h>
#include <wintrust.h>
#include <softpub.h>
#include <wincrypt.h>
#include <string>
#include <atomic>
#include <cstdio>
#include <vulkan/vulkan.h>
#include <sl.h>
#include <sl_dlss_g.h>
#include <sl_reflex.h>
#include <sl_pcl.h>

namespace {
HMODULE module{};
PFun_slInit* init{};
PFun_slShutdown* shutdown{};
PFun_slGetFeatureFunction* featureFunction{};
PFun_slGetNewFrameToken* newFrameToken{};
PFun_slIsFeatureSupported* isFeatureSupported{};
PFun_slSetTagForFrame* setTagForFrame{};
PFun_slSetConstants* setConstants{};
PFN_vkGetInstanceProcAddr instanceProc{};
PFN_vkGetDeviceProcAddr deviceProc{};
PFun_slReflexSetOptions* reflexOptions{};
PFun_slReflexSleep* reflexSleep{};
PFun_slPCLSetMarker* pclMarker{};
PFun_slDLSSGSetOptions* fgOptions{};
PFun_slDLSSGGetState* fgState{};
sl::FrameToken* frameToken{};
std::atomic<int> lastPresentError{0};
bool verboseLog{};
const sl::ViewportHandle viewport(0);
std::wstring pluginDirectory;
std::string projectIdentity;
const wchar_t* pluginPath{};

template<class T> T proc(const char* name) { return reinterpret_cast<T>(GetProcAddress(module, name)); }

bool loadFeature(sl::Feature feature, const char* name, void*& target) {
    return featureFunction && featureFunction(feature, name, target) == sl::Result::eOk && target;
}

bool verifyRelease(wchar_t* path) {
    WINTRUST_FILE_INFO file{};
    file.cbStruct = sizeof(file);
    file.pcwszFilePath = path;
    WINTRUST_DATA trust{};
    trust.cbStruct = sizeof(trust);
    trust.dwUIChoice = WTD_UI_NONE;
    trust.fdwRevocationChecks = WTD_REVOKE_NONE;
    trust.dwUnionChoice = WTD_CHOICE_FILE;
    trust.pFile = &file;
    trust.dwStateAction = WTD_STATEACTION_VERIFY;
    trust.dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL;
    GUID action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    const LONG signature = WinVerifyTrust(nullptr, &action, &trust);
    trust.dwStateAction = WTD_STATEACTION_CLOSE;
    WinVerifyTrust(nullptr, &action, &trust);
    if (signature != ERROR_SUCCESS) return false;

    // The 2.14.1 release shipped with this repository is pinned byte-for-byte.
    // This is stricter than accepting any Authenticode publisher and avoids
    // loading a different signed interposer into the process.
    constexpr uint8_t expected[32] = {
        0x8c, 0x87, 0xc9, 0x49, 0x94, 0x61, 0xda, 0x56,
        0x1e, 0xdd, 0x52, 0x9a, 0xa9, 0xbf, 0x78, 0x31,
        0xd6, 0x7d, 0x7b, 0x94, 0xeb, 0xb1, 0xc1, 0xa5,
        0xed, 0x54, 0xef, 0x49, 0x34, 0xe1, 0xea, 0x4c,
    };
    HANDLE input = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (input == INVALID_HANDLE_VALUE) return false;
    HCRYPTPROV provider{};
    HCRYPTHASH hash{};
    uint8_t digest[32]{};
    DWORD size = sizeof(digest);
    bool valid = CryptAcquireContextW(&provider, nullptr, nullptr, PROV_RSA_AES, CRYPT_VERIFYCONTEXT) &&
        CryptCreateHash(provider, CALG_SHA_256, 0, 0, &hash);
    uint8_t bytes[64 * 1024];
    DWORD read{};
    while (valid) {
        if (!ReadFile(input, bytes, sizeof(bytes), &read, nullptr)) { valid = false; break; }
        if (read == 0) break;
        valid = CryptHashData(hash, bytes, read, 0) != FALSE;
    }
    if (valid) valid = CryptGetHashParam(hash, HP_HASHVAL, digest, &size, 0) &&
        size == sizeof(expected) && memcmp(digest, expected, size) == 0;
    if (hash) CryptDestroyHash(hash);
    if (provider) CryptReleaseContext(provider, 0);
    CloseHandle(input);
    return valid;
}

struct TaggedImage {
    uint64_t image;
    uint64_t view;
    uint32_t layout;
    uint32_t format;
    uint32_t width;
    uint32_t height;
    uint32_t usage;
};

struct FrameCamera {
    const float* viewToClip;
    const float* clipToView;
    const float* clipToPrevClip;
    const float* prevClipToClip;
    float nearPlane, farPlane, fov, aspect;
    float jitterX, jitterY, mvecScaleX, mvecScaleY;
    float position[3], up[3], right[3], forward[3];
    uint32_t reset;
};

sl::Resource resource(const TaggedImage& image) {
    sl::Resource value{sl::ResourceType::eTex2d,
        reinterpret_cast<void*>(static_cast<uintptr_t>(image.image)), nullptr,
        reinterpret_cast<void*>(static_cast<uintptr_t>(image.view)), image.layout};
    value.width = image.width; value.height = image.height;
    value.nativeFormat = image.format; value.mipLevels = 1; value.arrayLayers = 1;
    value.flags = 0;
    value.usage = image.usage;
    return value;
}

void rowMajor(sl::float4x4& target, const float* columnMajor) {
    for (int row = 0; row < 4; ++row)
        for (int col = 0; col < 4; ++col)
            (&target[row].x)[col] = columnMajor[col * 4 + row];
}

// Called by Streamline's present thread. Keep it lock-free and let the render
// thread report or recover from the error on its next state query.
void onPresentError(const sl::APIError& error) {
    lastPresentError.store(static_cast<int>(error.vkRes), std::memory_order_relaxed);
}

void onStreamlineMessage(sl::LogType type, const char* message) {
    if ((type == sl::LogType::eInfo && !verboseLog) || !message) return;
    std::fprintf(stderr, "[Streamline %s] %s\n",
        type == sl::LogType::eError ? "error" : type == sl::LogType::eWarn ? "warning" : "info", message);
}
}

extern "C" {
__declspec(dllexport) int OptimumSlInitialize(const wchar_t* directory, const char* projectId) {
    if (module) return 0;
    if (!directory || !projectId) return -1;
    wchar_t path[MAX_PATH]{};
    if (swprintf(path, MAX_PATH, L"%ls\\sl.interposer.dll", directory) < 0) return -2;
    if (!verifyRelease(path)) return -5;
    module = LoadLibraryExW(path, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!module) return -3;
    init = proc<PFun_slInit*>("slInit");
    shutdown = proc<PFun_slShutdown*>("slShutdown");
    featureFunction = proc<PFun_slGetFeatureFunction*>("slGetFeatureFunction");
    newFrameToken = proc<PFun_slGetNewFrameToken*>("slGetNewFrameToken");
    isFeatureSupported = proc<PFun_slIsFeatureSupported*>("slIsFeatureSupported");
    setTagForFrame = proc<PFun_slSetTagForFrame*>("slSetTagForFrame");
    setConstants = proc<PFun_slSetConstants*>("slSetConstants");
    instanceProc = proc<PFN_vkGetInstanceProcAddr>("vkGetInstanceProcAddr");
    deviceProc = proc<PFN_vkGetDeviceProcAddr>("vkGetDeviceProcAddr");
    if (!init || !shutdown || !featureFunction || !newFrameToken || !isFeatureSupported || !setTagForFrame ||
        !setConstants || !instanceProc || !deviceProc) {
        FreeLibrary(module); module = nullptr; return -4;
    }
    pluginDirectory = directory;
    projectIdentity = projectId;
    pluginPath = pluginDirectory.c_str();
    static const sl::Feature features[] = { sl::kFeatureDLSS_G, sl::kFeatureReflex, sl::kFeaturePCL };
    sl::Preferences preferences{};
    preferences.featuresToLoad = features;
    preferences.numFeaturesToLoad = 3;
    preferences.projectId = projectIdentity.c_str();
    preferences.engine = sl::EngineType::eCustom;
    preferences.engineVersion = "0.3";
    preferences.renderAPI = sl::RenderAPI::eVulkan;
    verboseLog = GetEnvironmentVariableA("OPTIMUM_STREAMLINE_VERBOSE", nullptr, 0) > 0;
    if (verboseLog) preferences.logLevel = sl::LogLevel::eVerbose;
    preferences.logMessageCallback = onStreamlineMessage;
    preferences.flags = sl::PreferenceFlags::eUseManualHooking |
        sl::PreferenceFlags::eUseFrameBasedResourceTagging;
    preferences.pathsToPlugins = &pluginPath;
    preferences.numPathsToPlugins = 1;
    const auto result = init(preferences, sl::kSDKVersion);
    if (result != sl::Result::eOk) { FreeLibrary(module); module = nullptr; return static_cast<int>(result); }
    return 0;
}

__declspec(dllexport) void OptimumSlShutdown() {
    if (!module) return;
    if (shutdown) shutdown();
    FreeLibrary(module);
    module = nullptr;
    reflexOptions = nullptr; reflexSleep = nullptr; pclMarker = nullptr;
    fgOptions = nullptr; fgState = nullptr; frameToken = nullptr;
    lastPresentError.store(0, std::memory_order_relaxed);
}

__declspec(dllexport) VkResult OptimumSlCreateInstance(const VkInstanceCreateInfo* info, VkInstance* instance) {
    auto call = module ? reinterpret_cast<PFN_vkCreateInstance>(instanceProc(nullptr, "vkCreateInstance")) : nullptr;
    return call ? call(info, nullptr, instance) : VK_ERROR_INITIALIZATION_FAILED;
}
__declspec(dllexport) VkResult OptimumSlEnumeratePhysicalDevices(VkInstance instance,
    uint32_t* count, VkPhysicalDevice* devices) {
    auto call = module ? reinterpret_cast<PFN_vkEnumeratePhysicalDevices>(
        instanceProc(instance, "vkEnumeratePhysicalDevices")) : nullptr;
    return call ? call(instance, count, devices) : VK_ERROR_INITIALIZATION_FAILED;
}
__declspec(dllexport) VkResult OptimumSlCreateDevice(VkInstance instance, VkPhysicalDevice physical,
    const VkDeviceCreateInfo* info, VkDevice* device) {
    auto call = module ? reinterpret_cast<PFN_vkCreateDevice>(instanceProc(instance, "vkCreateDevice")) : nullptr;
    return call ? call(physical, info, nullptr, device) : VK_ERROR_INITIALIZATION_FAILED;
}
__declspec(dllexport) VkResult OptimumSlCreateWin32Surface(VkInstance instance,
    const VkWin32SurfaceCreateInfoKHR* info, VkSurfaceKHR* surface) {
    auto call = module ? reinterpret_cast<PFN_vkCreateWin32SurfaceKHR>(instanceProc(instance, "vkCreateWin32SurfaceKHR")) : nullptr;
    return call ? call(instance, info, nullptr, surface) : VK_ERROR_INITIALIZATION_FAILED;
}
__declspec(dllexport) void OptimumSlDestroySurface(VkInstance instance, VkSurfaceKHR surface) {
    auto call = module ? reinterpret_cast<PFN_vkDestroySurfaceKHR>(instanceProc(instance, "vkDestroySurfaceKHR")) : nullptr;
    if (call) call(instance, surface, nullptr);
}
__declspec(dllexport) VkResult OptimumSlCreateSwapchain(VkDevice device,
    const VkSwapchainCreateInfoKHR* info, VkSwapchainKHR* swapchain) {
    auto call = module ? reinterpret_cast<PFN_vkCreateSwapchainKHR>(deviceProc(device, "vkCreateSwapchainKHR")) : nullptr;
    return call ? call(device, info, nullptr, swapchain) : VK_ERROR_INITIALIZATION_FAILED;
}
__declspec(dllexport) void OptimumSlDestroySwapchain(VkDevice device, VkSwapchainKHR swapchain) {
    auto call = module ? reinterpret_cast<PFN_vkDestroySwapchainKHR>(deviceProc(device, "vkDestroySwapchainKHR")) : nullptr;
    if (call) call(device, swapchain, nullptr);
}
__declspec(dllexport) VkResult OptimumSlGetSwapchainImages(VkDevice device, VkSwapchainKHR swapchain,
    uint32_t* count, VkImage* images) {
    auto call = module ? reinterpret_cast<PFN_vkGetSwapchainImagesKHR>(deviceProc(device, "vkGetSwapchainImagesKHR")) : nullptr;
    return call ? call(device, swapchain, count, images) : VK_ERROR_INITIALIZATION_FAILED;
}
__declspec(dllexport) VkResult OptimumSlAcquire(VkDevice device, VkSwapchainKHR swapchain,
    uint64_t timeout, VkSemaphore semaphore, VkFence fence, uint32_t* index) {
    auto call = module ? reinterpret_cast<PFN_vkAcquireNextImageKHR>(deviceProc(device, "vkAcquireNextImageKHR")) : nullptr;
    return call ? call(device, swapchain, timeout, semaphore, fence, index) : VK_ERROR_INITIALIZATION_FAILED;
}
__declspec(dllexport) VkResult OptimumSlPresent(VkDevice device, VkQueue queue, const VkPresentInfoKHR* info) {
    auto call = module ? reinterpret_cast<PFN_vkQueuePresentKHR>(deviceProc(device, "vkQueuePresentKHR")) : nullptr;
    return call ? call(queue, info) : VK_ERROR_INITIALIZATION_FAILED;
}
__declspec(dllexport) VkResult OptimumSlDeviceWaitIdle(VkDevice device) {
    auto call = module ? reinterpret_cast<PFN_vkDeviceWaitIdle>(deviceProc(device, "vkDeviceWaitIdle")) : nullptr;
    return call ? call(device) : VK_ERROR_INITIALIZATION_FAILED;
}

// Resolve feature functions after the Vulkan device exists and the SL proxy has seen it.
__declspec(dllexport) int OptimumSlIsFrameGenerationSupported(VkPhysicalDevice physical) {
    if (!isFeatureSupported || !physical) return -1;
    sl::AdapterInfo adapter{};
    adapter.vkPhysicalDevice = physical;
    return static_cast<int>(isFeatureSupported(sl::kFeatureDLSS_G, adapter));
}
__declspec(dllexport) int OptimumSlBindFeatures() {
    void* function{};
    if (!loadFeature(sl::kFeatureReflex, "slReflexSetOptions", function)) return -1;
    reflexOptions = reinterpret_cast<PFun_slReflexSetOptions*>(function);
    if (!loadFeature(sl::kFeatureReflex, "slReflexSleep", function)) return -2;
    reflexSleep = reinterpret_cast<PFun_slReflexSleep*>(function);
    if (!loadFeature(sl::kFeaturePCL, "slPCLSetMarker", function)) return -3;
    pclMarker = reinterpret_cast<PFun_slPCLSetMarker*>(function);
    if (!loadFeature(sl::kFeatureDLSS_G, "slDLSSGSetOptions", function)) return -4;
    fgOptions = reinterpret_cast<PFun_slDLSSGSetOptions*>(function);
    if (!loadFeature(sl::kFeatureDLSS_G, "slDLSSGGetState", function)) return -5;
    fgState = reinterpret_cast<PFun_slDLSSGGetState*>(function);
    return 0;
}
__declspec(dllexport) int OptimumSlSetReflex(int mode, uint32_t maxFps) {
    if (!reflexOptions) return -1;
    sl::ReflexOptions options{};
    options.mode = mode == 2 ? sl::ReflexMode::eLowLatencyWithBoost :
        mode == 1 ? sl::ReflexMode::eLowLatency : sl::ReflexMode::eOff;
    options.frameLimitUs = maxFps ? 1000000u / maxFps : 0;
    return static_cast<int>(reflexOptions(options));
}
__declspec(dllexport) int OptimumSlBeginFrame(uint32_t frameIndex) {
    if (!newFrameToken) return -1;
    return static_cast<int>(newFrameToken(frameToken, &frameIndex));
}
__declspec(dllexport) int OptimumSlReflexSleep() {
    return reflexSleep && frameToken ? static_cast<int>(reflexSleep(*frameToken)) : -1;
}
__declspec(dllexport) int OptimumSlMarker(uint32_t marker) {
    return pclMarker && frameToken ? static_cast<int>(pclMarker(static_cast<sl::PCLMarker>(marker), *frameToken)) : -1;
}
__declspec(dllexport) int OptimumSlTagFrame(VkCommandBuffer commandBuffer,
    const TaggedImage* depth, const TaggedImage* motion, const TaggedImage* hudless,
    const TaggedImage* ui, const FrameCamera* camera, uint32_t backbufferWidth,
    uint32_t backbufferHeight) {
    if (!setTagForFrame || !setConstants || !frameToken || !depth || !motion ||
        !hudless || !ui || !camera || !camera->viewToClip || !camera->clipToView ||
        !camera->clipToPrevClip || !camera->prevClipToClip) return -1;

    sl::Constants constants{};
    rowMajor(constants.cameraViewToClip, camera->viewToClip);
    rowMajor(constants.clipToCameraView, camera->clipToView);
    rowMajor(constants.clipToPrevClip, camera->clipToPrevClip);
    rowMajor(constants.prevClipToClip, camera->prevClipToClip);
    constants.jitterOffset = {camera->jitterX, camera->jitterY};
    constants.mvecScale = {camera->mvecScaleX, camera->mvecScaleY};
    constants.cameraPos = {camera->position[0], camera->position[1], camera->position[2]};
    constants.cameraUp = {camera->up[0], camera->up[1], camera->up[2]};
    constants.cameraRight = {camera->right[0], camera->right[1], camera->right[2]};
    constants.cameraFwd = {camera->forward[0], camera->forward[1], camera->forward[2]};
    constants.cameraNear = camera->nearPlane;
    constants.cameraFar = camera->farPlane;
    constants.cameraFOV = camera->fov;
    constants.cameraAspectRatio = camera->aspect;
    constants.depthInverted = sl::Boolean::eFalse;
    constants.cameraMotionIncluded = sl::Boolean::eTrue;
    constants.motionVectors3D = sl::Boolean::eFalse;
    constants.reset = camera->reset ? sl::Boolean::eTrue : sl::Boolean::eFalse;
    auto result = setConstants(constants, *frameToken, viewport);
    if (result != sl::Result::eOk) return static_cast<int>(result);

    sl::Resource d = resource(*depth), m = resource(*motion), h = resource(*hudless), u = resource(*ui);
    const sl::Extent depthExtent{0, 0, depth->width, depth->height};
    const sl::Extent motionExtent{0, 0, motion->width, motion->height};
    const sl::Extent colorExtent{0, 0, hudless->width, hudless->height};
    const sl::Extent uiExtent{0, 0, ui->width, ui->height};
    (void)backbufferWidth;
    (void)backbufferHeight;
      // The renderer reuses its four upright staging images on the next frame,
      // which can begin before this frame reaches the asynchronous Present.
      // Copy them on the tagging command buffer so each Present sees its own
      // depth, motion, scene and UI contents.
      sl::ResourceTag tags[] = {
          {&d, sl::kBufferTypeDepth, sl::ResourceLifecycle::eOnlyValidNow, &depthExtent},
          {&m, sl::kBufferTypeMotionVectors, sl::ResourceLifecycle::eOnlyValidNow, &motionExtent},
          {&h, sl::kBufferTypeHUDLessColor, sl::ResourceLifecycle::eOnlyValidNow, &colorExtent},
          {&u, sl::kBufferTypeUIColorAndAlpha, sl::ResourceLifecycle::eOnlyValidNow, &uiExtent},
      };
    return static_cast<int>(setTagForFrame(*frameToken, viewport, tags, 4,
        reinterpret_cast<sl::CommandBuffer*>(commandBuffer)));
}
__declspec(dllexport) int OptimumSlInvalidateFrameTags() {
    if (!setTagForFrame || !frameToken) return -1;
    sl::ResourceTag tags[] = {
        {nullptr, sl::kBufferTypeDepth, sl::ResourceLifecycle::eValidUntilPresent, nullptr},
        {nullptr, sl::kBufferTypeMotionVectors, sl::ResourceLifecycle::eValidUntilPresent, nullptr},
        {nullptr, sl::kBufferTypeHUDLessColor, sl::ResourceLifecycle::eValidUntilPresent, nullptr},
        {nullptr, sl::kBufferTypeUIColorAndAlpha, sl::ResourceLifecycle::eValidUntilPresent, nullptr},
    };
    return static_cast<int>(setTagForFrame(*frameToken, viewport, tags, 4, nullptr));
}
__declspec(dllexport) int OptimumSlSetFrameGeneration(int enabled, uint32_t width, uint32_t height,
    uint32_t colorFormat, uint32_t backBuffers) {
    if (!fgOptions) return -1;
    sl::DLSSGOptions options{};
    options.mode = enabled ? sl::DLSSGMode::eOn : sl::DLSSGMode::eOff;
    options.numFramesToGenerate = 1;
    options.colorWidth = width; options.colorHeight = height;
    options.colorBufferFormat = colorFormat; options.numBackBuffers = backBuffers;
    options.enableUserInterfaceRecomposition = sl::Boolean::eTrue;
    options.onErrorCallback = onPresentError;
    return static_cast<int>(fgOptions(viewport, options));
}
__declspec(dllexport) int OptimumSlTakePresentError() {
    return lastPresentError.exchange(0, std::memory_order_relaxed);
}
__declspec(dllexport) int OptimumSlGetFrameGenerationState(uint32_t* status, uint32_t* presented) {
    if (!fgState || !status || !presented) return -1;
    sl::DLSSGState state{};
    auto result = fgState(viewport, state, nullptr);
    if (result == sl::Result::eOk) {
        *status = static_cast<uint32_t>(state.status);
        *presented = state.numFramesActuallyPresented;
    }
    return static_cast<int>(result);
}
}
