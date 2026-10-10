// VulkanStory VMA bridge. VMA is included unmodified from sdk/vma (MIT).
// No Vulkan loader is linked: every call uses the renderer's current dispatch.
#define VMA_STATIC_VULKAN_FUNCTIONS 0
#define VMA_DYNAMIC_VULKAN_FUNCTIONS 1
#define VMA_IMPLEMENTATION
#include <vk_mem_alloc.h>
#include "vulkanstory_vma.h"
#include <cstring>
#include <mutex>
#include <new>

static_assert(sizeof(void*) == 8, "The VMA bridge supports x64 only.");
static_assert(sizeof(VsVmaCreateInfo) == 56 && sizeof(VsVmaAllocateInfo) == 64);
static_assert(sizeof(VsVmaAllocationInfo) == 64 && sizeof(VsVmaHeapStats) == 64);
static_assert(sizeof(VsVmaClassStats) == 48);

namespace {
struct Allocator {
    VmaAllocator vma = nullptr;
    PFN_vkAllocateMemory allocateMemory = nullptr;
    PFN_vkFreeMemory freeMemory = nullptr;
    PFN_vkGetBufferMemoryRequirements bufferRequirements = nullptr;
    PFN_vkGetImageMemoryRequirements imageRequirements = nullptr;
    VkDevice device = VK_NULL_HANDLE;
    const VkPhysicalDeviceMemoryProperties* memoryProperties = nullptr;
    VmaPool pools[VS_VMA_CLASS_COUNT][VK_MAX_MEMORY_TYPES] = {};
    uint64_t dedicatedBytes[VS_VMA_CLASS_COUNT] = {};
    uint64_t dedicatedCount[VS_VMA_CLASS_COUNT] = {};
    std::mutex mutex;
};

// VMA may reduce a preferred block size after a rejected physical allocation.
// Enforce the renderer's reserve/ReBAR limit before Vulkan sees any such request.
struct GrowthScope;
thread_local GrowthScope* currentGrowth = nullptr;
struct GrowthScope {
    Allocator& allocator;
    uint64_t remaining;
    uint64_t heapRemaining[VK_MAX_MEMORY_HEAPS] = {};
    VkDeviceMemory createdMemory = VK_NULL_HANDLE;
    uint64_t createdBytes = 0;
    uint32_t createdHeap = 0;
    GrowthScope* previous;
    GrowthScope(Allocator& owner, uint64_t limit)
        : allocator(owner), remaining(limit), previous(currentGrowth) {
        VmaBudget budgets[VK_MAX_MEMORY_HEAPS] = {};
        vmaGetHeapBudgets(owner.vma, budgets);
        for (uint32_t heap = 0; heap < owner.memoryProperties->memoryHeapCount; ++heap)
            heapRemaining[heap] = budgets[heap].budget > budgets[heap].usage
                ? budgets[heap].budget - budgets[heap].usage : 0;
        currentGrowth = this;
    }
    ~GrowthScope() { currentGrowth = previous; }
};

VKAPI_ATTR VkResult VKAPI_CALL AllocateMemory(VkDevice device, const VkMemoryAllocateInfo* info,
    const VkAllocationCallbacks* callbacks, VkDeviceMemory* memory) {
    if (!currentGrowth || info->allocationSize > currentGrowth->remaining) {
        *memory = VK_NULL_HANDLE;
        return VK_ERROR_OUT_OF_DEVICE_MEMORY;
    }
    uint32_t heap = currentGrowth->allocator.memoryProperties->memoryTypes[info->memoryTypeIndex].heapIndex;
    if (info->allocationSize > currentGrowth->heapRemaining[heap]) {
        *memory = VK_NULL_HANDLE;
        return VK_ERROR_OUT_OF_DEVICE_MEMORY;
    }
    VkResult result = currentGrowth->allocator.allocateMemory(device, info, callbacks, memory);
    if (result == VK_SUCCESS) {
        currentGrowth->heapRemaining[heap] -= info->allocationSize;
        if (currentGrowth->remaining != UINT64_MAX) currentGrowth->remaining -= info->allocationSize;
        currentGrowth->createdMemory = *memory;
        currentGrowth->createdBytes = info->allocationSize;
        currentGrowth->createdHeap = heap;
    }
    return result;
}

VKAPI_ATTR void VKAPI_CALL FreeMemory(VkDevice device, VkDeviceMemory memory, const VkAllocationCallbacks* callbacks) {
    // Every VMA allocation/free/destroy entry is scoped to its owning dispatcher.
    // Restore headroom when VMA discards a new block after a mapping failure;
    // alternate compatible memory types must retain VMA's normal retry semantics.
    if (memory == currentGrowth->createdMemory && memory != VK_NULL_HANDLE) {
        currentGrowth->heapRemaining[currentGrowth->createdHeap] += currentGrowth->createdBytes;
        if (currentGrowth->remaining != UINT64_MAX) currentGrowth->remaining += currentGrowth->createdBytes;
        currentGrowth->createdMemory = VK_NULL_HANDLE;
        currentGrowth->createdBytes = 0;
    }
    currentGrowth->allocator.freeMemory(device, memory, callbacks);
}

Allocator* Owner(uint64_t handle) { return reinterpret_cast<Allocator*>(static_cast<uintptr_t>(handle)); }
VmaAllocation Allocation(uint64_t handle) { return reinterpret_cast<VmaAllocation>(static_cast<uintptr_t>(handle)); }
uint32_t Purpose(const VmaAllocationInfo& info) {
    return static_cast<uint32_t>(reinterpret_cast<uintptr_t>(info.pUserData) - 1);
}
}

