using System.Reflection;

namespace VulkanStory.Game;

// Scene coverage for the pinned original renderer/content paths. General platform
// API coverage remains a separate required group in the version profile.
internal static class SceneProfileComposition
{
    internal static StartupPatchGroup Create1227(ProcessRuntime runtime, Assembly essentials, Assembly survival)
    {
        ArgumentNullException.ThrowIfNull(essentials);
        ArgumentNullException.ThrowIfNull(survival);
        if (essentials.GetName().Name != "VSEssentials" || survival.GetName().Name != "VSSurvivalMod")
            throw new InvalidOperationException("Scene routing requires the original Essentials and Survival assemblies.");
        return Compose(
        [
            TransparencyConsumerPatches.CreateGroup(runtime),
            SkyConsumerPatches.CreateGroup(runtime),
            CelestialConsumerPatches.CreateGroup(runtime),
            ParticleConsumerPatches.CreateGroup(runtime),
            DecalConsumerPatches.CreateGroup(runtime),
            ChunkConsumerPatches.CreateGroup(runtime),
            CloudConsumerPatches.CreateGroup(runtime, essentials),
            CloudMapConsumerPatches.CreateGroup(runtime, essentials),
            EntityConsumerPatches.CreateGroup(runtime),
            HeldItemMotionConsumerPatches.CreateGroup(runtime, essentials),
            InstanceMotionConsumerPatches.CreateGroup(runtime, survival),
            RigidContentMotionConsumerPatches.CreateGroup(runtime, survival),
            DroppedFallingMotionConsumerPatches.CreateGroup(runtime, essentials),
            DirectAnimatedMotionConsumerPatches.CreateGroup(runtime, survival),
            SceneRawStateConsumerPatches.CreateGroup(runtime, survival),
        ]);
    }
    private static StartupPatchGroup Compose(StartupPatchGroup[] groups)
    {
        string[] required =
        [
            "graphics-scene-transparency", "graphics-scene-sky", "graphics-scene-celestial",
            "graphics-scene-particles", "graphics-scene-decals", "graphics-scene-chunks",
            "graphics-scene-cloud-volumetric", "graphics-scene-cloud-map", "graphics-scene-entities",
            "graphics-motion-held-items", "graphics-motion-instances", "graphics-motion-rigid-content",
            "graphics-motion-dropped-falling", "graphics-motion-direct-animated", "graphics-scene-raw-state",
        ];
        var attempted = new List<StartupPatchGroup>();
        return new StartupPatchGroup("graphics-scene", () =>
        {
            var names = groups.Select(group => group.Name).ToHashSet(StringComparer.Ordinal);
            if (names.Count != groups.Length || !names.SetEquals(required))
                throw new InvalidOperationException("Pinned scene coverage is incomplete or repeats a group.");
            // All guards run before any leaf installs; actual incoming Harmony
            // instructions are checked by the leaf transpilers during installation.
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
            if (failures.Count != 0) throw new AggregateException("Scene subgroup rollback failed.", failures);
        });
    }
}
