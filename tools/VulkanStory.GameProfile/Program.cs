using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using VulkanStory.Bootstrap;
using VulkanStory.Game;

// CLI entry: verifies accepted installation hashes against the embedded startup
// IL profile, validates platform metadata, and writes a method-operand inventory.
// This metadata check does not launch the game or establish startup/render acceptance.
// Exit codes: 0 for a matching inventory, 1 for a captured failure, 2 for invalid arguments.
bool inspectPerformance = args.Length == 7 && args[6] == "--performance-patches";
if ((args.Length != 6 && !inspectPerformance) || args[0] != "--game-directory" || args[2] != "--profile" || args[4] != "--output")
{
    Console.Error.WriteLine("Usage: VulkanStory.GameProfile --game-directory <game dir> --profile <profile.json> --output <inventory.json> [--performance-patches]");
    return 2;
}

try
{
    string gameDirectory = Path.GetFullPath(args[1]);
    string profilePath = Path.GetFullPath(args[3]);
    string outputPath = Path.GetFullPath(args[5]);

    GameProfile profile = GameProfile.Load(profilePath);
    string profileId = profile.Id;
    if (profileId != StartupIlProfile.Id ||
        profile.Files["VintagestoryLib.dll"] != StartupIlProfile.GameAssemblySha256)
        throw new InvalidDataException("File profile and embedded startup IL profile do not agree.");
    profile.VerifyFiles(gameDirectory);

    AssemblyLoadContext.Default.Resolving += (_, name) =>
    {
        foreach (string directory in new[] { gameDirectory, Path.Combine(gameDirectory, "Lib"), Path.Combine(gameDirectory, "Mods") })
        {
            string path = Path.Combine(directory, name.Name + ".dll");
            if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        }
        return null;
    };

    string gameAssemblyPath = Path.Combine(gameDirectory, "VintagestoryLib.dll");
    string gameAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(gameAssemblyPath)));
    Assembly game = AssemblyLoadContext.Default.LoadFromAssemblyPath(gameAssemblyPath);
    IReadOnlyList<StartupMethodInventory> methods = StartupIlInventory.CaptureProfile1227(game);
    StartupIlProfile.VerifyProfile1227(methods, gameAssemblySha256);
    GamePlatformBindingProfile.Validate1227();

    var performance = new List<object>();
    int performanceFailures = 0;
    if (inspectPerformance)
    {
        Assembly essentials = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(gameDirectory, "Mods", "VSEssentials.dll"));
        Assembly survival = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(gameDirectory, "Mods", "VSSurvivalMod.dll"));
        performance = PerformanceInstallationCheck.Run(essentials, survival, out performanceFailures);
    }

    string? parent = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
    File.WriteAllText(outputPath, JsonSerializer.Serialize(new
    {
        profileId,
        gameAssemblySha256,
        methods,
        performance,
    }, new JsonSerializerOptions { WriteIndented = true }));

    foreach (StartupMethodInventory method in methods)
    {
        int windowUses = method.Uses.Count(use =>
            use.Member.Contains("Window", StringComparison.Ordinal) ||
            use.Member.Contains("GLFW", StringComparison.Ordinal) ||
            use.Member.Contains("ScreenManager", StringComparison.Ordinal));
        Console.WriteLine($"{method.Event}: IL={method.IlLength}, member operands={method.Uses.Count}, window-related={windowUses}");
    }
    Console.WriteLine("Matched pinned startup IL anchors and saved inventory: " + outputPath);
    Console.WriteLine("Matched original platform input/frame/close binding metadata.");
    return performanceFailures == 0 ? 0 : 1;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
