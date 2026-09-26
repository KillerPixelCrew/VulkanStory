// XeSS-FG 3.0.2 DX12 presentation bridge. The Intel binaries are loaded from
// this module's directory; no SDK binary is modified or embedded here.
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <vulkan/vulkan.h>
#include <cstdint>
#include <cstring>
#include <cstddef>
#include <array>
#include <new>
#include <string>
#include "xess_fg/xefg_swapchain_d3d12.h"
#include "xell/xell_d3d12.h"

template<typename T> static void Release(T*& value) {
    if (value) { value->Release(); value = nullptr; }
}

struct OptimumXessFg {
    HMODULE fgModule = nullptr;
    HMODULE xellModule = nullptr;
    IDXGIFactory4* factory = nullptr;
    IDXGIAdapter1* adapter = nullptr;
    ID3D12Device* device = nullptr;
    ID3D12CommandQueue* queue = nullptr;
    ID3D12Fence* sharedFence = nullptr;
    IDXGISwapChain4* swapchain = nullptr;
    std::array<ID3D12CommandAllocator*, 3> allocators{};
    std::array<uint64_t, 3> allocatorCompletion{};
    ID3D12GraphicsCommandList* commandList = nullptr;
    ID3D12Fence* completionFence = nullptr;
    HANDLE completionEvent = nullptr;
    uint64_t completionValue = 0;
    uint32_t nextAllocator = 0;
    bool allowTearing = false;
    xell_context_handle_t xell = nullptr;
    xefg_swapchain_handle_t fg = nullptr;
    uint32_t width = 0, height = 0;
    bool started = false;

    decltype(&xellD3D12CreateContext) createXell = nullptr;
    decltype(&xellDestroyContext) destroyXell = nullptr;
    decltype(&xellSetSleepMode) setSleepMode = nullptr;
    decltype(&xellSleep) sleep = nullptr;
    decltype(&xellAddMarkerData) marker = nullptr;
    decltype(&xefgSwapChainD3D12CreateContext) createFg = nullptr;
    decltype(&xefgSwapChainDestroy) destroyFg = nullptr;
    decltype(&xefgSwapChainSetLatencyReduction) setLatency = nullptr;
    decltype(&xefgSwapChainD3D12InitFromSwapChainDesc) initSwapchain = nullptr;
    decltype(&xefgSwapChainD3D12GetSwapChainPtr) getSwapchain = nullptr;
    decltype(&xefgSwapChainSetEnabled) setEnabled = nullptr;
    decltype(&xefgSwapChainSetPresentId) setPresentId = nullptr;
    decltype(&xefgSwapChainSetNumInterpolatedFrames) setInterpolations = nullptr;
    decltype(&xefgSwapChainSetUiCompositionState) setUi = nullptr;
    decltype(&xefgSwapChainD3D12TagFrameResource) tagResource = nullptr;
    decltype(&xefgSwapChainTagFrameConstants) tagConstants = nullptr;
    decltype(&xefgSwapChainGetLastPresentStatus) presentStatus = nullptr;
};

struct OptimumXessFrame {
    uint32_t frameId;
    uint32_t reset;
    uint32_t vsync;
    uint32_t reserved;
    ID3D12Resource* color;
    ID3D12Resource* depth;
    ID3D12Resource* motion;
    ID3D12Resource* hudless;
    ID3D12Resource* ui;
    float viewMatrix[16];
    float projectionMatrix[16];
    float jitterX, jitterY;
    float motionScaleX, motionScaleY;
    uint64_t readyFenceValue;
    uint64_t doneFenceValue;
};
static_assert(sizeof(OptimumXessFrame) == 216);
static_assert(offsetof(OptimumXessFrame, readyFenceValue) == 200);

