using System.Reflection;

namespace VulkanStory.Game;

/// <summary>Assembles complete platform, window, graphics, loop and shutdown coverage into one startup transaction.</summary>
internal static class StartupProfileComposition
{
    internal static GameStartupPlan Create1227(ProcessRuntime runtime, GameSessionServices services,
        Assembly essentials, Assembly survival)
        => Create1227(runtime, () => services, essentials, survival);
    internal static GameStartupPlan Create1227(ProcessRuntime runtime, Func<GameSessionServices> services,
        Assembly essentials, Assembly survival)
    {
        Func<bool> enabled = () => runtime.Routing.RoutingEnabled;
        return Create(runtime, services, ComposeGraphics(
        [
            TextureConsumerPatches.CreateSubset(enabled),
            ShaderConsumerPatches.CreateSubset(enabled),
            ShaderSourceConsumerPatches.CreateGroup(runtime),
            MeshConsumerPatches.CreateSubset(enabled),
            StateConsumerPatches.CreateSubset(enabled),
            FramebufferConsumerPatches.CreateSubset(enabled),
            QueriesCaptureConsumerPatches.CreateSubset(enabled),
            PlatformStartupRoutingPatches.CreateSubset(runtime),
            TemporalConsumerPatches.CreateGroup(runtime),
            MotionUniformConsumerPatches.CreateGroup(runtime),
            UiConsumerPatches.CreateGroup(runtime),
            MenuSettingsConsumerPatches.CreateGroup(enabled),
            ControllerHintConsumerPatches.CreateGroup(enabled),
            AnalogDirectionConsumerPatches.CreateGroup(enabled),
            AnalogClientConsumerPatches.CreateGroup(runtime),
            SceneProfileComposition.Create1227(runtime, essentials, survival),
            PostProcessingConsumerPatches.CreateGroup(runtime),
            RemainingPlatformConsumerPatches.CreateGroup(enabled),
        ]));
    }
    // Called by the supported version adapter after ALL ordinary, scene and
    // post GL coverage has been supplied. Resource subsets alone are rejected.
    internal static GameStartupPlan Create(ProcessRuntime runtime, GameSessionServices services, StartupPatchGroup completeGraphics)
        => Create(runtime, () => services, completeGraphics);
    internal static GameStartupPlan Create(ProcessRuntime runtime, Func<GameSessionServices> services, StartupPatchGroup completeGraphics)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(completeGraphics);
        if (completeGraphics.Name != "graphics-api")
            throw new InvalidOperationException("Startup requires a complete graphics-api group, not a partial resource subset.");
        return new GameStartupPlan(
        [
            StartupWindowRoutingPatches.CreateConstructionGroup(runtime),
            StartupWindowRoutingPatches.CreateWindowGroup(runtime),
            WindowConsumerPatches.CreateCoreGroup(() => runtime.Routing.RoutingEnabled),
            completeGraphics,
            FrameLoopRoutingPatches.CreateGroup(runtime),
            StartupWindowRoutingPatches.CreateShutdownGroup(runtime),
        ], services);
    }
    internal static StartupPatchGroup ComposeGraphics(IEnumerable<StartupPatchGroup> completeCoverage)
    {
        StartupPatchGroup[] groups = completeCoverage.ToArray();
        string[] required = ["graphics-textures", "graphics-shaders", "graphics-shader-sources", "graphics-meshes", "graphics-states",
            "graphics-framebuffers", "graphics-queries-capture", "graphics-platform-start", "graphics-platform-remaining", "graphics-temporal", "graphics-motion-uniforms", "graphics-ui", "graphics-menu-settings", "graphics-controller-hints", "graphics-controller-analog-direction", "graphics-controller-analog-client", "graphics-scene", "graphics-post"];
        var attempted = new List<StartupPatchGroup>();
        return new StartupPatchGroup("graphics-api", () =>
        {
            var names = groups.Select(group => group.Name).ToHashSet(StringComparer.Ordinal);
            if (names.Count != groups.Length || required.Any(name => !names.Contains(name)))
                throw new InvalidOperationException("Graphics profile is missing required scene/post/API coverage or repeats a group.");
            foreach (var group in groups) group.Validate();
        }, () =>
        {
            foreach (var group in groups) { attempted.Add(group); group.Install(); }
        }, () =>
        {
            var failures = new List<Exception>();
            for (int index = attempted.Count - 1; index >= 0; index--)
                try { attempted[index].Remove(); } catch (Exception error) { failures.Add(error); }
            attempted.Clear();
            if (failures.Count != 0) throw new AggregateException("Graphics subgroup rollback failed.", failures);
        });
    }
}
