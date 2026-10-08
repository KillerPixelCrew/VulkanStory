using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.Client.NoObf;
using Xunit;

namespace VulkanStory.Game.Tests;

/// <summary>Checks official shader/UBO patch installation and rejection before ownerless GL operations.</summary>
/// <remarks>Does not create native shader programs or evaluate rendered output.</remarks>
public sealed class ShaderConsumerRoutingTests
{
    [Fact]
    public void ActualShaderPrefixesRejectMissingOwnerBeforeGlAndAreRemoved()
    {
        var platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
        MethodInfo location = typeof(ClientPlatformWindows).GetMethod("GetUniformLocation", [typeof(ShaderProgram), typeof(string)])!;
        MethodInfo use = typeof(ShaderProgramBase).GetMethod("Use", Type.EmptyTypes)!;
        MethodInfo stop = typeof(ShaderProgramBase).GetMethod("Stop", Type.EmptyTypes)!;
        MethodInfo scalar = typeof(ShaderProgramBase).GetMethod("Uniform", [typeof(string), typeof(float)])!;
        MethodInfo array = typeof(ShaderProgramBase).GetMethod("Uniforms4", [typeof(string), typeof(int), typeof(float[])])!;
        MethodInfo matrix = typeof(ShaderProgramBase).GetMethod("UniformMatrices", [typeof(string), typeof(int), typeof(float[])])!;
        MethodInfo sampler = typeof(ClientPlatformWindows).GetMethod("GenSampler", [typeof(bool)])!;
        MethodInfo texture = typeof(ShaderProgramBase).GetMethod("BindTexture2D", [typeof(string), typeof(int), typeof(int)])!;
        MethodInfo uboBind = typeof(UBO).GetMethod("Bind", Type.EmptyTypes)!;
        MethodInfo uboDispose = typeof(UBO).GetMethod("Dispose", Type.EmptyTypes)!;
        MethodInfo programDispose = typeof(ShaderProgramBase).GetMethod("Dispose", Type.EmptyTypes)!;
        bool enabled = true;
        var group = ShaderConsumerPatches.CreateSubset(() => enabled);
        group.Validate();
        try
        {
            group.Install();
            Assert.Contains("vulkanstory.routing.graphics-shaders", Harmony.GetPatchInfo(use)!.Owners);
            Assert.Contains("vulkanstory.routing.graphics-shaders", Harmony.GetPatchInfo(stop)!.Owners);
            Assert.Contains("vulkanstory.routing.graphics-shaders", Harmony.GetPatchInfo(scalar)!.Owners);
            Assert.Contains("vulkanstory.routing.graphics-shaders", Harmony.GetPatchInfo(array)!.Owners);
            Assert.Contains("vulkanstory.routing.graphics-shaders", Harmony.GetPatchInfo(matrix)!.Owners);
            Assert.Contains("vulkanstory.routing.graphics-shaders", Harmony.GetPatchInfo(sampler)!.Owners);
            Assert.Contains("vulkanstory.routing.graphics-shaders", Harmony.GetPatchInfo(texture)!.Owners);
            Assert.Contains("vulkanstory.routing.graphics-shaders", Harmony.GetPatchInfo(uboBind)!.Owners);
            Assert.Contains("vulkanstory.routing.graphics-shaders", Harmony.GetPatchInfo(uboDispose)!.Owners);
            Assert.Contains("vulkanstory.routing.graphics-shaders", Harmony.GetPatchInfo(programDispose)!.Owners);
            var buffer = (UBO)RuntimeHelpers.GetUninitializedObject(typeof(UBO));
            var missingBufferOwner = Assert.Throws<InvalidOperationException>(() => buffer.Bind());
            Assert.Contains("no renderer owner", missingBufferOwner.Message);
            buffer.Size = 8;
            Assert.Throws<ArgumentException>(() => buffer.Update<int>(1));
            var missingGenericOwner = Assert.Throws<InvalidOperationException>(() => buffer.Update<long>(1L));
            Assert.Contains("no renderer owner", missingGenericOwner.Message);
            var missingRangeOwner = Assert.Throws<InvalidOperationException>(() => buffer.Update<long>(1L, 0, 8));
            Assert.Contains("no renderer owner", missingRangeOwner.Message);
            var error = Assert.Throws<TargetInvocationException>(() => location.Invoke(platform, [null, "test"]));
            var cause = Assert.IsType<InvalidOperationException>(error.InnerException);
            Assert.Contains("no renderer adapter", cause.Message);
        }
        finally
        {
            enabled = false;
            group.Remove();
        }
        Assert.DoesNotContain("vulkanstory.routing.graphics-shaders", Harmony.GetPatchInfo(use)?.Owners ?? []);
        Assert.DoesNotContain("vulkanstory.routing.graphics-shaders", Harmony.GetPatchInfo(stop)?.Owners ?? []);
        Assert.DoesNotContain("vulkanstory.routing.graphics-shaders", Harmony.GetPatchInfo(scalar)?.Owners ?? []);
        Assert.DoesNotContain("vulkanstory.routing.graphics-shaders", Harmony.GetPatchInfo(array)?.Owners ?? []);
        Assert.DoesNotContain("vulkanstory.routing.graphics-shaders", Harmony.GetPatchInfo(matrix)?.Owners ?? []);
        Assert.DoesNotContain("vulkanstory.routing.graphics-shaders", Harmony.GetPatchInfo(sampler)?.Owners ?? []);
        Assert.DoesNotContain("vulkanstory.routing.graphics-shaders", Harmony.GetPatchInfo(texture)?.Owners ?? []);
        Assert.DoesNotContain("vulkanstory.routing.graphics-shaders", Harmony.GetPatchInfo(uboBind)?.Owners ?? []);
        Assert.DoesNotContain("vulkanstory.routing.graphics-shaders", Harmony.GetPatchInfo(uboDispose)?.Owners ?? []);
        Assert.DoesNotContain("vulkanstory.routing.graphics-shaders", Harmony.GetPatchInfo(programDispose)?.Owners ?? []);
    }
}
