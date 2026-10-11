#include <NRD.h>
#include <algorithm>
#include <cstring>
#include <new>

#ifdef _WIN32
#define EXPORT extern "C" __declspec(dllexport)
#else
#define EXPORT extern "C" __attribute__((visibility("default")))
#endif

// This bridge only translates the pinned NRD host ABI. Vulkan images, barriers,
// pipelines and retirement remain owned by the renderer.
struct State {
    nrd::Instance* instance = nullptr;
    const nrd::DispatchDesc* dispatches = nullptr;
    uint32_t count = 0, frame = 0;
    float previousJitter[2] = {};
    bool first = true;
};
struct Info {
    uint32_t pipelines, permanent, transient, sets, textures, storage;
    uint32_t textureOffset, storageOffset, constantOffset, samplerOffset;
    uint32_t constantSet, resourceSet, constantBytes;
};
struct Pipeline {
    const void* shader;
    uint64_t bytes;
    uint32_t textures, storage, constants;
};
struct Dispatch {
    const void* constants;
    uint32_t bytes, pipeline, width, height, resources;
};
EXPORT int VsNrdCreate(State** output, Info* info) {
    if (!output || !info) return -1;
    *output = nullptr;
    auto state = new (std::nothrow) State;
    if (!state) return -2;
    nrd::DenoiserDesc denoiser{0, nrd::Denoiser::RELAX_DIFFUSE_SPECULAR};
    nrd::InstanceCreationDesc create{};
    create.denoisers = &denoiser; create.denoisersNum = 1;
    auto result = nrd::CreateInstance(create, state->instance);
    if (result != nrd::Result::SUCCESS) { delete state; return int(result) + 1; }
    const auto& library = *nrd::GetLibraryDesc();
    const auto& desc = *nrd::GetInstanceDesc(*state->instance);
    if (library.versionMajor != 4 || library.versionMinor != 17 || library.versionBuild != 3 ||
        library.normalEncoding != nrd::NormalEncoding::RGBA16_SNORM ||
        library.roughnessEncoding != nrd::RoughnessEncoding::LINEAR) {
        nrd::DestroyInstance(*state->instance); delete state; return -3;
    }
    *info = {desc.pipelinesNum, desc.permanentPoolSize, desc.transientPoolSize,
        desc.descriptorPoolDesc.setsMaxNum, desc.descriptorPoolDesc.totalTexturesNum,
        desc.descriptorPoolDesc.totalStorageTexturesNum,
        library.spirvBindingOffsets.textureOffset, library.spirvBindingOffsets.storageTextureAndBufferOffset,
        library.spirvBindingOffsets.constantBufferOffset + desc.constantBufferRegisterIndex,
        library.spirvBindingOffsets.samplerOffset + desc.samplersBaseRegisterIndex,
        desc.constantBufferAndSamplersSpaceIndex, desc.resourcesSpaceIndex, desc.constantBufferMaxDataSize};
    *output = state; return 0;
}
EXPORT void VsNrdDestroy(State* state) {
    if (!state) return;
    nrd::DestroyInstance(*state->instance); delete state;
}
EXPORT int VsNrdPipeline(State* state, uint32_t index, Pipeline* output) {
    if (!state || !output) return -1;
    const auto& desc = *nrd::GetInstanceDesc(*state->instance);
    if (index >= desc.pipelinesNum) return -1;
    const auto& source = desc.pipelines[index];
    *output = {source.computeShaderSPIRV.bytecode, source.computeShaderSPIRV.size, 0, 0, source.hasConstantData ? 1u : 0u};
    for (uint32_t i = 0; i < source.resourceRangesNum; i++) {
        const auto& range = source.resourceRanges[i];
        if (range.descriptorType == nrd::DescriptorType::TEXTURE) output->textures += range.descriptorsNum;
        else output->storage += range.descriptorsNum;
    }
    return output->shader && output->bytes ? 0 : -2;
}
EXPORT int VsNrdTexture(State* state, uint32_t permanent, uint32_t index, uint32_t* format, uint32_t* factor) {
    if (!state || !format || !factor) return -1;
    const auto& desc = *nrd::GetInstanceDesc(*state->instance);
    if (index >= (permanent ? desc.permanentPoolSize : desc.transientPoolSize)) return -1;
    const auto& source = (permanent ? desc.permanentPool : desc.transientPool)[index];
    *format = uint32_t(source.format); *factor = source.downsampleFactor; return 0;
}
EXPORT int VsNrdBegin(State* state, const float* projection, const float* previousProjection,
    const float* view, const float* previousView, uint32_t width, uint32_t height,
    float dx, float dy, float dz, float jitterX, float jitterY, float milliseconds,
    float range, uint32_t reset, uint32_t* count) {
    if (!state || !count || !projection || !previousProjection || !view || !previousView ||
        width == 0 || height == 0 || width > 65535 || height > 65535) return -1;
    nrd::CommonSettings settings{};
    std::memcpy(settings.viewToClipMatrix, projection, 64);
    std::memcpy(settings.viewToClipMatrixPrev, previousProjection, 64);
    std::memcpy(settings.worldToViewMatrix, view, 64);
    std::memcpy(settings.worldToViewMatrixPrev, previousView, 64);
    // NRD decomposes D3D-style projections. Our images retain the game's
    // increasing-world-Y row order, so negate clip Y and remap GL depth.
    for (int column = 0; column < 4; column++) {
        int offset = column * 4;
        settings.viewToClipMatrix[offset + 1] *= -1.0f;
        settings.viewToClipMatrixPrev[offset + 1] *= -1.0f;
        settings.viewToClipMatrix[offset + 2] = 0.5f * (projection[offset + 2] + projection[offset + 3]);
        settings.viewToClipMatrixPrev[offset + 2] = 0.5f * (previousProjection[offset + 2] + previousProjection[offset + 3]);
    }
    // Previous camera coordinates must include the moving world origin.
    for (int row = 0; row < 4; row++)
        settings.worldToViewMatrixPrev[12 + row] += previousView[row] * dx +
            previousView[4 + row] * dy + previousView[8 + row] * dz;
    settings.resourceSize[0] = settings.resourceSizePrev[0] = settings.rectSize[0] = settings.rectSizePrev[0] = uint16_t(width);
    settings.resourceSize[1] = settings.resourceSizePrev[1] = settings.rectSize[1] = settings.rectSizePrev[1] = uint16_t(height);
    settings.motionVectorScale[0] = 1.0f / width; settings.motionVectorScale[1] = 1.0f / height;
    settings.cameraJitter[0] = -jitterX / width; settings.cameraJitter[1] = -jitterY / height;
    settings.cameraJitterPrev[0] = state->previousJitter[0]; settings.cameraJitterPrev[1] = state->previousJitter[1];
    settings.timeDeltaBetweenFrames = milliseconds;
    settings.denoisingRange = range;
    settings.frameIndex = state->frame++;
    settings.accumulationMode = state->first || reset ? nrd::AccumulationMode::CLEAR_AND_RESTART : nrd::AccumulationMode::CONTINUE;
    auto result = nrd::SetCommonSettings(*state->instance, settings);
    if (result != nrd::Result::SUCCESS) return int(result) + 1;
    nrd::Identifier identifier = 0;
    result = nrd::GetComputeDispatches(*state->instance, &identifier, 1, state->dispatches, state->count);
    if (result != nrd::Result::SUCCESS) return int(result) + 1;
    std::memcpy(state->previousJitter, settings.cameraJitter, sizeof(state->previousJitter));
    state->first = false; *count = state->count; return 0;
}
EXPORT int VsNrdDispatch(State* state, uint32_t index, Dispatch* output) {
    if (!state || !output || index >= state->count) return -1;
    const auto& source = state->dispatches[index];
    *output = {source.constantBufferData, source.constantBufferDataSize, source.pipelineIndex,
        source.gridWidth, source.gridHeight, source.resourcesNum};
    return 0;
}
EXPORT int VsNrdResource(State* state, uint32_t dispatch, uint32_t index, uint32_t* kind,
    uint32_t* poolIndex, uint32_t* storage) {
    if (!state || !kind || !poolIndex || !storage || dispatch >= state->count || index >= state->dispatches[dispatch].resourcesNum) return -1;
    const auto& source = state->dispatches[dispatch].resources[index];
    using T = nrd::ResourceType;
    switch (source.type) {
        case T::IN_MV: *kind = 0; break;
        case T::IN_NORMAL_ROUGHNESS: *kind = 1; break;
        case T::IN_VIEWZ: *kind = 2; break;
        case T::IN_DIFF_RADIANCE_HITDIST: *kind = 3; break;
        case T::IN_SPEC_RADIANCE_HITDIST: *kind = 4; break;
        case T::OUT_DIFF_RADIANCE_HITDIST: *kind = 5; break;
        case T::OUT_SPEC_RADIANCE_HITDIST: *kind = 6; break;
        case T::TRANSIENT_POOL: *kind = 7; break;
        case T::PERMANENT_POOL: *kind = 8; break;
        default: return -2;
    }
    *poolIndex = source.indexInPool;
    *storage = source.descriptorType == nrd::DescriptorType::STORAGE_TEXTURE ? 1u : 0u;
    return 0;
}
