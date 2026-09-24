using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// A subsystem that needs something from the instance or the device (plan seam
/// S1). The latency backends are the first of these; NGX/Streamline's
/// <c>GetFeatureInstance/DeviceExtensionRequirements</c> plug in the same way.
///
/// Contributors are consulted inside <see cref="VulkanContext.CreateInstance" />
/// and <see cref="VulkanContext.CreateDevice" />, before the create call, and
/// may only ever <em>ask</em>: an extension the loader or the driver does not
/// advertise is refused by <see cref="InstanceRequirements.Request" /> /
/// <see cref="DeviceRequirements.Request" /> and reported back, never named in
/// the create info. Naming an absent extension fails creation outright, which
/// would turn an optional feature into a silent fall back to OpenGL (rule 1).
/// </summary>
internal interface IDeviceRequirementContributor
{
    /// <summary>Short name for the log line when a request is refused.</summary>
    string Name { get; }

    /// <summary>Called once before vkCreateInstance.</summary>
    void ContributeInstanceExtensions(InstanceRequirements requirements);

    /// <summary>
    /// Called once before vkCreateDevice, with the physical device already
    /// chosen, so a contributor can query features before deciding what to ask
    /// for. Anything chained here is part of the VkDeviceCreateInfo pNext chain.
    /// </summary>
    void ContributeDeviceRequirements(DeviceRequirements requirements);
}

/// <summary>What the loader advertises, and what the instance will enable.</summary>
internal sealed class InstanceRequirements
{
    private readonly HashSet<string> _available;
    private readonly List<string> _enabled;

    public InstanceRequirements(HashSet<string> available, List<string> enabled)
    {
        _available = available;
        _enabled = enabled;
    }

    /// <summary>Notes about refused requests; never an error.</summary>
    public Action<string>? Log;

    public bool Has(string name) => _available.Contains(name);

    public bool IsEnabled(string name) => _enabled.Contains(name);

    /// <summary>
    /// Enables <paramref name="name" /> when the loader has it. Returns whether
    /// the instance will have it; asking twice is harmless.
    /// </summary>
    public bool Request(string name, string? requestedBy = null)
    {
        if (_enabled.Contains(name)) return true;
        if (!_available.Contains(name))
        {
            Log?.Invoke((requestedBy ?? "a contributor") + " asked for instance extension " + name +
                ", which the loader does not advertise; continuing without it");
            return false;
        }
        _enabled.Add(name);
        return true;
    }

    public IReadOnlyList<string> Enabled => _enabled;
}

/// <summary>
/// What the physical device advertises, what the logical device will enable, and
/// the one pNext chain of VkDeviceCreateInfo.
///
/// The chain replaces the single-slot "optionalFeatures" of the pre-latency
/// context: colour write, device fault and every contributor's feature struct
/// now link into one list, so two optional tiers can be on at the same time.
/// Structs handed to <see cref="ChainFeature{T}" /> are copied into unmanaged
/// scratch owned here and freed by <see cref="Dispose" /> after vkCreateDevice
/// has returned, so a contributor never has to keep memory pinned itself.
/// </summary>
internal sealed unsafe class DeviceRequirements : IDisposable
{
    private readonly Dictionary<string, uint> _available;
    private readonly List<string> _enabled;
    private readonly List<nint> _scratch = new();
    private void* _chain;
    private bool _disposed;

    public DeviceRequirements(
        Vk api, PhysicalDevice physicalDevice, Dictionary<string, uint> available, List<string> enabled)
    {
        Api = api;
        PhysicalDevice = physicalDevice;
        _available = available;
        _enabled = enabled;
    }

    public Vk Api { get; }
    public PhysicalDevice PhysicalDevice { get; }

    /// <summary>Notes about refused requests; never an error.</summary>
    public Action<string>? Log;

    /// <summary>Whether the device advertises the extension at or above a revision.</summary>
    public bool Has(string name, uint minimumSpecVersion = 0) =>
        _available.TryGetValue(name, out uint version) && version >= minimumSpecVersion;

    /// <summary>The advertised revision, 0 when the device does not have the extension.</summary>
    public uint SpecVersion(string name) => _available.TryGetValue(name, out uint version) ? version : 0;

    public bool IsEnabled(string name) => _enabled.Contains(name);

