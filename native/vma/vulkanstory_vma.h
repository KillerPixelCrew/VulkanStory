// VulkanStory's stable x64 C ABI for AMD Vulkan Memory Allocator (MIT).
// Vulkan resource creation, binding and destruction remain with the renderer.
#pragma once
#include <stdint.h>

#if defined(_WIN32)
#define VS_VMA_API __declspec(dllexport)
#else
#define VS_VMA_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

enum { VS_VMA_ABI_VERSION = 1, VS_VMA_CLASS_COUNT = 6, VS_VMA_MAX_HEAPS = 16 };
enum { VS_VMA_MEMORY_BUDGET = 1, VS_VMA_BUFFER_DEVICE_ADDRESS = 2 };
enum {
    VS_VMA_MAPPED = 1,
    VS_VMA_DEDICATED = 2,
    VS_VMA_CAN_ALIAS = 4,
    VS_VMA_NEVER_ALLOCATE = 8,
    VS_VMA_HOST_RANDOM_ACCESS = 16
};

// Handles and function pointers are uint64_t because both supported RIDs are x64.
// flags only enables features/extensions already enabled on the supplied device.
typedef struct VsVmaCreateInfo {
    uint32_t abiVersion, apiVersion, flags, reserved;
    uint64_t instance, physicalDevice, device;
    uint64_t getInstanceProcAddr, getDeviceProcAddr;
} VsVmaCreateInfo;

typedef struct VsVmaAllocateInfo {
    uint64_t size, alignment, buffer, image;
    uint32_t memoryTypeBits, requiredFlags, preferredFlags, poolClass, flags, reserved;
    // Maximum additional VkDeviceMemory bytes for this call. UINT64_MAX is unlimited.
    // Existing blocks remain reusable with a limit of zero.
    uint64_t growthLimitBytes;
} VsVmaAllocateInfo;

typedef struct VsVmaAllocationInfo {
    uint64_t allocation, memory, offset, size, mapped;
    uint32_t memoryTypeIndex, heapIndex, memoryProperties, poolClass, flags, reserved;
} VsVmaAllocationInfo;

typedef struct VsVmaHeapStats {
    uint64_t blockBytes, allocationBytes, usage, budget, heapSize;
    uint32_t blockCount, allocationCount, heapFlags, reserved;
    uint64_t imageBlockBytes;
} VsVmaHeapStats;

// Each purpose class owns VMA pools, so blockBytes counts real reservations once.
// Dedicated bytes/count are subsets of that class's block/allocation statistics.
typedef struct VsVmaClassStats {
    uint64_t blockBytes, allocationBytes, blockCount, allocationCount;
    uint64_t dedicatedBytes, dedicatedCount;
} VsVmaClassStats;

// Result-bearing functions return VkResult. Output handles are zero on failure.
VS_VMA_API int32_t vs_vma_create(const VsVmaCreateInfo* info, uint64_t* allocator);
VS_VMA_API void vs_vma_destroy(uint64_t allocator);
VS_VMA_API int32_t vs_vma_allocate(uint64_t allocator, const VsVmaAllocateInfo* info, VsVmaAllocationInfo* allocation);
VS_VMA_API void vs_vma_free(uint64_t allocator, uint64_t allocation);
VS_VMA_API void vs_vma_set_frame_index(uint64_t allocator, uint32_t frameIndex);
VS_VMA_API int32_t vs_vma_get_stats(uint64_t allocator, VsVmaHeapStats* heaps, uint32_t heapCapacity,
    VsVmaClassStats* classes, uint32_t classCapacity, uint32_t* heapCount);

#ifdef __cplusplus
}
#endif