int32_t vs_vma_create(const VsVmaCreateInfo* info, uint64_t* handle) {
    if (!handle) return VK_ERROR_INITIALIZATION_FAILED;
    *handle = 0;
    if (!info || info->abiVersion != VS_VMA_ABI_VERSION || !info->instance || !info->physicalDevice ||
        !info->device || !info->getInstanceProcAddr || !info->getDeviceProcAddr ||
        (info->flags & ~(VS_VMA_MEMORY_BUDGET | VS_VMA_BUFFER_DEVICE_ADDRESS))) return VK_ERROR_INITIALIZATION_FAILED;
    auto* owner = new (std::nothrow) Allocator;
    if (!owner) return VK_ERROR_OUT_OF_HOST_MEMORY;
    VmaVulkanFunctions functions = {};
    functions.vkGetInstanceProcAddr = reinterpret_cast<PFN_vkGetInstanceProcAddr>(static_cast<uintptr_t>(info->getInstanceProcAddr));
    functions.vkGetDeviceProcAddr = reinterpret_cast<PFN_vkGetDeviceProcAddr>(static_cast<uintptr_t>(info->getDeviceProcAddr));
    VkDevice device = reinterpret_cast<VkDevice>(static_cast<uintptr_t>(info->device));
    owner->device = device;
    owner->allocateMemory = reinterpret_cast<PFN_vkAllocateMemory>(functions.vkGetDeviceProcAddr(device, "vkAllocateMemory"));
    owner->freeMemory = reinterpret_cast<PFN_vkFreeMemory>(functions.vkGetDeviceProcAddr(device, "vkFreeMemory"));
    owner->bufferRequirements = reinterpret_cast<PFN_vkGetBufferMemoryRequirements>(functions.vkGetDeviceProcAddr(device, "vkGetBufferMemoryRequirements"));
    owner->imageRequirements = reinterpret_cast<PFN_vkGetImageMemoryRequirements>(functions.vkGetDeviceProcAddr(device, "vkGetImageMemoryRequirements"));
    if (!owner->allocateMemory || !owner->freeMemory || !owner->bufferRequirements || !owner->imageRequirements) {
        delete owner;
        return VK_ERROR_INITIALIZATION_FAILED;
    }
    functions.vkAllocateMemory = AllocateMemory;
    functions.vkFreeMemory = FreeMemory;
    VmaAllocatorCreateInfo create = {};
    create.instance = reinterpret_cast<VkInstance>(static_cast<uintptr_t>(info->instance));
    create.physicalDevice = reinterpret_cast<VkPhysicalDevice>(static_cast<uintptr_t>(info->physicalDevice));
    create.device = device;
    create.vulkanApiVersion = info->apiVersion;
    create.pVulkanFunctions = &functions;
    create.preferredLargeHeapBlockSize = 64ULL * 1024 * 1024;
    if (info->flags & VS_VMA_MEMORY_BUDGET) create.flags |= VMA_ALLOCATOR_CREATE_EXT_MEMORY_BUDGET_BIT;
    if (info->flags & VS_VMA_BUFFER_DEVICE_ADDRESS) create.flags |= VMA_ALLOCATOR_CREATE_BUFFER_DEVICE_ADDRESS_BIT;
    VkResult result = vmaCreateAllocator(&create, &owner->vma);
    if (result != VK_SUCCESS) { delete owner; return result; }
    vmaGetMemoryProperties(owner->vma, &owner->memoryProperties);
    *handle = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(owner));
    return VK_SUCCESS;
}

void vs_vma_destroy(uint64_t handle) {
    auto* owner = Owner(handle);
    if (!owner) return;
    // The renderer drains GPU work and releases every lease before this call.
    {
        GrowthScope growth(*owner, UINT64_MAX);
        for (auto& purposePools : owner->pools)
            for (VmaPool pool : purposePools)
                if (pool) vmaDestroyPool(owner->vma, pool);
        vmaDestroyAllocator(owner->vma);
    }
    delete owner;
}

