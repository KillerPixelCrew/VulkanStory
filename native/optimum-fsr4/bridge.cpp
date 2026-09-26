// AMD FSR 4 DX12 upscaler on the Vulkan adapter. Shared D3D12 resources are
// imported by Vulkan; a D3D12 fence orders both queues without CPU readback.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <vulkan/vulkan.h>
#include <cstdint>
#include <cstddef>
#include <cstring>
#include <cwchar>
#include <new>
#include <string>
#include "api/include/dx12/ffx_api_dx12.h"
#include "upscalers/include/ffx_upscale.h"

template<class T> static void Release(T*& object) {
    if (object) { object->Release(); object = nullptr; }
}

struct OptimumFsr4Frame {
    ID3D12Resource* color;
    ID3D12Resource* depth;
    ID3D12Resource* motion;
    ID3D12Resource* output;
    uint32_t renderWidth, renderHeight, displayWidth, displayHeight;
    float jitterX, jitterY, deltaMs, nearPlane, farPlane, fov;
    uint32_t reset;
    uint64_t readyValue, doneValue;
};
static_assert(sizeof(OptimumFsr4Frame) == 96 &&
    offsetof(OptimumFsr4Frame, readyValue) == 80);

struct OptimumFsr4 {
    HMODULE module{};
    IDXGIFactory4* factory{};
    IDXGIAdapter1* adapter{};
    ID3D12Device* device{};
    ID3D12CommandQueue* queue{};
    ID3D12Fence* sharedFence{};
    ID3D12Fence* completionFence{};
    HANDLE completionEvent{};
    ID3D12CommandAllocator* allocators[3]{};
    uint64_t allocatorValues[3]{};
    ID3D12GraphicsCommandList* commands{};
    uint64_t completionValue{};
    uint32_t nextAllocator{};
    ffxContext effect{};
    PfnFfxCreateContext create{};
    PfnFfxDestroyContext destroy{};
    PfnFfxDispatch dispatch{};
    PfnFfxQuery query{};
};

extern "C" __declspec(dllexport) int OptimumFsr4Probe(VkPhysicalDevice physical) {
    if (!physical) return -1;
    HMODULE loader = GetModuleHandleW(L"vulkan-1.dll");
    auto properties = loader ? reinterpret_cast<PFN_vkGetPhysicalDeviceProperties>(
        GetProcAddress(loader, "vkGetPhysicalDeviceProperties")) : nullptr;
    if (!properties) return -2;
    VkPhysicalDeviceProperties info{};
    properties(physical, &info);
    return info.vendorID == 0x1002 ? 0 : -3;
}

static std::wstring OwnDirectory() {
    HMODULE own{};
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
            GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&OwnDirectory), &own)) return {};
    wchar_t path[MAX_PATH]{};
    DWORD length = GetModuleFileNameW(own, path, MAX_PATH);
    if (!length || length == MAX_PATH) return {};
    std::wstring result(path, length);
    auto slash = result.find_last_of(L"\\/");
    return slash == std::wstring::npos ? std::wstring{} : result.substr(0, slash + 1);
}

extern "C" __declspec(dllexport) int OptimumFsr4WaitIdle(OptimumFsr4* value) {
    if (!value || !value->completionFence || !value->completionValue ||
        value->completionFence->GetCompletedValue() >= value->completionValue) return 0;
    if (!value->completionEvent) return -1;
    HRESULT hr = value->completionFence->SetEventOnCompletion(
        value->completionValue, value->completionEvent);
    if (FAILED(hr)) return static_cast<int>(hr);
    return WaitForSingleObject(value->completionEvent, 10000) == WAIT_OBJECT_0 ? 0 : -2;
}

extern "C" __declspec(dllexport) void OptimumFsr4Destroy(OptimumFsr4* value) {
    if (!value) return;
    OptimumFsr4WaitIdle(value);
    if (value->effect && value->destroy) value->destroy(&value->effect, nullptr);
    Release(value->commands);
    for (auto*& allocator : value->allocators) Release(allocator);
    Release(value->completionFence);
    Release(value->sharedFence);
    Release(value->queue);
    Release(value->device);
    Release(value->adapter);
    Release(value->factory);
    if (value->completionEvent) CloseHandle(value->completionEvent);
    if (value->module) FreeLibrary(value->module);
    delete value;
}

