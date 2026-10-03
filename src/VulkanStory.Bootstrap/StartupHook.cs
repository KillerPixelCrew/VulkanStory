// The runtime requires this exact type name, in the global namespace.
internal static class StartupHook
{
    public static void Initialize()
    {
        try { VulkanStory.Bootstrap.BootstrapEntry.Initialize(); }
        catch (Exception error)
        {
            // Last resort for failures before the regular diagnostic sink exists.
            AppContext.SetData("VulkanStory.Bootstrap.Status", "bypassed");
            System.Diagnostics.Debug.WriteLine("VulkanStory startup hook bypassed: " + error.Message);
        }
    }
}
