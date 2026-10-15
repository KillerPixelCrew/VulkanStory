#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <vulkan/vulkan.h>
#include <vulkan/vk_layer.h>
#include <cstring>
#include <memory>
#include <mutex>
#include <unordered_map>
#include <vector>

// The final signal must follow work appended by an overlay's QueuePresent hook.
// This layer sits inside that hook and outside validation/driver presentation.
// Image-owned binary semaphores are retired by the real WSI present fences.
namespace {
constexpr char name[] = "VK_LAYER_VULKANSTORY_present";
template<class T> void* key(T handle) { return *reinterpret_cast<void**>(handle); }
struct InstanceData { VkInstance instance; PFN_vkGetInstanceProcAddr next; PFN_GetPhysicalDeviceProcAddr physical; };
struct ImageSync { VkSemaphore semaphore{}; VkFence fence{}; VkFence ownedFence{}; bool pending{}; };
struct DeviceData {
    VkDevice device{};
    PFN_vkGetDeviceProcAddr next{};
    PFN_vkQueueSubmit submit{};
    PFN_vkQueuePresentKHR present{};
    PFN_vkCreateSemaphore createSemaphore{};
    PFN_vkDestroySemaphore destroySemaphore{};
    PFN_vkGetSwapchainImagesKHR getImages{};
    PFN_vkWaitForFences waitFences{};
    PFN_vkGetFenceStatus fenceStatus{};
    PFN_vkDestroyFence destroyFence{};
    PFN_vkCreateFence createFence{};
    PFN_vkResetFences resetFences{};
    PFN_vkDestroySwapchainKHR destroySwapchain{};
    PFN_vkDestroyDevice destroyDevice{};
    PFN_vkDeviceWaitIdle waitIdle{};
    PFN_vkSetLatencyMarkerNV marker{};
    PFN_vkLatencySleepNV latencySleep{};
    PFN_vkSetLatencySleepModeNV sleepMode{};
    PFN_vkGetLatencyTimingsNV timings{};
    bool maintenance1{};
    std::mutex mutex;
    std::unordered_map<VkSwapchainKHR, std::vector<ImageSync>> chains;
    // Called before an application resets/destroys a borrowed present fence.
    // Its completion proof remains valid after the fence itself is gone.
    void finishFence(VkFence fence) {
        bool needed = false;
        for (auto& [chain, images] : chains)
            for (auto& image : images) needed |= image.pending && image.fence == fence;
        if (!needed) return;
        VkResult result = fenceStatus(device, fence);
        if (result == VK_NOT_READY) result = waitFences(device, 1, &fence, VK_TRUE, UINT64_MAX);
        if (result == VK_SUCCESS || result == VK_ERROR_DEVICE_LOST)
            for (auto& [chain, images] : chains)
                for (auto& image : images)
                    if (image.fence == fence) { image.pending = false; image.fence = VK_NULL_HANDLE; }
    }
    void releaseChain(VkSwapchainKHR chain) {
        auto found = chains.find(chain);
        if (found == chains.end()) return;
        for (auto& image : found->second) if (image.pending) finishFence(image.fence);
        for (auto& image : found->second) {
            if (image.semaphore) destroySemaphore(device, image.semaphore, nullptr);
            if (image.ownedFence) destroyFence(device, image.ownedFence, nullptr);
        }
        chains.erase(found);
    }
};
std::mutex registryMutex;
std::unordered_map<void*, InstanceData> instances;
std::unordered_map<void*, std::shared_ptr<DeviceData>> devices;
template<class T> std::shared_ptr<DeviceData> data(T handle) {
    std::lock_guard lock(registryMutex);
    auto found = devices.find(key(handle));
    return found == devices.end() ? nullptr : found->second;
}
InstanceData instanceData(VkInstance instance) {
    std::lock_guard lock(registryMutex);
    auto found = instances.find(key(instance));
    return found == instances.end() ? InstanceData{} : found->second;
}
VkLayerInstanceCreateInfo* instanceLink(const VkInstanceCreateInfo* info) {
    for (auto p = (VkBaseInStructure*)info->pNext; p; p = (VkBaseInStructure*)p->pNext)
        if (p->sType == VK_STRUCTURE_TYPE_LOADER_INSTANCE_CREATE_INFO) {
            auto link = (VkLayerInstanceCreateInfo*)p;
            if (link->function == VK_LAYER_LINK_INFO) return link;
        }
    return nullptr;
}
VkLayerDeviceCreateInfo* deviceLink(const VkDeviceCreateInfo* info) {
    for (auto p = (VkBaseInStructure*)info->pNext; p; p = (VkBaseInStructure*)p->pNext)
        if (p->sType == VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO) {
            auto link = (VkLayerDeviceCreateInfo*)p;
            if (link->function == VK_LAYER_LINK_INFO) return link;
        }
    return nullptr;
}
VkResult VKAPI_CALL CreateInstance(const VkInstanceCreateInfo* info, const VkAllocationCallbacks* allocator, VkInstance* output) {
    auto link = instanceLink(info);
    if (!link || !link->u.pLayerInfo) return VK_ERROR_INITIALIZATION_FAILED;
    auto next = link->u.pLayerInfo->pfnNextGetInstanceProcAddr;
    auto physical = link->u.pLayerInfo->pfnNextGetPhysicalDeviceProcAddr;
    link->u.pLayerInfo = link->u.pLayerInfo->pNext;
    auto create = (PFN_vkCreateInstance)next(VK_NULL_HANDLE, "vkCreateInstance");
    VkResult result = create(info, allocator, output);
    if (result == VK_SUCCESS) {
        std::lock_guard lock(registryMutex);
        instances[key(*output)] = {*output, next, physical};
    }
    return result;
}
void VKAPI_CALL DestroyInstance(VkInstance instance, const VkAllocationCallbacks* allocator) {
    auto entry = instanceData(instance);
    auto destroy = (PFN_vkDestroyInstance)entry.next(instance, "vkDestroyInstance");
    { std::lock_guard lock(registryMutex); instances.erase(key(instance)); }
    destroy(instance, allocator);
}
VkResult VKAPI_CALL CreateDevice(VkPhysicalDevice physical, const VkDeviceCreateInfo* info,
    const VkAllocationCallbacks* allocator, VkDevice* output) {
    InstanceData parent{};
    { std::lock_guard lock(registryMutex); parent = instances.at(key(physical)); }
    auto link = deviceLink(info);
    if (!link || !link->u.pLayerInfo) return VK_ERROR_INITIALIZATION_FAILED;
    auto nextInstance = link->u.pLayerInfo->pfnNextGetInstanceProcAddr;
    auto next = link->u.pLayerInfo->pfnNextGetDeviceProcAddr;
    link->u.pLayerInfo = link->u.pLayerInfo->pNext;
    auto create = (PFN_vkCreateDevice)nextInstance(parent.instance, "vkCreateDevice");
    VkResult result = create(physical, info, allocator, output);
    if (result != VK_SUCCESS) return result;
    auto d = std::make_shared<DeviceData>();
    d->device = *output; d->next = next;
    for (auto p = (const VkBaseInStructure*)info->pNext; p; p = p->pNext)
        if (p->sType == VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SWAPCHAIN_MAINTENANCE_1_FEATURES_KHR)
            d->maintenance1 = ((const VkPhysicalDeviceSwapchainMaintenance1FeaturesKHR*)p)->swapchainMaintenance1 == VK_TRUE;
#define LOAD(member, type, symbol) d->member = (type)next(*output, symbol)
    LOAD(submit, PFN_vkQueueSubmit, "vkQueueSubmit");
    LOAD(present, PFN_vkQueuePresentKHR, "vkQueuePresentKHR");
    LOAD(createSemaphore, PFN_vkCreateSemaphore, "vkCreateSemaphore");
    LOAD(destroySemaphore, PFN_vkDestroySemaphore, "vkDestroySemaphore");
    LOAD(getImages, PFN_vkGetSwapchainImagesKHR, "vkGetSwapchainImagesKHR");
    LOAD(waitFences, PFN_vkWaitForFences, "vkWaitForFences");
    LOAD(fenceStatus, PFN_vkGetFenceStatus, "vkGetFenceStatus");
    LOAD(destroyFence, PFN_vkDestroyFence, "vkDestroyFence");
    LOAD(createFence, PFN_vkCreateFence, "vkCreateFence");
    LOAD(resetFences, PFN_vkResetFences, "vkResetFences");
    LOAD(destroySwapchain, PFN_vkDestroySwapchainKHR, "vkDestroySwapchainKHR");
    LOAD(destroyDevice, PFN_vkDestroyDevice, "vkDestroyDevice");
    LOAD(waitIdle, PFN_vkDeviceWaitIdle, "vkDeviceWaitIdle");
    LOAD(marker, PFN_vkSetLatencyMarkerNV, "vkSetLatencyMarkerNV");
    LOAD(latencySleep, PFN_vkLatencySleepNV, "vkLatencySleepNV");
    LOAD(sleepMode, PFN_vkSetLatencySleepModeNV, "vkSetLatencySleepModeNV");
    LOAD(timings, PFN_vkGetLatencyTimingsNV, "vkGetLatencyTimingsNV");
#undef LOAD
    { std::lock_guard lock(registryMutex); devices[key(*output)] = std::move(d); }
    return VK_SUCCESS;
}
void VKAPI_CALL DestroyDevice(VkDevice device, const VkAllocationCallbacks* allocator) {
    auto d = data(device);
    {
        std::lock_guard lock(d->mutex);
        d->waitIdle(device);
        while (!d->chains.empty()) d->releaseChain(d->chains.begin()->first);
    }
    { std::lock_guard lock(registryMutex); devices.erase(key(device)); }
    d->destroyDevice(device, allocator);
}
void VKAPI_CALL DestroySwapchain(VkDevice device, VkSwapchainKHR chain, const VkAllocationCallbacks* allocator) {
    auto d = data(device);
    std::lock_guard lock(d->mutex);
    d->releaseChain(chain);
    d->destroySwapchain(device, chain, allocator);
}
void VKAPI_CALL DestroyFence(VkDevice device, VkFence fence, const VkAllocationCallbacks* allocator) {
    auto d = data(device);
    std::lock_guard lock(d->mutex);
    d->finishFence(fence);
    d->destroyFence(device, fence, allocator);
}
VkResult VKAPI_CALL ResetFences(VkDevice device, uint32_t count, const VkFence* fences) {
    auto d = data(device);
    std::lock_guard lock(d->mutex);
    for (uint32_t i = 0; i < count; ++i) d->finishFence(fences[i]);
    return d->resetFences(device, count, fences);
}
void VKAPI_CALL Marker(VkDevice device, VkSwapchainKHR chain, const VkSetLatencyMarkerInfoNV* info) {
    auto d = data(device);
    std::lock_guard lock(d->mutex);
    d->marker(device, chain, info);
}
VkResult VKAPI_CALL LatencySleep(VkDevice device, VkSwapchainKHR chain, const VkLatencySleepInfoNV* info) {
    auto d = data(device);
    std::lock_guard lock(d->mutex);
    // This schedules the latency semaphore signal and returns immediately.
    // The SDK's vkWaitSemaphores happens outside this host-access guard.
    return d->latencySleep(device, chain, info);
}
VkResult VKAPI_CALL SleepMode(VkDevice device, VkSwapchainKHR chain, const VkLatencySleepModeInfoNV* info) {
    auto d = data(device);
    std::lock_guard lock(d->mutex);
    return d->sleepMode(device, chain, info);
}
void VKAPI_CALL Timings(VkDevice device, VkSwapchainKHR chain, VkGetLatencyMarkerInfoNV* info) {
    auto d = data(device);
    std::lock_guard lock(d->mutex);
    d->timings(device, chain, info);
}
PFN_vkVoidFunction latencyFunction(const char* function) {
    if (!std::strcmp(function, "vkSetLatencyMarkerNV")) return (PFN_vkVoidFunction)Marker;
    if (!std::strcmp(function, "vkLatencySleepNV")) return (PFN_vkVoidFunction)LatencySleep;
    if (!std::strcmp(function, "vkSetLatencySleepModeNV")) return (PFN_vkVoidFunction)SleepMode;
    if (!std::strcmp(function, "vkGetLatencyTimingsNV")) return (PFN_vkVoidFunction)Timings;
    return nullptr;
}
VkResult VKAPI_CALL Present(VkQueue queue, const VkPresentInfoKHR* info) {
    auto d = data(queue);
    std::lock_guard lock(d->mutex);
    auto fences = (const VkSwapchainPresentFenceInfoKHR*)nullptr;
    for (auto p = (const VkBaseInStructure*)info->pNext; p; p = p->pNext)
        if (p->sType == VK_STRUCTURE_TYPE_SWAPCHAIN_PRESENT_FENCE_INFO_KHR)
            fences = (const VkSwapchainPresentFenceInfoKHR*)p;
    // Do not create a synchronization owner without a real WSI retirement proof.
    // VulkanStory supplies these for both native and SDK-owned real swapchains.
    if (!fences && !d->maintenance1) return d->present(queue, info);
    if (fences) {
        if (fences->swapchainCount != info->swapchainCount) return d->present(queue, info);
        for (uint32_t i = 0; i < info->swapchainCount; ++i)
            if (!fences->pFences[i]) return d->present(queue, info);
    }
    std::vector<VkSemaphore> signals;
    std::vector<VkFence> ownedFences;
    std::vector<ImageSync*> owners;
    signals.reserve(info->swapchainCount); owners.reserve(info->swapchainCount);
    for (uint32_t i = 0; i < info->swapchainCount; ++i) {
        auto chain = info->pSwapchains[i];
        auto found = d->chains.find(chain);
        if (found == d->chains.end()) {
            uint32_t count = 0;
            VkResult result = d->getImages(d->device, chain, &count, nullptr);
            if (result != VK_SUCCESS) return result;
            found = d->chains.emplace(chain, std::vector<ImageSync>(count)).first;
        }
        if (info->pImageIndices[i] >= found->second.size()) return VK_ERROR_INITIALIZATION_FAILED;
        auto& image = found->second[info->pImageIndices[i]];
        if (image.pending) d->finishFence(image.fence);
        if (!image.semaphore) {
            VkSemaphoreCreateInfo create{VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO};
            VkResult result = d->createSemaphore(d->device, &create, nullptr, &image.semaphore);
            if (result != VK_SUCCESS) return result;
        }
        if (!fences) {
            if (!image.ownedFence) {
                VkFenceCreateInfo create{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
                VkResult result = d->createFence(d->device, &create, nullptr, &image.ownedFence);
                if (result != VK_SUCCESS) return result;
            }
            VkResult result = d->resetFences(d->device, 1, &image.ownedFence);
            if (result != VK_SUCCESS) return result;
            ownedFences.push_back(image.ownedFence);
        }
        signals.push_back(image.semaphore); owners.push_back(&image);
    }
    std::vector<VkPipelineStageFlags> stages(info->waitSemaphoreCount, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT);
    VkSubmitInfo submit{VK_STRUCTURE_TYPE_SUBMIT_INFO};
    submit.waitSemaphoreCount = info->waitSemaphoreCount;
    submit.pWaitSemaphores = info->pWaitSemaphores;
    submit.pWaitDstStageMask = stages.data();
    submit.signalSemaphoreCount = static_cast<uint32_t>(signals.size());
    submit.pSignalSemaphores = signals.data();
    VkResult result = d->submit(queue, 1, &submit, VK_NULL_HANDLE);
    if (result != VK_SUCCESS) return result;
    VkPresentInfoKHR final = *info;
    VkSwapchainPresentFenceInfoKHR privateFences{VK_STRUCTURE_TYPE_SWAPCHAIN_PRESENT_FENCE_INFO_KHR,
        info->pNext, info->swapchainCount, ownedFences.data()};
    if (!fences) { final.pNext = &privateFences; fences = &privateFences; }
    final.waitSemaphoreCount = static_cast<uint32_t>(signals.size());
    final.pWaitSemaphores = signals.data();
    result = d->present(queue, &final);
    if (result == VK_SUCCESS || result == VK_SUBOPTIMAL_KHR ||
        result == VK_ERROR_OUT_OF_DATE_KHR || result == VK_ERROR_SURFACE_LOST_KHR)
        for (uint32_t i = 0; i < info->swapchainCount; ++i) {
            owners[i]->fence = fences->pFences[i]; owners[i]->pending = true;
        }
    return result;
}
VkResult VKAPI_CALL EnumerateInstanceExtensions(const char* layer, uint32_t* count, VkExtensionProperties*) {
    if (layer && std::strcmp(layer, name)) return VK_ERROR_LAYER_NOT_PRESENT;
    *count = 0; return VK_SUCCESS;
}
VkResult VKAPI_CALL EnumerateDeviceExtensions(VkPhysicalDevice physical, const char* layer,
    uint32_t* count, VkExtensionProperties* properties) {
    if (layer && !std::strcmp(layer, name)) { *count = 0; return VK_SUCCESS; }
    InstanceData parent{};
    { std::lock_guard lock(registryMutex); parent = instances.at(key(physical)); }
    auto next = (PFN_vkEnumerateDeviceExtensionProperties)parent.next(parent.instance, "vkEnumerateDeviceExtensionProperties");
    return next(physical, layer, count, properties);
}
PFN_vkVoidFunction local(const char* function) {
#define ENTRY(symbol, target) if (!std::strcmp(function, symbol)) return (PFN_vkVoidFunction)target
    ENTRY("vkCreateInstance", CreateInstance); ENTRY("vkDestroyInstance", DestroyInstance);
    ENTRY("vkCreateDevice", CreateDevice); ENTRY("vkDestroyDevice", DestroyDevice);
    ENTRY("vkQueuePresentKHR", Present); ENTRY("vkDestroySwapchainKHR", DestroySwapchain);
    ENTRY("vkResetFences", ResetFences); ENTRY("vkDestroyFence", DestroyFence);
    ENTRY("vkEnumerateInstanceExtensionProperties", EnumerateInstanceExtensions);
    ENTRY("vkEnumerateDeviceExtensionProperties", EnumerateDeviceExtensions);
#undef ENTRY
    return nullptr;
}
}
extern "C" __declspec(dllexport) PFN_vkVoidFunction VKAPI_CALL WsiGetDeviceProcAddr(VkDevice device, const char* function) {
    if (!std::strcmp(function, "vkGetDeviceProcAddr")) return (PFN_vkVoidFunction)WsiGetDeviceProcAddr;
    auto d = data(device);
    if (!d) return nullptr;
    auto next = d->next(device, function);
    if (!next) return nullptr;
    if (auto latency = latencyFunction(function)) return latency;
    auto replacement = local(function);
    return replacement ? replacement : next;
}
extern "C" __declspec(dllexport) PFN_vkVoidFunction VKAPI_CALL WsiGetInstanceProcAddr(VkInstance instance, const char* function) {
    if (!std::strcmp(function, "vkGetInstanceProcAddr")) return (PFN_vkVoidFunction)WsiGetInstanceProcAddr;
    if (!std::strcmp(function, "vkGetDeviceProcAddr")) return (PFN_vkVoidFunction)WsiGetDeviceProcAddr;
    auto replacement = local(function);
    if (replacement) return replacement;
    if (!instance) return nullptr;
    auto parent = instanceData(instance);
    auto next = parent.next ? parent.next(instance, function) : nullptr;
    auto latency = latencyFunction(function);
    return next && latency ? latency : next;
}
extern "C" __declspec(dllexport) PFN_vkVoidFunction VKAPI_CALL WsiGetPhysicalDeviceProcAddr(VkInstance instance, const char* function) {
    auto parent = instanceData(instance);
    return parent.physical ? parent.physical(instance, function) : parent.next(instance, function);
}
extern "C" __declspec(dllexport) VkResult VKAPI_CALL vkNegotiateLoaderLayerInterfaceVersion(VkNegotiateLayerInterface* version) {
    if (version->loaderLayerInterfaceVersion < 2) return VK_ERROR_INITIALIZATION_FAILED;
    version->loaderLayerInterfaceVersion = 2;
    version->pfnGetInstanceProcAddr = WsiGetInstanceProcAddr;
    version->pfnGetDeviceProcAddr = WsiGetDeviceProcAddr;
    version->pfnGetPhysicalDeviceProcAddr = WsiGetPhysicalDeviceProcAddr;
    return VK_SUCCESS;
}