extern "C" __declspec(dllexport) int OptimumFsr4Create(
    VkPhysicalDevice physical, uint32_t renderWidth, uint32_t renderHeight,
    uint32_t displayWidth, uint32_t displayHeight, OptimumFsr4** output) {
    if (!physical || !output || !renderWidth || !renderHeight ||
        !displayWidth || !displayHeight) return -1;
    *output = nullptr;
    if (OptimumFsr4Probe(physical) != 0) return -11;
    auto* value = new (std::nothrow) OptimumFsr4();
    if (!value) return -2;
    int error = 0;
    do {
        HMODULE loader = GetModuleHandleW(L"vulkan-1.dll");
        auto properties = loader ? reinterpret_cast<PFN_vkGetPhysicalDeviceProperties2>(
            GetProcAddress(loader, "vkGetPhysicalDeviceProperties2")) : nullptr;
        if (!properties) { error = -3; break; }
        VkPhysicalDeviceIDProperties id{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES};
        VkPhysicalDeviceProperties2 props{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2};
        props.pNext = &id;
        properties(physical, &props);
        if (!id.deviceLUIDValid) { error = -4; break; }
        uint64_t luid{};
        std::memcpy(&luid, id.deviceLUID, VK_LUID_SIZE);
        HRESULT hr = CreateDXGIFactory2(0, IID_PPV_ARGS(&value->factory));
        if (FAILED(hr)) { error = static_cast<int>(hr); break; }
        for (UINT index = 0; ; ++index) {
            IDXGIAdapter1* candidate{};
            if (value->factory->EnumAdapters1(index, &candidate) == DXGI_ERROR_NOT_FOUND) break;
            DXGI_ADAPTER_DESC1 description{};
            candidate->GetDesc1(&description);
            uint64_t candidateLuid{};
            std::memcpy(&candidateLuid, &description.AdapterLuid, sizeof(candidateLuid));
            if (candidateLuid == luid) { value->adapter = candidate; break; }
            candidate->Release();
        }
        if (!value->adapter) { error = -5; break; }
        hr = D3D12CreateDevice(value->adapter, D3D_FEATURE_LEVEL_12_0,
            IID_PPV_ARGS(&value->device));
        if (FAILED(hr)) { error = static_cast<int>(hr); break; }
        D3D12_COMMAND_QUEUE_DESC queue{};
        queue.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        hr = value->device->CreateCommandQueue(&queue, IID_PPV_ARGS(&value->queue));
        if (FAILED(hr)) { error = static_cast<int>(hr); break; }
        std::wstring base = OwnDirectory();
        if (base.empty()) { error = -6; break; }
        value->module = LoadLibraryExW((base + L"amd_fidelityfx_upscaler_dx12.dll").c_str(),
            nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (!value->module) { error = -7; break; }
        value->create = reinterpret_cast<PfnFfxCreateContext>(GetProcAddress(value->module, "ffxCreateContext"));
        value->destroy = reinterpret_cast<PfnFfxDestroyContext>(GetProcAddress(value->module, "ffxDestroyContext"));
        value->dispatch = reinterpret_cast<PfnFfxDispatch>(GetProcAddress(value->module, "ffxDispatch"));
        value->query = reinterpret_cast<PfnFfxQuery>(GetProcAddress(value->module, "ffxQuery"));
        if (!value->create || !value->destroy || !value->dispatch || !value->query) {
            error = -8; break;
        }
        ffxCreateContextDescUpscale upscale{};
        upscale.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE;
        upscale.flags = FFX_UPSCALE_ENABLE_HIGH_DYNAMIC_RANGE | FFX_UPSCALE_ENABLE_AUTO_EXPOSURE;
        upscale.maxRenderSize = {renderWidth, renderHeight};
        upscale.maxUpscaleSize = {displayWidth, displayHeight};
        ffxCreateBackendDX12Desc backend{};
        backend.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12;
        backend.device = value->device;
        ffxCreateContextDescUpscaleVersion version{};
        version.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE_VERSION;
        version.version = FFX_UPSCALER_VERSION;
        upscale.header.pNext = &backend.header;
        backend.header.pNext = &version.header;
        error = static_cast<int>(value->create(&value->effect, &upscale.header, nullptr));
        if (error) break;
        ffxQueryGetProviderVersion provider{};
        provider.header.type = FFX_API_QUERY_DESC_TYPE_GET_PROVIDER_VERSION;
        error = static_cast<int>(value->query(&value->effect, &provider.header));
        if (error) break;
        // The signed loader can select a legacy FSR provider on older GPUs.
        // This option promises the ML upscaler, so reject that substitution.
        if (!provider.versionName ||
            (!std::strstr(provider.versionName, "FSR4") &&
             !std::strstr(provider.versionName, "FSR 4"))) { error = -9; break; }
        hr = value->device->CreateFence(0, D3D12_FENCE_FLAG_NONE,
            IID_PPV_ARGS(&value->completionFence));
        if (FAILED(hr)) { error = static_cast<int>(hr); break; }
        value->completionEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (!value->completionEvent) { error = -10; break; }
        for (auto*& allocator : value->allocators) {
            hr = value->device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT,
                IID_PPV_ARGS(&allocator));
            if (FAILED(hr)) { error = static_cast<int>(hr); break; }
        }
        if (error) break;
        hr = value->device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT,
            value->allocators[0], nullptr, IID_PPV_ARGS(&value->commands));
        if (FAILED(hr)) { error = static_cast<int>(hr); break; }
        hr = value->commands->Close();
        if (FAILED(hr)) { error = static_cast<int>(hr); break; }
    } while (false);
    if (error) { OptimumFsr4Destroy(value); return error; }
    *output = value;
    return 0;
}

