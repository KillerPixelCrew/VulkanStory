#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <cstdint>
#include <cstring>
#include <memory>

template<class T> static void release(T*& p) { if (p) { p->Release(); p = nullptr; } }
struct Presenter {
    IDXGIFactory4* factory{};
    ID3D12Device* device{};
    ID3D12CommandQueue* queue{};
    IDXGISwapChain3* swapchain{};
    ID3D12CommandAllocator* allocators[3]{};
    ID3D12GraphicsCommandList* commands{};
    ID3D12Fence* complete{};
    ID3D12Fence* shared{};
    HANDLE event{};
    uint64_t submitted{}, slots[3]{};
    uint32_t width{}, height{};
    ~Presenter() {
        release(commands);
        for (auto*& p : allocators) release(p);
        release(swapchain); release(shared); release(complete); release(queue);
        release(device); release(factory);
        if (event) CloseHandle(event);
    }
};
static HRESULT wait(Presenter* p, uint64_t value) {
    if (!value || p->complete->GetCompletedValue() >= value) return S_OK;
    HRESULT hr = p->complete->SetEventOnCompletion(value, p->event);
    if (FAILED(hr)) return hr;
    return WaitForSingleObject(p->event, 30000) == WAIT_OBJECT_0 ? S_OK : HRESULT_FROM_WIN32(ERROR_TIMEOUT);
}
extern "C" __declspec(dllexport) int AuxWaitIdle(Presenter* p) {
    if (!p) return E_INVALIDARG;
    uint64_t serial = p->submitted + 1;
    HRESULT hr = p->queue->Signal(p->complete, serial);
    if (FAILED(hr)) return hr;
    p->submitted = serial;
    return static_cast<int>(wait(p, serial));
}
extern "C" __declspec(dllexport) int AuxDestroy(Presenter* p) {
    if (!p) return 0;
    int hr = AuxWaitIdle(p);
    if (hr != 0) return hr;
    delete p;
    return 0;
}
extern "C" __declspec(dllexport) int AuxCreate(uint64_t adapterLuid, HWND window,
    uint32_t width, uint32_t height, Presenter** output) {
    if (!output || !window || !width || !height) return E_INVALIDARG;
    *output = nullptr;
    auto p = std::make_unique<Presenter>();
    HRESULT hr = CreateDXGIFactory2(0, IID_PPV_ARGS(&p->factory));
    if (FAILED(hr)) return hr;
    LUID luid{}; std::memcpy(&luid, &adapterLuid, sizeof(luid));
    IDXGIAdapter* adapter{};
    hr = p->factory->EnumAdapterByLuid(luid, IID_PPV_ARGS(&adapter));
    if (FAILED(hr)) return hr;
    hr = D3D12CreateDevice(adapter, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&p->device));
    release(adapter);
    if (FAILED(hr)) return hr;
    D3D12_COMMAND_QUEUE_DESC queue{};
    queue.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    hr = p->device->CreateCommandQueue(&queue, IID_PPV_ARGS(&p->queue));
    if (FAILED(hr)) return hr;
    DXGI_SWAP_CHAIN_DESC1 desc{};
    desc.Width = width; desc.Height = height;
    desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    desc.BufferCount = 3;
    desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    desc.Scaling = DXGI_SCALING_STRETCH;
    IDXGISwapChain1* chain{};
    hr = p->factory->CreateSwapChainForHwnd(p->queue, window, &desc, nullptr, nullptr, &chain);
    if (FAILED(hr)) return hr;
    hr = chain->QueryInterface(IID_PPV_ARGS(&p->swapchain));
    release(chain);
    if (FAILED(hr)) return hr;
    p->factory->MakeWindowAssociation(window, DXGI_MWA_NO_ALT_ENTER);
    hr = p->device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&p->complete));
    if (FAILED(hr)) return hr;
    p->event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!p->event) return HRESULT_FROM_WIN32(GetLastError());
    for (auto*& allocator : p->allocators) {
        hr = p->device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator));
        if (FAILED(hr)) return hr;
    }
    hr = p->device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, p->allocators[0], nullptr, IID_PPV_ARGS(&p->commands));
    if (FAILED(hr)) return hr;
    hr = p->commands->Close();
    if (FAILED(hr)) return hr;
    p->width = width; p->height = height;
    *output = p.release();
    return 0;
}
extern "C" __declspec(dllexport) int AuxCreateSharedImage(Presenter* p, uint32_t width,
    uint32_t height, uint32_t format, HANDLE* handle, ID3D12Resource** resource) {
    if (!p || !width || !height || format != 37 || !handle || !resource) return E_INVALIDARG;
    *handle = nullptr; *resource = nullptr;
    D3D12_HEAP_PROPERTIES heap{}; heap.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_DESC desc{};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    desc.Width = width; desc.Height = height; desc.DepthOrArraySize = 1; desc.MipLevels = 1;
    desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM; desc.SampleDesc.Count = 1;
    HRESULT hr = p->device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_SHARED,
        &desc, D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(resource));
    if (FAILED(hr)) return hr;
    hr = p->device->CreateSharedHandle(*resource, nullptr, GENERIC_ALL, nullptr, handle);
    if (FAILED(hr)) release(*resource);
    return hr;
}
extern "C" __declspec(dllexport) void AuxReleaseImage(ID3D12Resource* resource) { release(resource); }
extern "C" __declspec(dllexport) int AuxCreateSharedFence(Presenter* p, HANDLE* handle) {
    if (!p || !handle) return E_INVALIDARG;
    *handle = nullptr;
    if (!p->shared) {
        HRESULT hr = p->device->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&p->shared));
        if (FAILED(hr)) return hr;
    }
    return p->device->CreateSharedHandle(p->shared, nullptr, GENERIC_ALL, nullptr, handle);
}
static D3D12_RESOURCE_BARRIER transition(ID3D12Resource* image,
    D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after) {
    D3D12_RESOURCE_BARRIER b{};
    b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    b.Transition.pResource = image;
    b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    b.Transition.StateBefore = before; b.Transition.StateAfter = after;
    return b;
}
extern "C" __declspec(dllexport) int AuxPresent(Presenter* p, ID3D12Resource* source,
    uint32_t width, uint32_t height, uint32_t vsync, uint64_t ready, uint64_t done) {
    if (!p || !source || !p->shared || !ready || done <= ready) return E_INVALIDARG;
    if (width != p->width || height != p->height) {
        HRESULT hr = wait(p, p->submitted);
        if (FAILED(hr)) return hr;
        hr = p->swapchain->ResizeBuffers(3, width, height, DXGI_FORMAT_R8G8B8A8_UNORM, 0);
        if (FAILED(hr)) return hr;
        p->width = width; p->height = height;
    }
    UINT slot = p->swapchain->GetCurrentBackBufferIndex();
    HRESULT hr = wait(p, p->slots[slot]);
    if (FAILED(hr)) return hr;
    hr = p->queue->Wait(p->shared, ready);
    if (FAILED(hr)) return hr;
    hr = p->allocators[slot]->Reset();
    if (FAILED(hr)) return hr;
    hr = p->commands->Reset(p->allocators[slot], nullptr);
    if (FAILED(hr)) return hr;
    ID3D12Resource* destination{};
    hr = p->swapchain->GetBuffer(slot, IID_PPV_ARGS(&destination));
    if (FAILED(hr)) return hr;
    D3D12_RESOURCE_BARRIER before[] = {
        transition(source, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_SOURCE),
        transition(destination, D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_COPY_DEST)};
    p->commands->ResourceBarrier(2, before);
    p->commands->CopyResource(destination, source);
    D3D12_RESOURCE_BARRIER after[] = {
        transition(source, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_COMMON),
        transition(destination, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_PRESENT)};
    p->commands->ResourceBarrier(2, after);
    release(destination);
    hr = p->commands->Close();
    if (FAILED(hr)) return hr;
    ID3D12CommandList* lists[]{p->commands};
    p->queue->ExecuteCommandLists(1, lists);
    hr = p->queue->Signal(p->shared, done);
    if (FAILED(hr)) return hr;
    HRESULT presented = p->swapchain->Present(vsync ? 1 : 0, 0);
    uint64_t serial = p->submitted + 1;
    hr = p->queue->Signal(p->complete, serial);
    if (FAILED(hr)) return hr;
    p->submitted = p->slots[slot] = serial;
    return FAILED(presented) ? presented : 0;
}