int32_t vs_vma_allocate(uint64_t handle, const VsVmaAllocateInfo* info, VsVmaAllocationInfo* output) {
    if (!output) return VK_ERROR_INITIALIZATION_FAILED;
    std::memset(output, 0, sizeof(*output));
    auto* owner = Owner(handle);
    if (!owner || !info || !info->size || !info->alignment || (info->alignment & (info->alignment - 1)) ||
        !info->memoryTypeBits || info->poolClass >= VS_VMA_CLASS_COUNT || (info->buffer && info->image) ||
        (info->flags & ~(VS_VMA_MAPPED | VS_VMA_DEDICATED | VS_VMA_CAN_ALIAS | VS_VMA_NEVER_ALLOCATE | VS_VMA_HOST_RANDOM_ACCESS)) ||
        ((info->flags & VS_VMA_DEDICATED) && (info->flags & VS_VMA_NEVER_ALLOCATE))) return VK_ERROR_INITIALIZATION_FAILED;
    std::lock_guard<std::mutex> guard(owner->mutex);
    GrowthScope growth(*owner, info->growthLimitBytes);
    VmaAllocationCreateInfo create = {};
    create.flags = VMA_ALLOCATION_CREATE_WITHIN_BUDGET_BIT;
    if (info->flags & VS_VMA_MAPPED) {
        create.flags |= VMA_ALLOCATION_CREATE_MAPPED_BIT;
        create.requiredFlags |= VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT;
        create.flags |= (info->flags & VS_VMA_HOST_RANDOM_ACCESS)
            ? VMA_ALLOCATION_CREATE_HOST_ACCESS_RANDOM_BIT : VMA_ALLOCATION_CREATE_HOST_ACCESS_SEQUENTIAL_WRITE_BIT;
    }
    if (info->flags & VS_VMA_DEDICATED) create.flags |= VMA_ALLOCATION_CREATE_DEDICATED_MEMORY_BIT;
    if (info->flags & VS_VMA_CAN_ALIAS) create.flags |= VMA_ALLOCATION_CREATE_CAN_ALIAS_BIT;
    if (info->flags & VS_VMA_NEVER_ALLOCATE) create.flags |= VMA_ALLOCATION_CREATE_NEVER_ALLOCATE_BIT;
    create.requiredFlags |= info->requiredFlags;
    create.preferredFlags = info->preferredFlags;
    create.memoryTypeBits = info->memoryTypeBits;
    create.minAlignment = info->alignment;
    create.pUserData = reinterpret_cast<void*>(static_cast<uintptr_t>(info->poolClass + 1));
    VkMemoryRequirements requirements = { info->size, info->alignment, info->memoryTypeBits };
    if (info->buffer)
        owner->bufferRequirements(owner->device, reinterpret_cast<VkBuffer>(static_cast<uintptr_t>(info->buffer)), &requirements);
    else if (info->image)
        owner->imageRequirements(owner->device, reinterpret_cast<VkImage>(static_cast<uintptr_t>(info->image)), &requirements);
    uint32_t allowedTypes = info->memoryTypeBits & requirements.memoryTypeBits;
    VmaAllocation allocation = nullptr;
    VkResult result = VK_ERROR_FEATURE_NOT_PRESENT;
    while (allowedTypes) {
        create.pool = nullptr;
        create.memoryTypeBits = allowedTypes;
        uint32_t typeIndex;
        VkResult selection = vmaFindMemoryTypeIndex(owner->vma, allowedTypes, &create, &typeIndex);
        if (selection != VK_SUCCESS) break;
        VmaPool& pool = owner->pools[info->poolClass][typeIndex];
        if (!pool) {
            VmaPoolCreateInfo poolInfo = {};
            poolInfo.memoryTypeIndex = typeIndex;
            // blockSize=0 keeps VMA's adaptive sizing and driver-required/preferred
            // dedicated allocations. minBlockCount=0 allows VMA to release blocks.
            result = vmaCreatePool(owner->vma, &poolInfo, &pool);
            if (result != VK_SUCCESS) return result;
        }
        create.pool = pool;
        if (info->buffer)
            result = vmaAllocateMemoryForBuffer(owner->vma, reinterpret_cast<VkBuffer>(static_cast<uintptr_t>(info->buffer)), &create, &allocation, nullptr);
        else if (info->image)
            result = vmaAllocateMemoryForImage(owner->vma, reinterpret_cast<VkImage>(static_cast<uintptr_t>(info->image)), &create, &allocation, nullptr);
        else
            result = vmaAllocateMemory(owner->vma, &requirements, &create, &allocation, nullptr);
        if (result == VK_SUCCESS) break;
        if (result != VK_ERROR_OUT_OF_DEVICE_MEMORY && result != VK_ERROR_OUT_OF_HOST_MEMORY) return result;
        allowedTypes &= ~(1U << typeIndex);
    }
    if (result != VK_SUCCESS) return result;
    VmaAllocationInfo2 actual = {};
    vmaGetAllocationInfo2(owner->vma, allocation, &actual);
    const auto& memory = actual.allocationInfo;
    const auto& type = owner->memoryProperties->memoryTypes[memory.memoryType];
    output->allocation = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(allocation));
    output->memory = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(memory.deviceMemory));
    output->offset = memory.offset;
    output->size = memory.size;
    output->mapped = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(memory.pMappedData));
    output->memoryTypeIndex = memory.memoryType;
    output->heapIndex = type.heapIndex;
    output->memoryProperties = type.propertyFlags;
    output->poolClass = info->poolClass;
    if (memory.pMappedData) output->flags |= VS_VMA_MAPPED;
    if (actual.dedicatedMemory) {
        output->flags |= VS_VMA_DEDICATED;
        owner->dedicatedBytes[info->poolClass] += memory.size;
        ++owner->dedicatedCount[info->poolClass];
    }
    return VK_SUCCESS;
}