static std::wstring ModulePath() {
    HMODULE own = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
            GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&ModulePath), &own)) return {};
    wchar_t path[MAX_PATH];
    DWORD length = GetModuleFileNameW(own, path, MAX_PATH);
    if (!length || length == MAX_PATH) return {};
    std::wstring result(path, length);
    auto separator = result.find_last_of(L"\\/");
    return separator == std::wstring::npos ? std::wstring{} : result.substr(0, separator + 1);
}

static bool LoadRuntime(OptimumXessFg* value) {
    std::wstring base = ModulePath();
    if (base.empty()) return false;
    value->fgModule = LoadLibraryExW((base + L"libxess_fg.dll").c_str(), nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    value->xellModule = LoadLibraryExW((base + L"libxell.dll").c_str(), nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!value->fgModule || !value->xellModule) return false;
#define LOAD(module, field, symbol) value->field = reinterpret_cast<decltype(value->field)>(GetProcAddress(value->module, #symbol))
    LOAD(xellModule, createXell, xellD3D12CreateContext);
    LOAD(xellModule, destroyXell, xellDestroyContext);
    LOAD(xellModule, setSleepMode, xellSetSleepMode);
    LOAD(xellModule, sleep, xellSleep);
    LOAD(xellModule, marker, xellAddMarkerData);
    LOAD(fgModule, createFg, xefgSwapChainD3D12CreateContext);
    LOAD(fgModule, destroyFg, xefgSwapChainDestroy);
    LOAD(fgModule, setLatency, xefgSwapChainSetLatencyReduction);
    LOAD(fgModule, initSwapchain, xefgSwapChainD3D12InitFromSwapChainDesc);
    LOAD(fgModule, getSwapchain, xefgSwapChainD3D12GetSwapChainPtr);
    LOAD(fgModule, setEnabled, xefgSwapChainSetEnabled);
    LOAD(fgModule, setPresentId, xefgSwapChainSetPresentId);
    LOAD(fgModule, setInterpolations, xefgSwapChainSetNumInterpolatedFrames);
    LOAD(fgModule, setUi, xefgSwapChainSetUiCompositionState);
    LOAD(fgModule, tagResource, xefgSwapChainD3D12TagFrameResource);
    LOAD(fgModule, tagConstants, xefgSwapChainTagFrameConstants);
    LOAD(fgModule, presentStatus, xefgSwapChainGetLastPresentStatus);
#undef LOAD
    return value->createXell && value->destroyXell && value->setSleepMode &&
        value->sleep && value->marker && value->createFg && value->destroyFg &&
        value->setLatency && value->initSwapchain && value->getSwapchain &&
        value->setEnabled && value->setPresentId && value->setInterpolations &&
        value->setUi && value->tagResource && value->tagConstants &&
        value->presentStatus;
}

extern "C" __declspec(dllexport) int OptimumXessFgWaitIdle(OptimumXessFg* value) {
    if (!value) return -1;
    if (!value->completionFence || !value->completionValue ||
        value->completionFence->GetCompletedValue() >= value->completionValue) return 0;
    if (!value->completionEvent) return -2;
    HRESULT result = value->completionFence->SetEventOnCompletion(
        value->completionValue, value->completionEvent);
    if (FAILED(result)) return static_cast<int>(result);
    return WaitForSingleObject(value->completionEvent, 10000) == WAIT_OBJECT_0 ? 0 : -3;
}

extern "C" __declspec(dllexport) void OptimumXessFgDestroy(OptimumXessFg* value) {
    if (!value) return;
    OptimumXessFgWaitIdle(value);
    if (value->fg && value->setEnabled) value->setEnabled(value->fg, 0);
    Release(value->commandList);
    for (auto*& allocator : value->allocators) Release(allocator);
    Release(value->completionFence);
    if (value->completionEvent) CloseHandle(value->completionEvent);
    Release(value->swapchain);
    if (value->fg && value->destroyFg) value->destroyFg(value->fg);
    value->fg = nullptr;
    if (value->xell && value->destroyXell) value->destroyXell(value->xell);
    value->xell = nullptr;
    Release(value->sharedFence);
    Release(value->queue);
    Release(value->device);
    Release(value->adapter);
    Release(value->factory);
    if (value->fgModule) FreeLibrary(value->fgModule);
    if (value->xellModule) FreeLibrary(value->xellModule);
    delete value;
}

// The 64-bit LUID is copied from VkPhysicalDeviceIDProperties::deviceLUID.
// The exact adapter match prevents cross-GPU resource handoffs.
extern "C" __declspec(dllexport) int OptimumXessFgCreate(uint64_t adapterLuid,
    OptimumXessFg** output) {
    if (!output || !adapterLuid) return -1;
    *output = nullptr;
    auto* value = new (std::nothrow) OptimumXessFg();
    if (!value) return -2;
    int error = 0;
    do {
        if (!LoadRuntime(value)) { error = -3; break; }
        if (FAILED(CreateDXGIFactory2(0, IID_PPV_ARGS(&value->factory)))) { error = -4; break; }
        LUID luid{};
        luid.LowPart = static_cast<DWORD>(adapterLuid);
        luid.HighPart = static_cast<LONG>(adapterLuid >> 32);
        if (FAILED(value->factory->EnumAdapterByLuid(luid, IID_PPV_ARGS(&value->adapter)))) {
            error = -5; break;
        }
        if (FAILED(D3D12CreateDevice(value->adapter, D3D_FEATURE_LEVEL_11_0,
                IID_PPV_ARGS(&value->device)))) { error = -6; break; }
        D3D12_COMMAND_QUEUE_DESC queue{};
        queue.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        if (FAILED(value->device->CreateCommandQueue(&queue,
                IID_PPV_ARGS(&value->queue)))) { error = -7; break; }
        int xell = static_cast<int>(value->createXell(value->device, &value->xell));
        if (xell != 0 || !value->xell) { error = xell ? xell : -8; break; }
        int fg = static_cast<int>(value->createFg(value->device, &value->fg));
        if (fg != 0 || !value->fg) { error = fg ? fg : -9; break; }
        fg = static_cast<int>(value->setLatency(value->fg, value->xell));
        if (fg != 0) { error = fg; break; }
        xell_sleep_params_t mode{};
        mode.bLowLatencyMode = 1;
        xell = static_cast<int>(value->setSleepMode(value->xell, &mode));
        if (xell != 0) { error = xell; break; }
    } while (false);
    if (error) { OptimumXessFgDestroy(value); return error; }
    *output = value;
    return 0;
}

extern "C" __declspec(dllexport) int OptimumXessFgCreateFromVulkan(
    VkPhysicalDevice physical, OptimumXessFg** output) {
    if (!physical || !output) return -1;
    HMODULE loader = GetModuleHandleW(L"vulkan-1.dll");
    auto properties = loader ? reinterpret_cast<PFN_vkGetPhysicalDeviceProperties2>(
        GetProcAddress(loader, "vkGetPhysicalDeviceProperties2")) : nullptr;
    if (!properties) return -10;
    VkPhysicalDeviceIDProperties id{};
    id.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES;
    VkPhysicalDeviceProperties2 props{};
    props.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2;
    props.pNext = &id;
    properties(physical, &props);
    if (!id.deviceLUIDValid) return -11;
    uint64_t luid = 0;
    std::memcpy(&luid, id.deviceLUID, VK_LUID_SIZE);
    return OptimumXessFgCreate(luid, output);
}

extern "C" __declspec(dllexport) int OptimumXessFgStart(OptimumXessFg* value,
    HWND window, uint32_t width, uint32_t height, uint32_t vsync) {
    if (!value || !window || !width || !height || value->started) return -1;
    (void)vsync;
    IDXGIFactory5* factory5 = nullptr;
    if (SUCCEEDED(value->factory->QueryInterface(IID_PPV_ARGS(&factory5)))) {
        BOOL supported = FALSE;
        value->allowTearing = SUCCEEDED(factory5->CheckFeatureSupport(
            DXGI_FEATURE_PRESENT_ALLOW_TEARING, &supported, sizeof(supported))) && supported;
        Release(factory5);
    }
    DXGI_SWAP_CHAIN_DESC1 desc{};
    desc.Width = width;
    desc.Height = height;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    desc.BufferCount = 3;
    desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    desc.Flags = value->allowTearing ? DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING : 0;
    xefg_swapchain_d3d12_init_params_t params{};
    params.maxInterpolatedFrames = 1;
    params.uiMode = XEFG_SWAPCHAIN_UI_MODE_HUDLESS_UITEXTURE;
    int code = static_cast<int>(value->initSwapchain(value->fg, window, &desc, nullptr,
        value->queue, value->factory, &params));
    if (code < 0) return code;
    code = static_cast<int>(value->getSwapchain(value->fg,
        __uuidof(IDXGISwapChain4), reinterpret_cast<void**>(&value->swapchain)));
    if (code < 0 || !value->swapchain) return code < 0 ? code : -2;
    // HUDLESS_UITEXTURE selects the composition method, but the SDK leaves
    // composition disabled until this separate switch is enabled.
    code = static_cast<int>(value->setUi(value->fg,
        XEFG_SWAPCHAIN_UI_COMPOSITION_STATE_ENABLED));
    if (code < 0) return code;
    HRESULT result = value->device->CreateFence(0, D3D12_FENCE_FLAG_NONE,
        IID_PPV_ARGS(&value->completionFence));
    if (FAILED(result)) return static_cast<int>(result);
    value->completionEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!value->completionEvent) return -3;
    for (auto*& allocator : value->allocators) {
        result = value->device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT,
            IID_PPV_ARGS(&allocator));
        if (FAILED(result)) return static_cast<int>(result);
    }
    result = value->device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT,
        value->allocators[0], nullptr, IID_PPV_ARGS(&value->commandList));
    if (FAILED(result)) return static_cast<int>(result);
    result = value->commandList->Close();
    if (FAILED(result)) return static_cast<int>(result);
    value->width = width;
    value->height = height;
    value->started = true;
    return 0;
}

