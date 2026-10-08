// The runtime requires this exact type name, in the global namespace.
/// <summary>CoreCLR startup-hook entry that prepares VulkanStory before the original client entry point.</summary>
internal static class StartupHook
{
    /// <summary>Runs early bootstrap once and records ordinary failures as a bypass.</summary>
    /// <remarks>Headless bootstrap failures propagate so the harness cannot fall back to a visible client.</remarks>
    public static void Initialize()
    {
        try { VulkanStory.Bootstrap.BootstrapEntry.Initialize(); }
        catch (Exception error)
        {
            if (Environment.GetEnvironmentVariable("VULKANSTORY_HEADLESS") == "1") throw;
            // Last resort for failures before the regular diagnostic sink exists.
            AppContext.SetData("VulkanStory.Bootstrap.Status", "bypassed");
            System.Diagnostics.Debug.WriteLine("VulkanStory startup hook bypassed: " + error.Message);
        }
    }
}
