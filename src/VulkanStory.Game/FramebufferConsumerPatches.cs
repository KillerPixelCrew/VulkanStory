using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

/// <summary>Replaces original platform framebuffer operations with adapter-owned targets after startup routing commits.</summary>
/// <remarks>Patch discovery and installation belong to startup. Callbacks use the committed routing predicate; game object identity remains in the integration assembly.</remarks>
internal static class FramebufferConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-framebuffers";
    private static Func<bool>? enabled;
    private sealed record Binding(string Name, Type Result, Type[] Parameters, string Prefix);
    private static readonly Binding[] Bindings =
    [
        new("CreateFramebuffer", typeof(FrameBufferRef), [typeof(FramebufferAttrs)], nameof(Create)),
        new("DisposeFrameBuffer", typeof(void), [typeof(FrameBufferRef), typeof(bool)], nameof(Dispose)),
        new("set_CurrentFrameBuffer", typeof(void), [typeof(FrameBufferRef)], nameof(Bind)),
        new("set_CurrentFrameBufferKeepVw", typeof(void), [typeof(FrameBufferRef)], nameof(BindKeepViewport)),
        new("GlClearColorRgbaf", typeof(void), [typeof(float), typeof(float), typeof(float), typeof(float)], nameof(ClearColor)),
        new("ClearFrameBuffer", typeof(void), [typeof(FrameBufferRef), typeof(bool)], nameof(ClearRef)),
        new("ClearFrameBuffer", typeof(void), [typeof(FrameBufferRef), typeof(float[]), typeof(bool), typeof(bool)], nameof(ClearRefColor)),
        new("ClearFrameBuffer", typeof(void), [typeof(EnumFrameBuffer)], nameof(ClearPass)),
        new("SetupDefaultFrameBuffers", typeof(List<FrameBufferRef>), [], nameof(SetupDefaults)),
        new("DisposeFrameBuffers", typeof(void), [typeof(List<FrameBufferRef>)], nameof(DisposeDefaults)),
        new("LoadFrameBuffer", typeof(void), [typeof(FrameBufferRef), typeof(int)], nameof(LoadRef)),
        new("UnloadFrameBuffer", typeof(void), [typeof(FrameBufferRef)], nameof(UnloadRef)),
        new("LoadFrameBuffer", typeof(void), [typeof(EnumFrameBuffer)], nameof(LoadPass)),
        new("UnloadFrameBuffer", typeof(void), [typeof(EnumFrameBuffer)], nameof(UnloadPass)),
    ];
    private static MethodInfo Resolve(Binding binding)
    {
        var method = typeof(ClientPlatformWindows).GetMethod(binding.Name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
            binder: null, types: binding.Parameters, modifiers: null);
        if (method is null || method.ReturnType != binding.Result || method.GetMethodBody() is null)
            throw new MissingMethodException(typeof(ClientPlatformWindows).FullName, binding.Name);
        return method;
    }
    /// <summary>Creates the dormant patch group for this consumer path; validation and installation remain separate transaction steps.</summary>
    /// <param name="routing">Predicate read by routed callbacks after the complete startup transaction commits.</param>
    /// <returns>Validation, installation and removal callbacks for the startup transaction.</returns>
    /// <remarks>Binding or IL-anchor mismatches reject the group. Creating the group does not enable graphics routing.</remarks>
    internal static StartupPatchGroup CreateSubset(Func<bool> routing)
    {
        ArgumentNullException.ThrowIfNull(routing);
        var harmony = new Harmony(Owner);
        (MethodInfo Target, MethodInfo Prefix)[]? methods = null;
        bool attempted = false;
        return new StartupPatchGroup("graphics-framebuffers", () =>
        {
            GameFramebufferBindings.Validate();
            FramebufferDrawBufferRoutes.ValidateBindings();
            methods = Bindings.Select(binding => (Resolve(binding), typeof(FramebufferConsumerPatches).GetMethod(binding.Prefix,
                BindingFlags.NonPublic | BindingFlags.Static)!)).ToArray();
        }, () =>
        {
            if (methods is null || enabled != null) throw new InvalidOperationException("Framebuffer routing is unvalidated or already owned.");
            enabled = routing; attempted = true;
            foreach (var method in methods) harmony.Patch(method.Target, prefix: new HarmonyMethod(method.Prefix));
            FramebufferDrawBufferRoutes.Install(harmony);
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner);
            if (ReferenceEquals(enabled, routing)) enabled = null;
            attempted = false;
        });
    }
    internal static bool TryAdapter(ClientPlatformWindows platform, out GameGraphicsAdapter adapter)
    {
        adapter = null!;
        if (enabled?.Invoke() != true) return false;
        if (!GameGraphicsAdapter.TryGet(platform, out var found) || found is null)
            throw new InvalidOperationException("Active framebuffer routing has no renderer adapter.");
        adapter = found; return true;
    }
    private static bool Create(ClientPlatformWindows __instance, FramebufferAttrs __0, ref FrameBufferRef __result)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        __result = adapter.CreateFramebuffer(__0); return false;
    }
    private static bool Dispose(ClientPlatformWindows __instance, FrameBufferRef __0, bool __1)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.DisposeFramebuffer(__0, __1); return false;
    }
    private static bool Bind(ClientPlatformWindows __instance, FrameBufferRef __0)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.SetFramebuffer(__0, keepViewport: false); return false;
    }
    private static bool BindKeepViewport(ClientPlatformWindows __instance, FrameBufferRef __0)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.SetFramebuffer(__0, keepViewport: true); return false;
    }
    private static bool ClearColor(ClientPlatformWindows __instance, float __0, float __1, float __2, float __3)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.SetClearColor(__0, __1, __2, __3); return false;
    }
    private static bool ClearRef(ClientPlatformWindows __instance, FrameBufferRef __0, bool __1)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.ClearFramebuffer(__0, __1); return false;
    }
    private static bool ClearRefColor(ClientPlatformWindows __instance, FrameBufferRef __0, float[] __1, bool __2, bool __3)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.ClearFramebuffer(__0, __1, __2, __3); return false;
    }
    private static bool ClearPass(ClientPlatformWindows __instance, EnumFrameBuffer __0)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.ClearFramebuffer(__0); return false;
    }
    private static bool SetupDefaults(ClientPlatformWindows __instance, ref List<FrameBufferRef> __result)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        __result = adapter.SetupDefaultFramebuffers(); return false;
    }
    private static bool DisposeDefaults(ClientPlatformWindows __instance, List<FrameBufferRef> __0)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.DisposeFramebuffers(__0); return false;
    }
    private static bool LoadRef(ClientPlatformWindows __instance, FrameBufferRef __0, int __1)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.LoadFramebuffer(__0, __1); return false;
    }
    private static bool UnloadRef(ClientPlatformWindows __instance, FrameBufferRef __0)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.LoadFramebuffer(EnumFrameBuffer.Primary); return false;
    }
    private static bool LoadPass(ClientPlatformWindows __instance, EnumFrameBuffer __0)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.LoadFramebuffer(__0); return false;
    }
    private static bool UnloadPass(ClientPlatformWindows __instance, EnumFrameBuffer __0)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.UnloadFramebuffer(__0); return false;
    }
}