    /// <summary>
    /// Enables <paramref name="name" /> when the device advertises it at or above
    /// <paramref name="minimumSpecVersion" />. Returns whether the device will
    /// have it; asking twice is harmless.
    /// </summary>
    public bool Request(string name, uint minimumSpecVersion = 0, string? requestedBy = null)
    {
        if (_enabled.Contains(name)) return true;
        if (!Has(name, minimumSpecVersion))
        {
            Log?.Invoke((requestedBy ?? "a contributor") + " asked for device extension " + name +
                (minimumSpecVersion > 0 ? " revision >= " + minimumSpecVersion : "") +
                ", which this device does not advertise (" +
                (_available.ContainsKey(name) ? "revision " + SpecVersion(name) : "absent") +
                "); continuing without it");
            return false;
        }
        _enabled.Add(name);
        return true;
    }

    /// <summary>
    /// Queries physical-device features through a caller-built pNext chain. The
    /// two-step shape the colour-write probe uses: query what is supported, then
    /// re-request only what is actually going to be used.
    /// </summary>
    public void QueryFeatures(void* chainHead)
    {
        var query = new PhysicalDeviceFeatures2
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = chainHead,
        };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &query);
    }

    /// <summary>
    /// Links a struct the caller keeps alive (a stack local of the creating
    /// method) into the chain. Its own pNext is overwritten.
    /// </summary>
    public void ChainFeature(void* feature)
    {
        if (feature == null) return;
        // Every Vulkan structure begins with VkStructureType sType; void* pNext,
        // so pNext sits one pointer in on both 32- and 64-bit ABIs.
        *(void**)((byte*)feature + IntPtr.Size) = _chain;
        _chain = feature;
    }

    /// <summary>
    /// Copies <paramref name="feature" /> into scratch memory owned here and
    /// links it in. The copy lives until <see cref="Dispose" />, which the
    /// context calls after vkCreateDevice.
    /// </summary>
    public void ChainFeature<T>(T feature) where T : unmanaged
    {
        nint memory = Marshal.AllocHGlobal(sizeof(T));
        _scratch.Add(memory);
        *(T*)memory = feature;
        ChainFeature((void*)memory);
    }

    /// <summary>The head of the chain, null when nothing was chained.</summary>
    public void* Chain => _chain;

    public IReadOnlyList<string> Enabled => _enabled;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _chain = null;
        for (int i = 0; i < _scratch.Count; i++) Marshal.FreeHGlobal(_scratch[i]);
        _scratch.Clear();
    }
}

/// <summary>
/// What the physical device advertises for latency work, read once at device
/// creation (plan seam S1, "detect without enabling anything that is not used").
/// </summary>
internal readonly struct LatencyDeviceSupport
{
    public LatencyDeviceSupport(
        bool nvLowLatency2, uint nvLowLatency2SpecVersion, bool amdAntiLag, bool presentId, bool presentId2)
    {
        NvLowLatency2 = nvLowLatency2;
        NvLowLatency2SpecVersion = nvLowLatency2SpecVersion;
        AmdAntiLag = amdAntiLag;
        PresentId = presentId;
        PresentId2 = presentId2;
    }

    /// <summary>VK_NV_low_latency2 is advertised.</summary>
    public bool NvLowLatency2 { get; }

    /// <summary>Its advertised revision; 0 when absent.</summary>
    public uint NvLowLatency2SpecVersion { get; }

    /// <summary>VK_AMD_anti_lag is advertised and VkPhysicalDeviceAntiLagFeaturesAMD.antiLag is supported.</summary>
    public bool AmdAntiLag { get; }

    /// <summary>VK_KHR_present_id with its presentId feature.</summary>
    public bool PresentId { get; }

    /// <summary>VK_KHR_present_id2 with its presentId2 feature.</summary>
    public bool PresentId2 { get; }

    public override string ToString() =>
        "nv_low_latency2=" + (NvLowLatency2 ? "rev " + NvLowLatency2SpecVersion : "no") +
        " amd_anti_lag=" + (AmdAntiLag ? "yes" : "no") +
        " present_id=" + (PresentId ? "yes" : "no") +
        " present_id2=" + (PresentId2 ? "yes" : "no");
}

