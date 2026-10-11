using Silk.NET.Vulkan;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Render.Vulkan.RayTracing;

/// <summary>Negotiates the portable KHR traversal path independently of reconstruction providers.</summary>
internal sealed unsafe class RayTracingRequirements : IDeviceRequirementContributor
{
    internal bool NrdImagesAvailable { get; private set; }
    public string Name => "Vulkan path tracing";
    internal bool Available { get; private set; }
    internal bool RayQueriesAvailable { get; private set; }
    internal string Reason { get; private set; } = "device not queried";

    public void ContributeInstanceExtensions(InstanceRequirements requirements) { }

    public void ContributeDeviceRequirements(DeviceRequirements requirements)
    {
        string[] extensions = ["VK_KHR_acceleration_structure", "VK_KHR_deferred_host_operations", "VK_KHR_ray_tracing_pipeline"];
        foreach (string extension in extensions)
            if (!requirements.Has(extension))
            {
                Reason = "missing " + extension;
                return;
            }

        var query = new PhysicalDeviceRayQueryFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr,
        };
        bool hasQueries = requirements.Has("VK_KHR_ray_query");
        var pipeline = new PhysicalDeviceRayTracingPipelineFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceRayTracingPipelineFeaturesKhr,
            PNext = hasQueries ? &query : null,
        };
        var acceleration = new PhysicalDeviceAccelerationStructureFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr,
            PNext = &pipeline,
        };
        requirements.QueryFeatures(&acceleration);
        if (!acceleration.AccelerationStructure || !pipeline.RayTracingPipeline)
        {
            Reason = "accelerationStructure or rayTracingPipeline feature unavailable";
            return;
        }
        var indexing = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features };
        requirements.QueryFeatures(&indexing);
        PhysicalDeviceFeatures imageFeatures = default;
        requirements.Api.GetPhysicalDeviceFeatures(requirements.PhysicalDevice, &imageFeatures);
        if (requirements.Vulkan12 == null || !indexing.ShaderSampledImageArrayNonUniformIndexing || !requirements.RequestShaderInt64())
        {
            Reason = "ray material addressing or nonuniform sampled-image indexing unavailable";
            return;
        }
        if (!imageFeatures.ShaderStorageImageExtendedFormats)
        {
            Reason = "compact reconstruction guide storage formats unavailable";
            return;
        }
        requirements.CoreFeatures->ShaderStorageImageExtendedFormats = true;
        // The selected RELAX schedule writes unformatted UAVs. Validation shaders
        // also read them, but validation is disabled and those kernels aren't created.
        NrdImagesAvailable = imageFeatures.ShaderStorageImageWriteWithoutFormat;
        if (NrdImagesAvailable)
        {
            requirements.CoreFeatures->ShaderStorageImageReadWithoutFormat = imageFeatures.ShaderStorageImageReadWithoutFormat;
            requirements.CoreFeatures->ShaderStorageImageWriteWithoutFormat = true;
        }
        requirements.Vulkan12->ShaderSampledImageArrayNonUniformIndexing = true;
        if (!requirements.RequestBufferDeviceAddress(Name))
        {
            Reason = "bufferDeviceAddress unavailable";
            return;
        }

        foreach (string extension in extensions) requirements.Request(extension, requestedBy: Name);
        requirements.ChainFeature(new PhysicalDeviceAccelerationStructureFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr,
            AccelerationStructure = true,
        });
        requirements.ChainFeature(new PhysicalDeviceRayTracingPipelineFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceRayTracingPipelineFeaturesKhr,
            RayTracingPipeline = true,
        });
        RayQueriesAvailable = hasQueries && query.RayQuery;
        if (RayQueriesAvailable)
        {
            requirements.Request("VK_KHR_ray_query", requestedBy: Name);
            requirements.ChainFeature(new PhysicalDeviceRayQueryFeaturesKHR
            {
                SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr,
                RayQuery = true,
            });
        }
        Available = true;
        Reason = "KHR acceleration structures and ray pipeline enabled" + (RayQueriesAvailable ? ", ray queries enabled" : "");
    }
}
