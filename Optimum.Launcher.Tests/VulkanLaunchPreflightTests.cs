using System;
using System.IO;
using Optimum.Launcher;
using Xunit;

namespace Optimum.Launcher.Tests;

public sealed class VulkanLaunchPreflightTests
{
    [Theory]
    [InlineData("{\"Renderer\":\"vulkan\"}", true)]
    [InlineData("{\"Renderer\":\" VULKAN \"}", true)]
    [InlineData("{\"renderer\":\"vulkan\"}", false)]
    [InlineData("{\"Renderer\":\"opengl\",\"Renderer\":\"vulkan\"}", true)]
    [InlineData("{\"Renderer\":\"vulkan\",\"Renderer\":\"opengl\"}", false)]
    [InlineData("{\"Renderer\":\"auto\"}", false)]
    [InlineData("{\"Renderer\":\"opengl\"}", false)]
    [InlineData("{ broken", false)]
    public void OnlyExplicitVulkanRequestsPreflight(string json, bool expected)
    {
        string root = Directory.CreateTempSubdirectory("optimum-vulkan-preflight-").FullName;
        try
        {
            string config = Path.Combine(root, "ModConfig", "optimum.json");
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            File.WriteAllText(config, json);
            Assert.Equal(expected, VulkanLaunchPreflight.IsExplicitlyRequested(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MissingRendererReportsAnErrorBeforeGameCode()
    {
        string root = Directory.CreateTempSubdirectory("optimum-no-renderer-").FullName;
        try { Assert.Contains("Repair or reinstall", VulkanLaunchPreflight.Check(root)); }
        finally { Directory.Delete(root); }
    }

    [Fact]
    public void HeadlessRendererProbeCanRunAgainstAStagedRenderer()
    {
        string? directory = Environment.GetEnvironmentVariable("OPTIMUM_VULKAN_PREFLIGHT_SMOKE_DIR");
        if (directory is null) return;
        Assert.Null(VulkanLaunchPreflight.Check(directory));
    }
}