extern "C" __declspec(dllexport) int OptimumFsr4CreateSharedImage(
    OptimumFsr4* value, uint32_t width, uint32_t height, uint32_t vkFormat,
    uint32_t writable, HANDLE* handle, ID3D12Resource** resource) {
    if (!value || !width || !height || !handle || !resource) return -1;
    *handle = nullptr; *resource = nullptr;
    DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
    D3D12_RESOURCE_FLAGS flags = D3D12_RESOURCE_FLAG_NONE;
    switch (static_cast<VkFormat>(vkFormat)) {
        case VK_FORMAT_R16G16B16A16_SFLOAT: format = DXGI_FORMAT_R16G16B16A16_FLOAT; break;
        case VK_FORMAT_R16G16_SFLOAT: format = DXGI_FORMAT_R16G16_FLOAT; break;
        case VK_FORMAT_D32_SFLOAT:
            format = DXGI_FORMAT_D32_FLOAT;
            flags = D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL;
            break;
        default: return -2;
    }
    if (writable) {
        if (flags != D3D12_RESOURCE_FLAG_NONE) return -3;
        flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
    }
    D3D12_HEAP_PROPERTIES heap{}; heap.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_DESC desc{};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    desc.Width = width; desc.Height = height;
    desc.DepthOrArraySize = 1; desc.MipLevels = 1;
    desc.Format = format; desc.SampleDesc.Count = 1;
    desc.Flags = flags;
    HRESULT hr = value->device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_SHARED,
        &desc, D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(resource));
    if (FAILED(hr)) return static_cast<int>(hr);
    hr = value->device->CreateSharedHandle(*resource, nullptr, GENERIC_ALL,
        nullptr, handle);
    if (FAILED(hr)) { Release(*resource); return static_cast<int>(hr); }
    return 0;
}

extern "C" __declspec(dllexport) void OptimumFsr4ReleaseImage(ID3D12Resource* resource) {
    Release(resource);
}

extern "C" __declspec(dllexport) int OptimumFsr4CreateSharedFence(
    OptimumFsr4* value, HANDLE* handle) {
    if (!value || !handle) return -1;
    *handle = nullptr;
    if (!value->sharedFence) {
        HRESULT hr = value->device->CreateFence(0, D3D12_FENCE_FLAG_SHARED,
            IID_PPV_ARGS(&value->sharedFence));
        if (FAILED(hr)) return static_cast<int>(hr);
    }
    HRESULT hr = value->device->CreateSharedHandle(value->sharedFence,
        nullptr, GENERIC_ALL, nullptr, handle);
    return FAILED(hr) ? static_cast<int>(hr) : 0;
}

static void Transition(ID3D12GraphicsCommandList* commands, ID3D12Resource* resource,
    D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after) {
    D3D12_RESOURCE_BARRIER barrier{};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = resource;
    barrier.Transition.StateBefore = before;
    barrier.Transition.StateAfter = after;
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    commands->ResourceBarrier(1, &barrier);
}