void vs_vma_free(uint64_t handle, uint64_t allocationHandle) {
    auto* owner = Owner(handle);
    if (!owner || !allocationHandle) return;
    std::lock_guard<std::mutex> guard(owner->mutex);
    GrowthScope growth(*owner, UINT64_MAX);
    VmaAllocation allocation = Allocation(allocationHandle);
    VmaAllocationInfo2 info = {};
    vmaGetAllocationInfo2(owner->vma, allocation, &info);
    if (info.dedicatedMemory) {
        uint32_t purpose = Purpose(info.allocationInfo);
        owner->dedicatedBytes[purpose] -= info.allocationInfo.size;
        --owner->dedicatedCount[purpose];
    }
    vmaFreeMemory(owner->vma, allocation);
}

void vs_vma_set_frame_index(uint64_t handle, uint32_t frameIndex) {
    auto* owner = Owner(handle);
    if (!owner) return;
    std::lock_guard<std::mutex> guard(owner->mutex);
    vmaSetCurrentFrameIndex(owner->vma, frameIndex);
}

int32_t vs_vma_get_stats(uint64_t handle, VsVmaHeapStats* heaps, uint32_t heapCapacity,
    VsVmaClassStats* classes, uint32_t classCapacity, uint32_t* heapCount) {
    auto* owner = Owner(handle);
    if (!owner || !heaps || !classes || !heapCount || classCapacity < VS_VMA_CLASS_COUNT)
        return VK_ERROR_INITIALIZATION_FAILED;
    std::lock_guard<std::mutex> guard(owner->mutex);
    *heapCount = owner->memoryProperties->memoryHeapCount;
    if (heapCapacity < *heapCount) return VK_INCOMPLETE;
    VmaBudget budgets[VK_MAX_MEMORY_HEAPS] = {};
    vmaGetHeapBudgets(owner->vma, budgets);
    std::memset(heaps, 0, sizeof(*heaps) * heapCapacity);
    std::memset(classes, 0, sizeof(*classes) * classCapacity);
    for (uint32_t heap = 0; heap < *heapCount; ++heap) {
        const auto& budget = budgets[heap];
        heaps[heap] = { budget.statistics.blockBytes, budget.statistics.allocationBytes, budget.usage,
            budget.budget, owner->memoryProperties->memoryHeaps[heap].size,
            budget.statistics.blockCount, budget.statistics.allocationCount,
            owner->memoryProperties->memoryHeaps[heap].flags, 0, 0 };
    }
    for (uint32_t purpose = 0; purpose < VS_VMA_CLASS_COUNT; ++purpose) {
        for (uint32_t type = 0; type < owner->memoryProperties->memoryTypeCount; ++type) {
            VmaPool pool = owner->pools[purpose][type];
            if (!pool) continue;
            VmaStatistics statistics = {};
            vmaGetPoolStatistics(owner->vma, pool, &statistics);
            classes[purpose].blockBytes += statistics.blockBytes;
            classes[purpose].allocationBytes += statistics.allocationBytes;
            classes[purpose].blockCount += statistics.blockCount;
            classes[purpose].allocationCount += statistics.allocationCount;
            if (purpose == 0 || purpose == 4)
                heaps[owner->memoryProperties->memoryTypes[type].heapIndex].imageBlockBytes += statistics.blockBytes;
        }
        classes[purpose].dedicatedBytes = owner->dedicatedBytes[purpose];
        classes[purpose].dedicatedCount = owner->dedicatedCount[purpose];
    }
    return VK_SUCCESS;
}
