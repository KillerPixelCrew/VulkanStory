// Streamline's Vulkan entry points must come from sl.interposer.dll.  Keep its
// C++ ABI and frame tokens on this side of the managed/native boundary.
#define WIN32_LEAN_AND_MEAN
#define VK_USE_PLATFORM_WIN32_KHR
#include <windows.h>
#include <wintrust.h>
#include <softpub.h>
#include <wincrypt.h>
#include <string>
#include <cstdint>
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
PFun_slFreeResources* freeResources{};
PFN_vkGetInstanceProcAddr instanceProc{};
PFN_vkGetDeviceProcAddr deviceProc{};
PFun_slReflexSetOptions* reflexOptions{};
PFun_slReflexSleep* reflexSleep{};
PFun_slReflexGetState* reflexState{};
PFun_slPCLSetMarker* pclMarker{};
PFun_slPCLGetState* pclState{};
PFun_slPCLSetOptions* pclOptions{};
PFun_slDLSSGSetOptions* fgOptions{};
PFun_slDLSSGGetState* fgState{};
bool fgConfigured{};
bool fgResourcesMayExist{};
bool fgEnabled{};
VkSwapchainKHR currentSwapchain{};
uint32_t fgCount{}, fgWidth{}, fgHeight{}, fgFormat{}, fgBackBuffers{};
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
__declspec(dllexport) int VulkanStorySlInitialize(const wchar_t* directory, const char* projectId,
    uint32_t loadReflex, uint32_t loadDlssG) {
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
    freeResources = proc<PFun_slFreeResources*>("slFreeResources");
    instanceProc = proc<PFN_vkGetInstanceProcAddr>("vkGetInstanceProcAddr");
    deviceProc = proc<PFN_vkGetDeviceProcAddr>("vkGetDeviceProcAddr");
    if (!init || !shutdown || !featureFunction || !newFrameToken || !isFeatureSupported || !setTagForFrame ||
        !setConstants || !freeResources || !instanceProc || !deviceProc) {
        FreeLibrary(module); module = nullptr; return -4;
    }
    pluginDirectory = directory;
    projectIdentity = projectId;
    pluginPath = pluginDirectory.c_str();
    static const sl::Feature pclOnly[] = { sl::kFeaturePCL };
    static const sl::Feature latencyFeatures[] = { sl::kFeatureReflex, sl::kFeaturePCL };
    static const sl::Feature frameGenerationFeatures[] = {
        sl::kFeatureDLSS_G, sl::kFeatureReflex, sl::kFeaturePCL };
    sl::Preferences preferences{};
    preferences.featuresToLoad = loadDlssG ? frameGenerationFeatures :
        loadReflex ? latencyFeatures : pclOnly;
    preferences.numFeaturesToLoad = loadDlssG ? 3 : loadReflex ? 2 : 1;
    preferences.projectId = projectIdentity.c_str();
    preferences.engine = sl::EngineType::eCustom;
    preferences.engineVersion = "0.3";
    preferences.renderAPI = sl::RenderAPI::eVulkan;
    verboseLog = GetEnvironmentVariableA("VULKANSTORY_STREAMLINE_VERBOSE", nullptr, 0) > 0;
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

__declspec(dllexport) void VulkanStorySlShutdown() {
    if (!module) return;
    if (shutdown) shutdown();
    FreeLibrary(module);
    module = nullptr;
    reflexOptions = nullptr; reflexSleep = nullptr; reflexState = nullptr; pclMarker = nullptr;
    pclState = nullptr; pclOptions = nullptr;
    fgOptions = nullptr; fgState = nullptr; frameToken = nullptr;
    freeResources = nullptr;
    fgConfigured = false;
    fgResourcesMayExist = false;
    fgEnabled = false;
    currentSwapchain = VK_NULL_HANDLE;
    lastPresentError.store(0, std::memory_order_relaxed);
}

__declspec(dllexport) VkResult VulkanStorySlCreateInstance(const VkInstanceCreateInfo* info, VkInstance* instance) {
    auto call = module ? reinterpret_cast<PFN_vkCreateInstance>(instanceProc(nullptr, "vkCreateInstance")) : nullptr;
    return call ? call(info, nullptr, instance) : VK_ERROR_INITIALIZATION_FAILED;
}
__declspec(dllexport) VkResult VulkanStorySlEnumeratePhysicalDevices(VkInstance instance,
    uint32_t* count, VkPhysicalDevice* devices) {
    auto call = module ? reinterpret_cast<PFN_vkEnumeratePhysicalDevices>(
        instanceProc(instance, "vkEnumeratePhysicalDevices")) : nullptr;
    return call ? call(instance, count, devices) : VK_ERROR_INITIALIZATION_FAILED;
}
__declspec(dllexport) VkResult VulkanStorySlCreateDevice(VkInstance instance, VkPhysicalDevice physical,
    const VkDeviceCreateInfo* info, VkDevice* device) {
    auto call = module ? reinterpret_cast<PFN_vkCreateDevice>(instanceProc(instance, "vkCreateDevice")) : nullptr;
    return call ? call(physical, info, nullptr, device) : VK_ERROR_INITIALIZATION_FAILED;
}
__declspec(dllexport) VkResult VulkanStorySlCreateWin32Surface(VkInstance instance,
    const VkWin32SurfaceCreateInfoKHR* info, VkSurfaceKHR* surface) {
    auto call = module ? reinterpret_cast<PFN_vkCreateWin32SurfaceKHR>(instanceProc(instance, "vkCreateWin32SurfaceKHR")) : nullptr;
    return call ? call(instance, info, nullptr, surface) : VK_ERROR_INITIALIZATION_FAILED;
}
__declspec(dllexport) void VulkanStorySlDestroySurface(VkInstance instance, VkSurfaceKHR surface) {
    auto call = module ? reinterpret_cast<PFN_vkDestroySurfaceKHR>(instanceProc(instance, "vkDestroySurfaceKHR")) : nullptr;
    if (call) call(instance, surface, nullptr);
}
__declspec(dllexport) VkResult VulkanStorySlCreateSwapchain(VkDevice device,
    const VkSwapchainCreateInfoKHR* info, VkSwapchainKHR* swapchain) {
    auto call = module ? reinterpret_cast<PFN_vkCreateSwapchainKHR>(deviceProc(device, "vkCreateSwapchainKHR")) : nullptr;
    VkResult result = call ? call(device, info, nullptr, swapchain) : VK_ERROR_INITIALIZATION_FAILED;
    if (result == VK_SUCCESS && swapchain) {
        currentSwapchain = *swapchain;
        fgConfigured = false;
    }
    return result;
}
__declspec(dllexport) void VulkanStorySlDestroySwapchain(VkDevice device, VkSwapchainKHR swapchain) {
    auto call = module ? reinterpret_cast<PFN_vkDestroySwapchainKHR>(deviceProc(device, "vkDestroySwapchainKHR")) : nullptr;
    if (!call) return;
    call(device, swapchain, nullptr);
    // Deferred retirement of an old chain must not invalidate the live
    // successor's options and cause a redundant SetOptions on its next frame.
    if (swapchain == currentSwapchain) {
        currentSwapchain = VK_NULL_HANDLE;
        fgConfigured = false;
    }
}
__declspec(dllexport) VkResult VulkanStorySlGetSwapchainImages(VkDevice device, VkSwapchainKHR swapchain,
    uint32_t* count, VkImage* images) {
    auto call = module ? reinterpret_cast<PFN_vkGetSwapchainImagesKHR>(deviceProc(device, "vkGetSwapchainImagesKHR")) : nullptr;
    return call ? call(device, swapchain, count, images) : VK_ERROR_INITIALIZATION_FAILED;
}
__declspec(dllexport) VkResult VulkanStorySlAcquire(VkDevice device, VkSwapchainKHR swapchain,
    uint64_t timeout, VkSemaphore semaphore, VkFence fence, uint32_t* index) {
    auto call = module ? reinterpret_cast<PFN_vkAcquireNextImageKHR>(deviceProc(device, "vkAcquireNextImageKHR")) : nullptr;
    return call ? call(device, swapchain, timeout, semaphore, fence, index) : VK_ERROR_INITIALIZATION_FAILED;
}
__declspec(dllexport) VkResult VulkanStorySlPresent(VkDevice device, VkQueue queue, const VkPresentInfoKHR* info) {
    auto call = module ? reinterpret_cast<PFN_vkQueuePresentKHR>(deviceProc(device, "vkQueuePresentKHR")) : nullptr;
    return call ? call(queue, info) : VK_ERROR_INITIALIZATION_FAILED;
}
__declspec(dllexport) VkResult VulkanStorySlDeviceWaitIdle(VkDevice device) {
    auto call = module ? reinterpret_cast<PFN_vkDeviceWaitIdle>(deviceProc(device, "vkDeviceWaitIdle")) : nullptr;
    return call ? call(device) : VK_ERROR_INITIALIZATION_FAILED;
}

// Resolve feature functions after the Vulkan device exists and the SL proxy has seen it.
__declspec(dllexport) int VulkanStorySlIsFrameGenerationSupported(VkPhysicalDevice physical) {
    if (!isFeatureSupported || !physical) return -1;
    sl::AdapterInfo adapter{};
    adapter.vkPhysicalDevice = physical;
    return static_cast<int>(isFeatureSupported(sl::kFeatureDLSS_G, adapter));
}
__declspec(dllexport) int VulkanStorySlBindReflex() {
    void* function{};
    if (!loadFeature(sl::kFeatureReflex, "slReflexSetOptions", function)) return -1;
    reflexOptions = reinterpret_cast<PFun_slReflexSetOptions*>(function);
    if (!loadFeature(sl::kFeatureReflex, "slReflexSleep", function)) return -2;
    reflexSleep = reinterpret_cast<PFun_slReflexSleep*>(function);
    if (!loadFeature(sl::kFeatureReflex, "slReflexGetState", function)) return -3;
    reflexState = reinterpret_cast<PFun_slReflexGetState*>(function);
    return 0;
}
__declspec(dllexport) int VulkanStorySlBindFrameGeneration() {
    void* function{};
    if (!loadFeature(sl::kFeatureDLSS_G, "slDLSSGSetOptions", function)) return -4;
    fgOptions = reinterpret_cast<PFun_slDLSSGSetOptions*>(function);
    if (!loadFeature(sl::kFeatureDLSS_G, "slDLSSGGetState", function)) return -5;
    fgState = reinterpret_cast<PFun_slDLSSGGetState*>(function);
    return 0;
}
__declspec(dllexport) int VulkanStorySlGetReflexState(uint32_t* available, uint32_t* reports) {
    if (!reflexState || !available || !reports) return -1;
    sl::ReflexState state{};
    auto result = reflexState(state);
    if (result != sl::Result::eOk) return static_cast<int>(result);
    *available = state.lowLatencyAvailable ? 1u : 0u;
    *reports = state.latencyReportAvailable ? 1u : 0u;
    return 0;
}
__declspec(dllexport) int VulkanStorySlBindPcl() {
    void* function{};
    if (!loadFeature(sl::kFeaturePCL, "slPCLSetMarker", function)) return -1;
    pclMarker = reinterpret_cast<PFun_slPCLSetMarker*>(function);
    if (!loadFeature(sl::kFeaturePCL, "slPCLGetState", function)) return -2;
    pclState = reinterpret_cast<PFun_slPCLGetState*>(function);
    if (!loadFeature(sl::kFeaturePCL, "slPCLSetOptions", function)) return -3;
    pclOptions = reinterpret_cast<PFun_slPCLSetOptions*>(function);
    sl::PCLOptions options{};
    options.idThread = GetCurrentThreadId();
    return static_cast<int>(pclOptions(options));
}
__declspec(dllexport) uint32_t VulkanStorySlPclWindowMessage() {
    if (!pclState) return 0;
    sl::PCLState state{};
    return pclState(state) == sl::Result::eOk ? state.statsWindowMessage : 0;
}
__declspec(dllexport) int VulkanStorySlSetReflex(int mode, uint32_t maxFps) {
    if (!reflexOptions) return -1;
    sl::ReflexOptions options{};
    options.mode = mode == 2 ? sl::ReflexMode::eLowLatencyWithBoost :
        mode == 1 ? sl::ReflexMode::eLowLatency : sl::ReflexMode::eOff;
    options.frameLimitUs = maxFps ? 1000000u / maxFps : 0;
    return static_cast<int>(reflexOptions(options));
}
__declspec(dllexport) int VulkanStorySlBeginFrame(uint32_t frameIndex) {
    if (!newFrameToken) return -1;
    return static_cast<int>(newFrameToken(frameToken, &frameIndex));
}
__declspec(dllexport) uintptr_t VulkanStorySlCurrentFrameToken() {
    return reinterpret_cast<uintptr_t>(frameToken);
}
__declspec(dllexport) int VulkanStorySlReflexSleep() {
    return reflexSleep && frameToken ? static_cast<int>(reflexSleep(*frameToken)) : -1;
}
__declspec(dllexport) int VulkanStorySlMarker(uint32_t marker) {
    return pclMarker && frameToken ? static_cast<int>(pclMarker(static_cast<sl::PCLMarker>(marker), *frameToken)) : -1;
}
__declspec(dllexport) int VulkanStorySlMarkerForToken(uintptr_t token, uint32_t marker) {
    auto* frame = reinterpret_cast<sl::FrameToken*>(token);
    return pclMarker && frame ? static_cast<int>(pclMarker(static_cast<sl::PCLMarker>(marker), *frame)) : -1;
}
__declspec(dllexport) int VulkanStorySlTagFrame(VkCommandBuffer commandBuffer,
    const TaggedImage* depth, const TaggedImage* motion, const TaggedImage* hudless,
    const TaggedImage* ui, const FrameCamera* camera, uint32_t backbufferWidth,
    uint32_t backbufferHeight) {
    if (!setTagForFrame || !setConstants || !frameToken || !depth || !motion ||
        !hudless || !ui || !camera || !camera->viewToClip || !camera->clipToView ||
        !camera->clipToPrevClip || !camera->prevClipToClip) return -1;
    if (!backbufferWidth || !backbufferHeight) return -2;

    sl::Constants constants{};
    rowMajor(constants.cameraViewToClip, camera->viewToClip);
    rowMajor(constants.clipToCameraView, camera->clipToView);
    rowMajor(constants.clipToPrevClip, camera->clipToPrevClip);
    rowMajor(constants.prevClipToClip, camera->prevClipToClip);
    constants.jitterOffset = {camera->jitterX, camera->jitterY};
    constants.mvecScale = {camera->mvecScaleX, camera->mvecScaleY};
    // The game camera has no lens/pinhole shift; jitter is supplied separately.
    constants.cameraPinholeOffset = {0.f, 0.f};
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
    const sl::Extent backbufferExtent{0, 0, backbufferWidth, backbufferHeight};
      // The renderer reuses its four upright staging images on the next frame,
      // which can begin before this frame reaches the asynchronous Present.
      // Copy them on the tagging command buffer so each Present sees its own
      // depth, motion, scene and UI contents.
      sl::ResourceTag tags[] = {
          {&d, sl::kBufferTypeDepth, sl::ResourceLifecycle::eOnlyValidNow, &depthExtent},
          {&m, sl::kBufferTypeMotionVectors, sl::ResourceLifecycle::eOnlyValidNow, &motionExtent},
          {&h, sl::kBufferTypeHUDLessColor, sl::ResourceLifecycle::eOnlyValidNow, &colorExtent},
          {&u, sl::kBufferTypeUIColorAndAlpha, sl::ResourceLifecycle::eOnlyValidNow, &uiExtent},
          // SL owns the presented swapchain image; only its full extent is tagged.
          {nullptr, sl::kBufferTypeBackbuffer, sl::ResourceLifecycle::eValidUntilPresent, &backbufferExtent},
      };
    return static_cast<int>(setTagForFrame(*frameToken, viewport, tags, 5,
        reinterpret_cast<sl::CommandBuffer*>(commandBuffer)));
}
__declspec(dllexport) int VulkanStorySlInvalidateFrameTagsWithExtent(uint32_t width, uint32_t height) {
    if (!setTagForFrame || !frameToken) return -1;
    const sl::Extent backbufferExtent{0, 0, width, height};
    sl::ResourceTag tags[] = {
        {nullptr, sl::kBufferTypeDepth, sl::ResourceLifecycle::eValidUntilPresent, nullptr},
        {nullptr, sl::kBufferTypeMotionVectors, sl::ResourceLifecycle::eValidUntilPresent, nullptr},
        {nullptr, sl::kBufferTypeHUDLessColor, sl::ResourceLifecycle::eValidUntilPresent, nullptr},
        {nullptr, sl::kBufferTypeUIColorAndAlpha, sl::ResourceLifecycle::eValidUntilPresent, nullptr},
        {nullptr, sl::kBufferTypeBackbuffer, sl::ResourceLifecycle::eValidUntilPresent, &backbufferExtent},
    };
    return static_cast<int>(setTagForFrame(*frameToken, viewport, tags, width && height ? 5 : 4, nullptr));
}
__declspec(dllexport) int VulkanStorySlSetFrameGeneration(int enabled, uint32_t count,
    uint32_t width, uint32_t height, uint32_t colorFormat, uint32_t backBuffers) {
    // Swapchain recreation invalidates the dimensions/options cache, not the
    // feature's enabled state. Off is also the SDK default. Do not re-submit
    // Off for an unused/already-disabled feature when another provider rebuilds
    // presentation; Streamline can observe both requests before one Present.
    // A subsequent On still supplies the new swapchain description below.
    if (!enabled && !fgEnabled) return 0;
    if (!fgOptions) return -1;
    if (enabled && count == 0) return -2;
    if (fgConfigured && fgEnabled == (enabled != 0) && fgCount == count &&
        fgWidth == width && fgHeight == height && fgFormat == colorFormat &&
        fgBackBuffers == backBuffers) return 0;
    sl::DLSSGOptions options{};
    options.mode = enabled ? sl::DLSSGMode::eOn : sl::DLSSGMode::eOff;
    options.numFramesToGenerate = enabled ? count : 1;
    options.colorWidth = width; options.colorHeight = height;
    options.colorBufferFormat = colorFormat; options.numBackBuffers = backBuffers;
    options.enableUserInterfaceRecomposition = sl::Boolean::eTrue;
    options.onErrorCallback = onPresentError;
    int result = static_cast<int>(fgOptions(viewport, options));
    if (result == 0) {
        fgConfigured = true;
        fgEnabled = enabled != 0;
        if (fgEnabled) fgResourcesMayExist = true;
        fgCount = count;
        fgWidth = width; fgHeight = height; fgFormat = colorFormat;
        fgBackBuffers = backBuffers;
    }
    return result;
}
// Called after FG is off and the proxy's DeviceWaitIdle has drained presentation.
// Direct NGX SR shutdown must not precede release of Streamline's FG handle.
__declspec(dllexport) int VulkanStorySlDisableFrameGenerationForRelease() {
    // A different provider may own presentation. Do not configure an unused
    // DLSS-G feature solely to shut it down, or repeat an already-applied Off.
    if (!fgResourcesMayExist || !fgEnabled) return 0;
    return VulkanStorySlSetFrameGeneration(0, fgCount, fgWidth, fgHeight, fgFormat, fgBackBuffers);
}
__declspec(dllexport) int VulkanStorySlFreeFrameGenerationResources() {
    if (!fgOptions || !fgResourcesMayExist) return 0;
    if (!freeResources || fgEnabled) return -1;
    const auto result = freeResources(sl::kFeatureDLSS_G, viewport);
    if (result == sl::Result::eOk) { fgConfigured = false; fgResourcesMayExist = false; }
    return static_cast<int>(result);
}
__declspec(dllexport) int VulkanStorySlTakePresentError() {
    return lastPresentError.exchange(0, std::memory_order_relaxed);
}
__declspec(dllexport) int VulkanStorySlGetFrameGenerationState(uint32_t* status,
    uint32_t* presented, uint32_t* maxGenerated) {
    if (!fgState || !status || !presented || !maxGenerated) return -1;
    sl::DLSSGState state{};
    auto result = fgState(viewport, state, nullptr);
    if (result == sl::Result::eOk) {
        *status = static_cast<uint32_t>(state.status);
        *presented = state.numFramesActuallyPresented;
        *maxGenerated = state.numFramesToGenerateMax;
    }
    return static_cast<int>(result);
}
__declspec(dllexport) int VulkanStorySlGetFrameGenerationStateDetails(uint32_t* values, uint32_t count) {
    if (!fgState || !values || count != 6) return -1;
    sl::DLSSGState state{};
    const auto result = fgState(viewport, state, nullptr);
    if (result == sl::Result::eOk) {
        values[0] = static_cast<uint32_t>(state.status);
        values[1] = state.numFramesActuallyPresented;
        values[2] = state.numFramesToGenerateMax;
        values[3] = state.minWidthOrHeight;
        values[4] = static_cast<uint32_t>(state.bIsVsyncSupportAvailable);
        values[5] = static_cast<uint32_t>(state.bIsDynamicMFGSupported);
    }
    return static_cast<int>(result);
}
}
