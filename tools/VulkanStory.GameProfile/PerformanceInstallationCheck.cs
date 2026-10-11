using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using VulkanStory.Game.Performance;

internal static class PerformanceInstallationCheck
{
    // The entry point must register the official-game resolver BEFORE the JIT resolves Harmony/game dependencies.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static List<object> Run(Assembly essentials, Assembly survival, out int failures)
    {
        failures = 0;
        var results = new List<object>();
        var features = PerformanceFeatures.Create1227(essentials, survival);
        var installed = new List<(PerformanceFeature Feature, Harmony Owner)>();
        try
        {
            foreach (var feature in features)
            {
                var owner = new Harmony("vulkanstory.performance.inspection." + feature.Name);
                installed.Add((feature, owner));
                string? failure = null;
                try { feature.Validate(); feature.Install(owner); }
                catch (Exception error) { failure = error.ToString(); failures++; }
                results.Add(new { feature.Name, validatedAndInstalled = failure == null, failure });
                Console.WriteLine(feature.Name + ": " + (failure == null ? "validated and installed" : "FAILED: " + failure));
            }
        }
        finally
        {
            // This tool never calls game Main, creates graphics or runs a world.
            foreach (var (feature, owner) in installed.AsEnumerable().Reverse())
            {
                try { owner.UnpatchAll(owner.Id); feature.Uninstall?.Invoke(); }
                catch (Exception error) { Console.Error.WriteLine("Patch teardown failed: " + feature.Name + ": " + error); failures++; }
            }
        }
        return results;
    }
}