extern "C" __declspec(dllexport) int OptimumXessFgSetEnabled(OptimumXessFg* value,
    uint32_t enabled) {
    return value && value->fg && value->started ?
        static_cast<int>(value->setEnabled(value->fg, enabled ? 1 : 0)) : -1;
}

// VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D12_RESOURCE_BIT is a handle to a D3D12
// committed resource created by ID3D12Device::CreateSharedHandle. Vulkan imports
// this handle; it is not a handle exported from an arbitrary Vulkan allocation.
extern "C" __declspec(dllexport) int OptimumXessFgCreateSharedImage(OptimumXessFg* value,
    uint32_t width, uint32_t height, uint32_t vkFormat, HANDLE* sharedHandle,
    ID3D12Resource** resource) {
    if (!value || !value->device || !width || !height || !sharedHandle || !resource)
        return -1;
    *sharedHandle = nullptr;
    *resource = nullptr;
    DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
    D3D12_RESOURCE_FLAGS flags = D3D12_RESOURCE_FLAG_NONE;
    switch (static_cast<VkFormat>(vkFormat)) {
        case VK_FORMAT_R8G8B8A8_UNORM: format = DXGI_FORMAT_R8G8B8A8_UNORM; break;
        case VK_FORMAT_B8G8R8A8_UNORM: format = DXGI_FORMAT_B8G8R8A8_UNORM; break;
        case VK_FORMAT_R16G16_SFLOAT: format = DXGI_FORMAT_R16G16_FLOAT; break;
        case VK_FORMAT_R16G16B16A16_SFLOAT: format = DXGI_FORMAT_R16G16B16A16_FLOAT; break;
        case VK_FORMAT_R32_SFLOAT: format = DXGI_FORMAT_R32_FLOAT; break;
        case VK_FORMAT_D32_SFLOAT:
            format = DXGI_FORMAT_D32_FLOAT;
            flags = D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL;
            break;
        default: return -2;
    }
    D3D12_HEAP_PROPERTIES heap{};
    heap.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_DESC desc{};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    desc.Width = width;
    desc.Height = height;
    desc.DepthOrArraySize = 1;
    desc.MipLevels = 1;
    desc.Format = format;
    desc.SampleDesc.Count = 1;
    desc.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
    desc.Flags = flags;
    HRESULT result = value->device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_SHARED,
        &desc, D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(resource));
    if (FAILED(result)) return static_cast<int>(result);
    result = value->device->CreateSharedHandle(*resource, nullptr, GENERIC_ALL,
        nullptr, sharedHandle);
    if (FAILED(result)) {
        Release(*resource);
        return static_cast<int>(result);
    }
    return 0;
}

