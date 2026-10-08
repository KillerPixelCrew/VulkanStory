using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

// Scene subset; other native producers/motion hooks must join the scene group.
/// <summary>Substitutes original weighted-transparency setup and merge calls with the owned OIT targets and pass.</summary>
/// <remarks>Patch discovery and installation belong to startup. Callbacks use the committed routing predicate; game object identity remains in the integration assembly.</remarks>
internal static class TransparencyConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-transparency";
    private static ProcessRuntime? runtime;
    private static readonly AccessTools.FieldRef<SystemRenderOITLayers.BeforeOIT, ICoreClientAPI> Api =
        AccessTools.FieldRefAccess<SystemRenderOITLayers.BeforeOIT, ICoreClientAPI>("capi");
    private static readonly MethodInfo Before = Target(typeof(SystemRenderOITLayers.BeforeOIT), "OnRenderFrame", [typeof(float), typeof(EnumRenderStage)]);
    private static readonly MethodInfo After = Target(typeof(SystemRenderOITLayers.AfterOIT), "OnRenderFrame", [typeof(float), typeof(EnumRenderStage)]);
    private static readonly MethodInfo Dispose = Target(typeof(SystemRenderOITLayers.BeforeOIT), "Dispose", []);
    private static readonly MethodInfo Merge = Target(typeof(ClientPlatformWindows), "MergeTransparentRenderPass", []);
    private static MethodInfo Target(Type type, string name, Type[] parameters) =>
        type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, parameters, null) ??
        throw new MissingMethodException(type.FullName, name);
    /// <summary>Creates the dormant patch group for this consumer path; validation and installation remain separate transaction steps.</summary>
    /// <param name="owner">Process runtime that owns this group and its session.</param>
    /// <returns>Validation, installation and removal callbacks for the startup transaction.</returns>
    /// <remarks>Binding or IL-anchor mismatches reject the group. Creating the group does not enable graphics routing.</remarks>
    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner)
    {
        var harmony = new Harmony(Owner);
        bool attempted = false;
        return new StartupPatchGroup("graphics-scene-transparency", () =>
        {
            foreach (var method in new[] { Before, After, Dispose, Merge })
                if (method.ReturnType != typeof(void) || method.GetMethodBody() == null)
                    throw new InvalidOperationException("Original transparency target changed.");
            if (AccessTools.Field(typeof(SystemRenderOITLayers.BeforeOIT), "capi")?.FieldType != typeof(ICoreClientAPI))
                throw new MissingFieldException("Original OIT API association changed.");
        }, () =>
        {
            if (runtime != null) throw new InvalidOperationException("Transparency routing already has an owner.");
            runtime = owner; attempted = true;
            harmony.Patch(Before, prefix: new HarmonyMethod(typeof(TransparencyConsumerPatches), nameof(Begin)) { priority = Priority.First });
            harmony.Patch(After, prefix: new HarmonyMethod(typeof(TransparencyConsumerPatches), nameof(Bind)) { priority = Priority.First });
            harmony.Patch(Dispose, prefix: new HarmonyMethod(typeof(TransparencyConsumerPatches), nameof(Release)) { priority = Priority.First });
            harmony.Patch(Merge, prefix: new HarmonyMethod(typeof(TransparencyConsumerPatches), nameof(Compose)) { priority = Priority.First });
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner);
            if (ReferenceEquals(runtime, owner)) runtime = null;
            attempted = false;
        });
    }
    private static bool TryAdapter(ClientPlatformWindows? platform, out GameGraphicsAdapter adapter)
    {
        adapter = null!;
        if (runtime?.Routing.RoutingEnabled != true) return false;
        if (platform == null || !runtime.TrySession(platform, out var session))
            throw new InvalidOperationException("Active transparency routing has no original-platform session.");
        adapter = session.Graphics; return true;
    }
    private static bool Begin(SystemRenderOITLayers.BeforeOIT __instance)
    {
        if (!TryAdapter(ScreenManager.Platform as ClientPlatformWindows, out var adapter)) return true;
        adapter.BeginOit(Api(__instance)); return false;
    }
    private static bool Bind()
    {
        if (!TryAdapter(ScreenManager.Platform as ClientPlatformWindows, out var adapter)) return true;
        adapter.BindOit(); return false;
    }
    private static bool Release(SystemRenderOITLayers.BeforeOIT __instance)
    {
        if (!TryAdapter(ScreenManager.Platform as ClientPlatformWindows, out var adapter)) return true;
        adapter.ReleaseOitFor(Api(__instance)); return false;
    }
    private static bool Compose(ClientPlatformWindows __instance)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.MergeTransparent(); return false;
    }
}
