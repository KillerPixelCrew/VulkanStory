using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using Xunit;

namespace VulkanStory.Game.Tests;

public sealed class MeshConsumerRoutingTests
{
    [Fact]
    public void ActualMeshSubsetBindsInstallsRejectsOwnerlessUploadAndRemoves()
    {
        var platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
        var upload = typeof(ClientPlatformWindows).GetMethod("UploadMesh", [typeof(MeshData)])!;
        var dispose = typeof(VAO).GetMethod("Dispose", Type.EmptyTypes)!;
        var ssboUpdate = typeof(ClientPlatformWindows).GetMethod("UpdateSSBOMesh", [typeof(MeshRef), typeof(MeshData)])!;
        var draw = typeof(ClientPlatformWindows).GetMethod("RenderMesh", [typeof(MeshRef)])!;
        bool enabled = false;
        var group = MeshConsumerPatches.CreateSubset(() => enabled);
        group.Validate();
        try
        {
            group.Install();
            Assert.Contains("vulkanstory.routing.graphics-meshes", Harmony.GetPatchInfo(upload)!.Owners);
            Assert.Contains("vulkanstory.routing.graphics-meshes", Harmony.GetPatchInfo(dispose)!.Owners);
            Assert.Contains("vulkanstory.routing.graphics-meshes", Harmony.GetPatchInfo(ssboUpdate)!.Owners);
            Assert.Contains("vulkanstory.routing.graphics-meshes", Harmony.GetPatchInfo(draw)!.Owners);
            enabled = true;
            var error = Assert.Throws<TargetInvocationException>(() => upload.Invoke(platform, [null]));
            Assert.Contains("no renderer adapter", Assert.IsType<InvalidOperationException>(error.InnerException).Message);
            var drawError = Assert.Throws<TargetInvocationException>(() => draw.Invoke(platform, [null]));
            Assert.Contains("no renderer adapter", Assert.IsType<InvalidOperationException>(drawError.InnerException).Message);
        }
        finally { enabled = false; group.Remove(); }
        Assert.DoesNotContain("vulkanstory.routing.graphics-meshes", Harmony.GetPatchInfo(upload)?.Owners ?? []);
        Assert.DoesNotContain("vulkanstory.routing.graphics-meshes", Harmony.GetPatchInfo(dispose)?.Owners ?? []);
        Assert.DoesNotContain("vulkanstory.routing.graphics-meshes", Harmony.GetPatchInfo(ssboUpdate)?.Owners ?? []);
        Assert.DoesNotContain("vulkanstory.routing.graphics-meshes", Harmony.GetPatchInfo(draw)?.Owners ?? []);
    }
}