extern "C" __declspec(dllexport) void OptimumXessFgReleaseImage(ID3D12Resource* resource) {
    Release(resource);
}

// The caller closes the NT handle after a permanent Vulkan semaphore import.
// The bridge retains the ID3D12Fence for its command queue's GPU-side waits.
extern "C" __declspec(dllexport) int OptimumXessFgCreateSharedFence(
    OptimumXessFg* value, HANDLE* sharedHandle) {
    if (!value || !value->device || !sharedHandle) return -1;
    *sharedHandle = nullptr;
    if (!value->sharedFence) {
        HRESULT result = value->device->CreateFence(0, D3D12_FENCE_FLAG_SHARED,
            IID_PPV_ARGS(&value->sharedFence));
        if (FAILED(result)) return static_cast<int>(result);
    }
    HRESULT result = value->device->CreateSharedHandle(value->sharedFence, nullptr,
        GENERIC_ALL, nullptr, sharedHandle);
    return FAILED(result) ? static_cast<int>(result) : 0;
}

extern "C" __declspec(dllexport) int OptimumXessFgWaitSharedFence(
    OptimumXessFg* value, uint64_t fenceValue) {
    if (!value || !value->queue || !value->sharedFence || !fenceValue) return -1;
    HRESULT result = value->queue->Wait(value->sharedFence, fenceValue);
    return FAILED(result) ? static_cast<int>(result) : 0;
}