extern "C" __declspec(dllexport) int OptimumFsr4Evaluate(
    OptimumFsr4* value, const OptimumFsr4Frame* frame) {
    if (!value || !frame || !frame->color || !frame->depth || !frame->motion ||
        !frame->output || !frame->readyValue || !frame->doneValue ||
        !value->sharedFence) return -1;
    uint32_t slot = value->nextAllocator++ % 3;
    if (value->allocatorValues[slot] &&
        value->completionFence->GetCompletedValue() < value->allocatorValues[slot]) {
        HRESULT hr = value->completionFence->SetEventOnCompletion(
            value->allocatorValues[slot], value->completionEvent);
        if (FAILED(hr) || WaitForSingleObject(value->completionEvent, 10000) != WAIT_OBJECT_0)
            return -2;
    }
    HRESULT hr = value->allocators[slot]->Reset();
    if (FAILED(hr)) return static_cast<int>(hr);
    hr = value->commands->Reset(value->allocators[slot], nullptr);
    if (FAILED(hr)) return static_cast<int>(hr);
    constexpr auto read = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
    Transition(value->commands, frame->color, D3D12_RESOURCE_STATE_COMMON, read);
    Transition(value->commands, frame->depth, D3D12_RESOURCE_STATE_COMMON, read);
    Transition(value->commands, frame->motion, D3D12_RESOURCE_STATE_COMMON, read);
    Transition(value->commands, frame->output, D3D12_RESOURCE_STATE_COMMON,
        D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
    ffxDispatchDescUpscale upscale{};
    upscale.header.type = FFX_API_DISPATCH_DESC_TYPE_UPSCALE;
    upscale.commandList = value->commands;
    upscale.color = ffxApiGetResourceDX12(frame->color, FFX_API_RESOURCE_STATE_COMPUTE_READ);
    upscale.depth = ffxApiGetResourceDX12(frame->depth, FFX_API_RESOURCE_STATE_COMPUTE_READ);
    upscale.motionVectors = ffxApiGetResourceDX12(frame->motion, FFX_API_RESOURCE_STATE_COMPUTE_READ);
    upscale.output = ffxApiGetResourceDX12(frame->output, FFX_API_RESOURCE_STATE_UNORDERED_ACCESS);
    upscale.jitterOffset = {frame->jitterX, frame->jitterY};
    upscale.motionVectorScale = {1.0f, 1.0f};
    upscale.renderSize = {frame->renderWidth, frame->renderHeight};
    upscale.upscaleSize = {frame->displayWidth, frame->displayHeight};
    upscale.frameTimeDelta = frame->deltaMs > 0.0f ? frame->deltaMs : 16.667f;
    upscale.preExposure = 1.0f;
    upscale.reset = frame->reset != 0;
    upscale.cameraNear = frame->nearPlane;
    upscale.cameraFar = frame->farPlane;
    upscale.cameraFovAngleVertical = frame->fov;
    upscale.viewSpaceToMetersFactor = 1.0f;
    int code = static_cast<int>(value->dispatch(&value->effect, &upscale.header));
    if (code != 0) {
        value->commands->Close();
        return code;
    }
    Transition(value->commands, frame->color, read, D3D12_RESOURCE_STATE_COMMON);
    Transition(value->commands, frame->depth, read, D3D12_RESOURCE_STATE_COMMON);
    Transition(value->commands, frame->motion, read, D3D12_RESOURCE_STATE_COMMON);
    Transition(value->commands, frame->output, D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
        D3D12_RESOURCE_STATE_COMMON);
    hr = value->commands->Close();
    if (FAILED(hr)) return static_cast<int>(hr);
    hr = value->queue->Wait(value->sharedFence, frame->readyValue);
    if (FAILED(hr)) return static_cast<int>(hr);
    ID3D12CommandList* lists[] = {value->commands};
    value->queue->ExecuteCommandLists(1, lists);
    hr = value->queue->Signal(value->sharedFence, frame->doneValue);
    if (FAILED(hr)) return static_cast<int>(hr);
    uint64_t completed = ++value->completionValue;
    hr = value->queue->Signal(value->completionFence, completed);
    if (FAILED(hr)) return static_cast<int>(hr);
    value->allocatorValues[slot] = completed;
    return 0;
}
