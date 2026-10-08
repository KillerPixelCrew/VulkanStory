using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace VulkanStory.Bootstrap;

/// <summary>Validates the Windows client profile and installs managed integration before game startup.</summary>
/// <remarks>Graphics ownership remains with the original startup path until the integration commits routing.</remarks>
internal static class BootstrapEntry
{
    private static int started;
    private static BootstrapDependencies? dependencies;

    /// <summary>Attempts process-local bootstrap once, installs dependency resolution, and invokes the integration entry.</summary>
    /// <remarks>Ordinary failures unregister this resolver and publish a bypass; headless failures are rethrown.</remarks>
    internal static void Initialize()
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        BootstrapTrace trace = BootstrapTrace.Create();
        trace.Write("managed.bootstrap.enter", "Early runtime entry; graphics ownership follows complete profile preparation.");
        AppContext.SetData("VulkanStory.Bootstrap.Status", "initializing");
        try
        {
            string ownPath = typeof(BootstrapEntry).Assembly.Location;
            StartupHookEnvironment.RemoveOwnHook(ownPath);
            string gameDirectory = Path.GetFullPath(AppContext.BaseDirectory);
            string? executable = Path.GetFileName(Environment.ProcessPath);
            bool harness = Environment.GetEnvironmentVariable("VULKANSTORY_HEADLESS") == "1" &&
                string.Equals(executable, "dotnet.exe", StringComparison.OrdinalIgnoreCase) &&
                Assembly.GetEntryAssembly()?.GetName().Name == "Vintagestory";
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
                (!string.Equals(executable, "Vintagestory.exe", StringComparison.OrdinalIgnoreCase) && !harness))
            {
                SetStatus(trace, "bypassed", "Not the supported Windows x64 client process.");
                return;
            }

            string managedDirectory = Path.GetDirectoryName(ownPath)!;
            GameProfile profile = GameProfile.Load(Path.Combine(managedDirectory, "profiles", "vs-1.22.7-win-x64.json"));
            if (Environment.Version.Major != profile.RuntimeMajor)
                throw new InvalidDataException($"Expected .NET {profile.RuntimeMajor}, found {Environment.Version}.");
            profile.VerifyFiles(gameDirectory);
            trace.Write("managed.profile.accepted", profile.Id);

            dependencies = new BootstrapDependencies(managedDirectory, gameDirectory, trace.Write);
            dependencies.Register();
            string integrationPath = Path.Combine(managedDirectory, "VulkanStory.Game.dll");
            if (AssemblyName.GetAssemblyName(integrationPath).Version != typeof(BootstrapEntry).Assembly.GetName().Version)
                throw new InvalidDataException("Bootstrap and game integration payload versions differ.");
            Assembly integration = AssemblyLoadContext.Default.LoadFromAssemblyPath(integrationPath);
            MethodInfo install = integration.GetType("VulkanStory.Game.RuntimeBootstrap", throwOnError: true)!
                .GetMethod("Install", BindingFlags.Public | BindingFlags.Static,
                    new[] { typeof(string), typeof(Action<string, string>) })
                ?? throw new MissingMethodException("VulkanStory.Game.RuntimeBootstrap.Install");
            install.Invoke(null, new object[] { gameDirectory, (Action<string, string>)trace.Write });
            SetStatus(trace, "observing", "Process runtime loaded; graphics activate only with a complete registered startup profile.");
            AppDomain.CurrentDomain.ProcessExit += (_, _) => trace.Write("managed.process.exit", "");
        }
        catch (Exception error)
        {
            dependencies?.Unregister();
            Exception cause = (error as TargetInvocationException)?.InnerException ?? error;
            SetStatus(trace, "bypassed", cause.ToString());
            // A harness must not fall back to an ordinary visible OpenGL client.
            if (Environment.GetEnvironmentVariable("VULKANSTORY_HEADLESS") == "1")
                throw new InvalidOperationException("Headless bootstrap failed; refusing visible fallback.", cause);
        }
    }

    /// <summary>Publishes the bootstrap state for the late mod entry and appends its diagnostic marker.</summary>
    private static void SetStatus(BootstrapTrace trace, string status, string reason)
    {
        AppContext.SetData("VulkanStory.Bootstrap.Status", status);
        trace.Write("managed.bootstrap." + status, reason);
    }
}
