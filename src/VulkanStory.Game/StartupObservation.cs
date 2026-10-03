using System.Reflection;
using HarmonyLib;

namespace VulkanStory.Game;

// B0 only observes startup; these prefixes never suppress or replace an original body.
public static class StartupObservation
{
    private const string Owner = "vulkanstory.bootstrap.observation";
    private static readonly Dictionary<MethodBase, string> events = new();
    private static Action<string, string>? trace;
    private static int installed;
    internal static event Action<string>? StageEntering;

    public static void Install(string gameDirectory, Action<string, string> write)
    {
        if (Interlocked.Exchange(ref installed, 1) != 0)
            throw new InvalidOperationException("Startup observation was already installed.");
        trace = write;
        Harmony? harmony = null;
        bool patchAttempted = false;
        try
        {
            Assembly game = Assembly.Load("VintagestoryLib");
            if (!string.Equals(Path.GetFullPath(game.Location), Path.GetFullPath(Path.Combine(gameDirectory, "VintagestoryLib.dll")), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Game assembly was loaded from an unexpected location.");

            StartupIlProfile.VerifyProfile1227(StartupIlInventory.CaptureProfile1227(game));

            // Bind everything before modifying any method. Never accept a parameter-count-only match.
            foreach (StartupTarget target in StartupTargets.Profile1227)
            {
                Type type = game.GetType(target.TypeName, throwOnError: true)!;
                events.Add(StartupTargets.Resolve(type, target), target.Event);
            }
            harmony = new Harmony(Owner);
            var prefix = new HarmonyMethod(typeof(StartupObservation).GetMethod(nameof(Before), BindingFlags.NonPublic | BindingFlags.Static)!) { priority = Priority.First };
            var postfix = new HarmonyMethod(typeof(StartupObservation).GetMethod(nameof(After), BindingFlags.NonPublic | BindingFlags.Static)!) { priority = Priority.Last };
            foreach ((MethodBase method, _) in events)
            {
                patchAttempted = true;
                harmony.Patch(method, prefix: prefix, postfix: postfix);
            }
            write("managed.patches.ready", $"profile=vs-1.22.7-win-x64; owner={Owner}; methods={events.Count}; observer-only");
        }
        catch (Exception failure)
        {
            events.Clear();
            trace = null;
            if (patchAttempted && harmony is not null)
            {
                try { harmony.UnpatchAll(Owner); }
                catch (Exception rollback) { throw new AggregateException("Observation patch installation and rollback failed.", failure, rollback); }
            }
            throw;
        }
    }

    private static void Before(MethodBase __originalMethod) => Record(__originalMethod, ".enter");
    private static void After(MethodBase __originalMethod) => Record(__originalMethod, ".return");

    private static void Record(MethodBase method, string phase)
    {
        // Patch callbacks must not change exception behavior of the original game.
        try
        {
            if (!events.TryGetValue(method, out string? name)) return;
            if (phase == ".enter") StageEntering?.Invoke(name);
            trace?.Invoke(name + phase, "");
            if (name == "game.screens.start" && phase == ".enter")
            {
                // The game's path/settings initialization has completed at this point.
                // Do not read GamePaths during patch discovery or earlier callbacks.
                Assembly? api = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "VintagestoryAPI");
                object? dataPath = api?.GetType("Vintagestory.API.Config.GamePaths")?.GetProperty(
                    "DataPath", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                trace?.Invoke("game.data_path", dataPath?.ToString() ?? "unavailable");
            }
        }
        catch { /* Trace failure is non-fatal. */ }
    }
}