extern "C" __declspec(dllexport) int OptimumXessFgSignalSharedFence(
    OptimumXessFg* value, uint64_t fenceValue) {
    if (!value || !value->queue || !value->sharedFence || !fenceValue) return -1;
    HRESULT result = value->queue->Signal(value->sharedFence, fenceValue);
    return FAILED(result) ? static_cast<int>(result) : 0;
}

static void Transition(ID3D12GraphicsCommandList* list, ID3D12Resource* resource,
    D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after) {
    D3D12_RESOURCE_BARRIER barrier{};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = resource;
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    barrier.Transition.StateBefore = before;
    barrier.Transition.StateAfter = after;
    list->ResourceBarrier(1, &barrier);
}

// The Vulkan queue signals readyFenceValue after releasing shared-image
// ownership. The SDK records its ONLY_NOW input copies into this command list;
// doneFenceValue is signalled only after its proxy Present has queued work.
extern "C" __declspec(dllexport) int OptimumXessFgPresent(OptimumXessFg* value,
    const OptimumXessFrame* frame, uint32_t* framesPresented,
    int* frameGenResult, uint32_t* frameGenEnabled) {
    if (framesPresented) *framesPresented = 0;
    if (frameGenResult) *frameGenResult = 0;
    if (frameGenEnabled) *frameGenEnabled = 0;
    if (!value || !value->started || !frame || !framesPresented ||
        !frameGenResult || !frameGenEnabled || !value->sharedFence ||
        !frame->frameId || !frame->readyFenceValue ||
        frame->doneFenceValue <= frame->readyFenceValue ||
        !frame->color || !frame->depth || !frame->motion ||
        !frame->hudless || !frame->ui) return -1;

    D3D12_RESOURCE_DESC color = frame->color->GetDesc();
    D3D12_RESOURCE_DESC hudless = frame->hudless->GetDesc();
    D3D12_RESOURCE_DESC ui = frame->ui->GetDesc();
    D3D12_RESOURCE_DESC motion = frame->motion->GetDesc();
    D3D12_RESOURCE_DESC depth = frame->depth->GetDesc();
    if (color.Width != value->width || color.Height != value->height ||
        color.Format != DXGI_FORMAT_B8G8R8A8_UNORM ||
        hudless.Width != value->width || hudless.Height != value->height ||
        hudless.Format != color.Format || ui.Width != value->width ||
        ui.Height != value->height || ui.Format != color.Format ||
        motion.Width != depth.Width || motion.Height != depth.Height ||
        motion.Format != DXGI_FORMAT_R16G16_FLOAT ||
        depth.Format != DXGI_FORMAT_D32_FLOAT) return -2;

    uint32_t slot = value->nextAllocator++ % static_cast<uint32_t>(value->allocators.size());
    uint64_t completion = value->allocatorCompletion[slot];
    if (completion && value->completionFence->GetCompletedValue() < completion) {
        HRESULT result = value->completionFence->SetEventOnCompletion(completion,
            value->completionEvent);
        if (FAILED(result)) return static_cast<int>(result);
        if (WaitForSingleObject(value->completionEvent, 10000) != WAIT_OBJECT_0) return -3;
    }
    HRESULT result = value->allocators[slot]->Reset();
    if (FAILED(result)) return static_cast<int>(result);
    result = value->commandList->Reset(value->allocators[slot], nullptr);
    if (FAILED(result)) return static_cast<int>(result);

    std::array<ID3D12Resource*, 5> inputs = {
        frame->color, frame->depth, frame->motion, frame->hudless, frame->ui
    };
    for (ID3D12Resource* input : inputs)
        Transition(value->commandList, input, D3D12_RESOURCE_STATE_COMMON,
            D3D12_RESOURCE_STATE_COPY_SOURCE);
    ID3D12Resource* backbuffer = nullptr;
    uint32_t backbufferIndex = value->swapchain->GetCurrentBackBufferIndex();
    result = value->swapchain->GetBuffer(backbufferIndex, IID_PPV_ARGS(&backbuffer));
    if (FAILED(result)) { value->commandList->Close(); return static_cast<int>(result); }
    Transition(value->commandList, backbuffer, D3D12_RESOURCE_STATE_PRESENT,
        D3D12_RESOURCE_STATE_COPY_DEST);
    value->commandList->CopyResource(backbuffer, frame->color);

    struct Tag { ID3D12Resource* resource; xefg_swapchain_resource_type_t type; };
    std::array<Tag, 4> tags = {{
        {frame->depth, XEFG_SWAPCHAIN_RES_DEPTH},
        {frame->motion, XEFG_SWAPCHAIN_RES_MOTION_VECTOR},
        {frame->hudless, XEFG_SWAPCHAIN_RES_HUDLESS_COLOR},
        {frame->ui, XEFG_SWAPCHAIN_RES_UI},
    }};
    for (const Tag& tag : tags) {
        D3D12_RESOURCE_DESC desc = tag.resource->GetDesc();
        xefg_swapchain_d3d12_resource_data_t data{};
        data.type = tag.type;
        data.validity = XEFG_SWAPCHAIN_RV_ONLY_NOW;
        data.resourceSize = {static_cast<uint32_t>(desc.Width), desc.Height};
        data.pResource = tag.resource;
        data.incomingState = D3D12_RESOURCE_STATE_COPY_SOURCE;
        int code = static_cast<int>(value->tagResource(value->fg, value->commandList,
            frame->frameId, &data));
        if (code < 0) {
            value->commandList->Close();
            Release(backbuffer);
            return code;
        }
    }
    for (ID3D12Resource* input : inputs)
        Transition(value->commandList, input, D3D12_RESOURCE_STATE_COPY_SOURCE,
            D3D12_RESOURCE_STATE_COMMON);
    Transition(value->commandList, backbuffer, D3D12_RESOURCE_STATE_COPY_DEST,
        D3D12_RESOURCE_STATE_PRESENT);
    Release(backbuffer);
    result = value->commandList->Close();
    if (FAILED(result)) return static_cast<int>(result);

    xefg_swapchain_frame_constant_data_t constants{};
    std::memcpy(constants.viewMatrix, frame->viewMatrix, sizeof(constants.viewMatrix));
    std::memcpy(constants.projectionMatrix, frame->projectionMatrix,
        sizeof(constants.projectionMatrix));
    constants.jitterOffsetX = frame->jitterX;
    constants.jitterOffsetY = frame->jitterY;
    constants.motionVectorScaleX = frame->motionScaleX;
    constants.motionVectorScaleY = frame->motionScaleY;
    constants.resetHistory = frame->reset;
    int code = static_cast<int>(value->tagConstants(value->fg, frame->frameId, &constants));
    if (code < 0) return code;
    result = value->queue->Wait(value->sharedFence, frame->readyFenceValue);
    if (FAILED(result)) return static_cast<int>(result);
    ID3D12CommandList* lists[] = {value->commandList};
    value->queue->ExecuteCommandLists(1, lists);
    int presentIdCode = static_cast<int>(value->setPresentId(value->fg, frame->frameId));
    uint32_t flags = !frame->vsync && value->allowTearing ? DXGI_PRESENT_ALLOW_TEARING : 0;
    result = presentIdCode >= 0 ? value->swapchain->Present(frame->vsync ? 1 : 0, flags) : E_FAIL;
    // Even a failed present must release Vulkan's shared images after the
    // already-submitted copy/tag list has finished on this queue.
    HRESULT handoff = value->queue->Signal(value->sharedFence, frame->doneFenceValue);
    uint64_t finished = ++value->completionValue;
    HRESULT retire = value->queue->Signal(value->completionFence, finished);
    value->allocatorCompletion[slot] = finished;
    if (FAILED(handoff)) return static_cast<int>(handoff);
    if (FAILED(retire)) return static_cast<int>(retire);
    if (presentIdCode < 0) return presentIdCode;
    if (FAILED(result)) return static_cast<int>(result);
    xefg_swapchain_present_status_t status{};
    code = static_cast<int>(value->presentStatus(value->fg, &status));
    if (code < 0) return code;
    *framesPresented = status.framesPresented;
    *frameGenResult = static_cast<int>(status.frameGenResult);
    *frameGenEnabled = status.isFrameGenEnabled;
    return 0;
}

extern "C" __declspec(dllexport) int OptimumXessFgSetLatencyMode(OptimumXessFg* value,
    uint32_t minimumIntervalUs, uint32_t enabled) {
    if (!value || !value->xell) return -1;
    xell_sleep_params_t mode{};
    mode.minimumIntervalUs = minimumIntervalUs;
    mode.bLowLatencyMode = enabled ? 1 : 0;
    return static_cast<int>(value->setSleepMode(value->xell, &mode));
}

extern "C" __declspec(dllexport) int OptimumXessFgSleep(OptimumXessFg* value,
    uint32_t frameId) {
    return value && value->xell ?
        static_cast<int>(value->sleep(value->xell, frameId)) : -1;
}

extern "C" __declspec(dllexport) int OptimumXessFgMarker(OptimumXessFg* value,
    uint32_t frameId, xell_latency_marker_type_t marker) {
    return value && value->xell ?
        static_cast<int>(value->marker(value->xell, frameId, marker)) : -1;
}
