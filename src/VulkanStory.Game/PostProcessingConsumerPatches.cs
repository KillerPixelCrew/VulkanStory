using System.Reflection;
using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

// Post effects, final composition and blit form one group. Scene/API/window
// coverage must still join it before the complete startup transaction commits.
/// <summary>Routes original post, final composition and primary-to-default seams through the session reconstruction pipeline.</summary>
/// <remarks>Patch discovery and installation belong to startup. Callbacks use the committed routing predicate; game object identity remains in the integration assembly.</remarks>
internal static class PostProcessingConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-post-processing";
    private static ProcessRuntime? runtime;
    private static readonly MethodInfo Post = AccessTools.Method(typeof(ClientPlatformWindows),
        "RenderPostprocessingEffects", [typeof(float[])]) ?? throw new MissingMethodException("Original post-processing method is missing.");
    private static readonly MethodInfo Final = AccessTools.Method(typeof(ClientPlatformWindows), "RenderFinalComposition", []) ??
        throw new MissingMethodException("Original final composition method is missing.");
    private static readonly MethodInfo Blit = AccessTools.Method(typeof(ClientPlatformWindows), "BlitPrimaryToDefault", []) ??
        throw new MissingMethodException("Original final blit method is missing.");
    /// <summary>Creates the dormant patch group for this consumer path; validation and installation remain separate transaction steps.</summary>
    /// <param name="owner">Process runtime that owns this group and its session.</param>
    /// <returns>Validation, installation and removal callbacks for the startup transaction.</returns>
    /// <remarks>Binding or IL-anchor mismatches reject the group. Creating the group does not enable graphics routing.</remarks>
    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner)
    {
        var harmony = new Harmony(Owner);
        bool attempted = false;
        return new StartupPatchGroup("graphics-post", () =>
        {
            if (Post.ReturnType != typeof(void) || Post.IsStatic || Post.GetMethodBody() == null ||
                Post.DeclaringType != typeof(ClientPlatformWindows))
                throw new InvalidOperationException("Official post-processing signature/body changed.");
            GameFrameBindings.Validate(); GameFramebufferBindings.Validate();
            if (Final.ReturnType != typeof(void) || Final.IsStatic || Final.GetMethodBody() == null ||
                Final.DeclaringType != typeof(ClientPlatformWindows))
                throw new InvalidOperationException("Official final composition signature/body changed.");
            if (Blit.ReturnType != typeof(void) || Blit.IsStatic || Blit.GetMethodBody() == null ||
                Blit.DeclaringType != typeof(ClientPlatformWindows))
                throw new InvalidOperationException("Official final blit signature/body changed.");
        }, () =>
        {
            if (runtime != null) throw new InvalidOperationException("Post-processing routing already has an owner.");
            runtime = owner; attempted = true;
            harmony.Patch(Post, prefix: new HarmonyMethod(typeof(PostProcessingConsumerPatches), nameof(Render)) { priority = Priority.First });
            harmony.Patch(Final, prefix: new HarmonyMethod(typeof(PostProcessingConsumerPatches), nameof(Compose)) { priority = Priority.First });
            harmony.Patch(Blit, prefix: new HarmonyMethod(typeof(PostProcessingConsumerPatches), nameof(PresentScene)) { priority = Priority.First });
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner);
            if (ReferenceEquals(runtime, owner)) runtime = null;
            attempted = false;
        });
    }
    private static bool Render(ClientPlatformWindows __instance, float[]? __0)
    {
        if (runtime?.Routing.RoutingEnabled != true) return true;
        if (!runtime.TrySession(__instance, out var session)) throw new InvalidOperationException("Active post routing lost its session.");
        session.RenderPostProcessing(__0);
        return false;
    }
    private static bool Compose(ClientPlatformWindows __instance)
    {
        if (runtime?.Routing.RoutingEnabled != true) return true;
        if (!runtime.TrySession(__instance, out var session)) throw new InvalidOperationException("Active final composition lost its session.");
        session.Graphics.RenderFinalComposition();
        return false;
    }
    private static bool PresentScene(ClientPlatformWindows __instance)
    {
        if (runtime?.Routing.RoutingEnabled != true) return true;
        if (!runtime.TrySession(__instance, out var session)) throw new InvalidOperationException("Active final blit lost its session.");
        session.Graphics.BlitPrimaryToDefault();
        return false;
    }
}