/// <summary>
/// The latency subsystem's device requirements (plan seam S1): detect what the
/// driver offers and enable what the frame-marking foundation uses.
///
/// That is <c>VK_KHR_present_id</c> alone, and only on a presentable device that
/// supports its feature: one id per present, chained as <c>VkPresentIdKHR</c>, is
/// the frame identity carried through to the display, and chaining it is a
/// validation error unless the extension and the feature are both on. The vendor
/// extensions are detected and reported, never enabled - their backends live on
/// <c>feat/latency</c>, which replaces the selection here with its ranked one.
///
/// Extension revisions and feature bits are recorded either way, because the
/// "device up" line reports what the driver offered, not only what was taken.
/// </summary>
internal sealed unsafe class LatencyDeviceRequirements : IDeviceRequirementContributor
{
    public const string NvLowLatency2ExtensionName = "VK_NV_low_latency2";
    public const string AmdAntiLagExtensionName = "VK_AMD_anti_lag";
    public const string PresentIdExtensionName = "VK_KHR_present_id";
    public const string PresentId2ExtensionName = "VK_KHR_present_id2";
    public const string SwapchainExtensionName = "VK_KHR_swapchain";

    public string Name => "latency";

    /// <summary>What the driver advertises, filled in by <see cref="ContributeDeviceRequirements" />.</summary>
    public LatencyDeviceSupport Support { get; private set; }

    /// <summary>The backend chosen for this device; always None on this branch.</summary>
    public LatencyBackendKind Selected => LatencyBackendKind.None;

    /// <summary>VK_KHR_present_id and its feature are enabled.</summary>
    public bool PresentIdEnabled { get; private set; }

    /// <summary>Nothing on the instance: the foundation needs no instance extension.</summary>
    public void ContributeInstanceExtensions(InstanceRequirements requirements)
    {
    }

    public void ContributeDeviceRequirements(DeviceRequirements requirements)
    {
        uint nvRevision = requirements.SpecVersion(NvLowLatency2ExtensionName);
        bool hasAmd = requirements.Has(AmdAntiLagExtensionName);
        bool hasPresentId = requirements.Has(PresentIdExtensionName);
        bool hasPresentId2 = requirements.Has(PresentId2ExtensionName);

        // The feature bits behind those extensions, queried the way the colour-write
        // probe does: one GetPhysicalDeviceFeatures2 over a chain of only the structs
        // whose extension is present, then re-request just what is used.
        var antiLag = new PhysicalDeviceAntiLagFeaturesAMD
        {
            SType = StructureType.PhysicalDeviceAntiLagFeaturesAmd,
        };
        var presentId = new PhysicalDevicePresentIdFeaturesKHR
        {
            SType = StructureType.PhysicalDevicePresentIDFeaturesKhr,
        };
        var presentId2 = new PhysicalDevicePresentId2FeaturesKHR
        {
            SType = StructureType.PhysicalDevicePresentID2FeaturesKhr,
        };

        void* query = null;
        if (hasAmd) { antiLag.PNext = query; query = &antiLag; }
        if (hasPresentId) { presentId.PNext = query; query = &presentId; }
        if (hasPresentId2) { presentId2.PNext = query; query = &presentId2; }
        if (query != null) requirements.QueryFeatures(query);

        Support = new LatencyDeviceSupport(
            nvRevision > 0, nvRevision,
            hasAmd && antiLag.AntiLag,
            hasPresentId && presentId.PresentId,
            hasPresentId2 && presentId2.PresentId2);

        // VK_KHR_present_id hangs off VK_KHR_swapchain: a headless device reports
        // what it found and enables nothing.
        bool presentable = requirements.IsEnabled(SwapchainExtensionName);
        PresentIdEnabled = presentable && Support.PresentId
            && requirements.Request(PresentIdExtensionName, 0, Name);
        if (PresentIdEnabled)
        {
            requirements.ChainFeature(new PhysicalDevicePresentIdFeaturesKHR
            {
                SType = StructureType.PhysicalDevicePresentIDFeaturesKhr,
                PresentId = true,
            });
        }
    }

    /// <summary>The latency token of the "device up" log line.</summary>
    public string Summary() => Summary(Selected, Support, PresentIdEnabled);

    /// <summary>What runs, what the driver offered, and whether present ids are on.</summary>
    public static string Summary(LatencyBackendKind kind, in LatencyDeviceSupport support, bool presentIdEnabled)
    {
        string text = "latency backend " + LatencyBackends.Token(kind);
        if (support.NvLowLatency2) text += " (low_latency2 rev " + support.NvLowLatency2SpecVersion + " available)";
        if (support.AmdAntiLag) text += " (anti_lag available)";
        if (support.PresentId || support.PresentId2)
        {
            text += ", present id " + (presentIdEnabled ? "ON" : "available") +
                (support.PresentId2 ? " (+id2)" : "");
        }
        return text;
    }
}
