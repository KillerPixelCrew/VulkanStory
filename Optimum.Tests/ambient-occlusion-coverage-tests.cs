using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Source coverage for the Vulkan ambient occlusion (docs/research/ambient-occlusion.md section C
/// and the section E decisions): the three-way setting and its backend rule, the post-chain seam
/// that lets the platform replace vanilla SSAO and composes before the TAA resolve, the shader
/// prefix and the class-channel writes that compile in only under it, the opt-in debug outputs,
/// the patcher listings and the licence notices of the ported compute shaders.
///
/// Text assertions prove the wiring exists; Optimum.Render.Vulkan.Tests/AmbientOcclusionTests
/// and SceneSsaoTests prove the numbers.
/// </summary>
public class AmbientOcclusionCoverageTests
{
    // ------------------------------------------------------------------ the setting

    [Fact]
    public void TheSettingDefaultsToAutoMediumAndPersistsThroughOptimumJson()
    {
        string config = Read("VintagestoryApi/Config/OptimumConfig.cs");
        Assert.Contains("public static string AmbientOcclusion = \"auto\";", config);
        Assert.Contains("public static string AmbientOcclusionPreset = \"medium\";", config);
        Assert.Contains("public string AmbientOcclusion { get; set; } = \"auto\";", config);
        Assert.Contains("public string AmbientOcclusionPreset { get; set; } = \"medium\";", config);
        Assert.Contains("AmbientOcclusion = AmbientOcclusion,", config);
        Assert.Contains("AmbientOcclusionPreset = AmbientOcclusionPreset,", config);
        Assert.Contains("(nameof(OptimumConfigData.AmbientOcclusion), AmbientOcclusion)", config);
        Assert.Contains("(nameof(OptimumConfigData.AmbientOcclusionPreset), AmbientOcclusionPreset)", config);
        // An unrecognised value degrades to the default instead of failing the file.
        Assert.Contains("AmbientOcclusion = requestedAo is \"vanilla\" or \"gtao\" ? requestedAo : \"auto\";", config);
        Assert.Contains("AmbientOcclusionPreset = requestedAoPreset is \"low\" or \"high\" or \"ultra\" ? requestedAoPreset : \"medium\";", config);
    }

    [Fact]
    public void AutoIsGtaoOnVulkanWithTaaVanillaOtherwiseAndOpenGlIgnoresTheSetting()
    {
        string saved = OptimumConfig.AmbientOcclusion;
        try
        {
            OptimumConfig.AmbientOcclusion = "auto";
            Assert.True(OptimumConfig.GtaoSelected(vulkanBackend: true, taaActive: true));
            Assert.False(OptimumConfig.GtaoSelected(vulkanBackend: true, taaActive: false));
            Assert.False(OptimumConfig.GtaoSelected(vulkanBackend: false, taaActive: true));
            Assert.False(OptimumConfig.GtaoSelected(vulkanBackend: false, taaActive: false));

            OptimumConfig.AmbientOcclusion = "vanilla";
            Assert.False(OptimumConfig.GtaoSelected(vulkanBackend: true, taaActive: true));
            Assert.False(OptimumConfig.GtaoSelected(vulkanBackend: true, taaActive: false));
            Assert.False(OptimumConfig.GtaoSelected(vulkanBackend: false, taaActive: true));

            OptimumConfig.AmbientOcclusion = "gtao";
            Assert.True(OptimumConfig.GtaoSelected(vulkanBackend: true, taaActive: true));
            Assert.True(OptimumConfig.GtaoSelected(vulkanBackend: true, taaActive: false));
            // OpenGL stays vanilla SSAO whatever is asked for.
            Assert.False(OptimumConfig.GtaoSelected(vulkanBackend: false, taaActive: true));
            Assert.False(OptimumConfig.GtaoSelected(vulkanBackend: false, taaActive: false));

            OptimumConfig.AmbientOcclusion = "GTAO";
            Assert.True(OptimumConfig.GtaoSelected(vulkanBackend: true, taaActive: false));
        }
        finally
        {
            OptimumConfig.AmbientOcclusion = saved;
        }

        // The effective form reads the backend that actually started and the TAA actually in effect.
        Assert.Contains("public static bool EffectiveGtao => GtaoSelected(OptimumRender.IsVulkan, EffectiveTaa);",
            Read("VintagestoryApi/Config/OptimumConfig.cs"));
    }

    // ------------------------------------------------------------------ the post chain

    [Fact]
    public void ThePlatformReplacesVanillaSsaoAndComposesBeforeTheResolve()
    {
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        string post = Between(platform, "public override void RenderPostprocessingEffects", "public override void ClearSsaoTarget");

        int reset = post.IndexOf("optimumAmbientOcclusionTexture = 0;", StringComparison.Ordinal);
        int ask = post.IndexOf("optimumAmbientOcclusionTexture = RenderOptimumAmbientOcclusion(projectMatrix);", StringComparison.Ordinal);
        int vanillaGuard = post.IndexOf("if (optimumAmbientOcclusionTexture == 0 && RenderSSAO && projectMatrix != null)", StringComparison.Ordinal);
        int ssao = post.IndexOf("ssao.Use();", StringComparison.Ordinal);
        int gtaoGuard = post.IndexOf("if (optimumAmbientOcclusionTexture != 0)", StringComparison.Ordinal);
        int compose = post.IndexOf("ApplyOptimumSceneSsao();", gtaoGuard, StringComparison.Ordinal);
        int resolve = post.IndexOf("RenderOptimumTaaResolve();", StringComparison.Ordinal);
        Assert.True(reset >= 0 && reset < ask, "the texture is cleared before the platform is asked");
        Assert.True(ask < vanillaGuard && vanillaGuard < ssao, "vanilla SSAO runs only when the platform returned nothing");
        Assert.True(ssao < gtaoGuard && gtaoGuard < compose && compose < resolve,
            "the platform's AO is composed after the vanilla block and before the resolve");
        Assert.Contains("if (RenderSSAO && projectMatrix != null)\n\t\t{\n\t\t\toptimumAmbientOcclusionTexture = RenderOptimumAmbientOcclusion(projectMatrix);",
            post.Replace("\r\n", "\n"));

        // The neutral body is the OpenGL path: 0, vanilla SSAO. ClientPlatformWindows does not override it.
        string abstractPlatform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs");
        Assert.Contains("public virtual int RenderOptimumAmbientOcclusion(float[] projectMatrix)\n\t{\n\t\treturn 0;\n\t}",
            abstractPlatform.Replace("\r\n", "\n"));
        Assert.Contains("public virtual int OptimumAmbientOcclusionDebugTexture(int index)\n\t{\n\t\treturn 0;\n\t}",
            abstractPlatform.Replace("\r\n", "\n"));
        Assert.DoesNotContain("override int RenderOptimumAmbientOcclusion", platform);

        // The compose binds the platform's texture, and the attenuation inputs only for the OPTIMUMAO shaders.
        string apply = Between(platform, "private void ApplyOptimumSceneSsao()", "public override void RenderFinalComposition");
        Assert.Contains("composite.BindTexture2D(\"ssaoScene\", optimumAmbientOcclusionTexture, 0);", apply);
        Assert.Contains("composite.BindTexture2D(\"ssaoScene\", frameBuffers[14].ColorTextureIds[0], 0);", apply);
        Assert.Contains("if (OptimumConfig.AmbientOcclusionShadersUseGtao)", apply);
        Assert.Contains("composite.BindTexture2D(\"gPositionScene\", frameBuffers[0].ColorTextureIds[3], 1);", apply);
        Assert.Contains("composite.BindTexture2D(\"revealageScene\", frameBuffers[1].ColorTextureIds[1], 2);", apply);
        Assert.Contains("optimumSsaoInScene = true;", apply);

        // Never on glow: the pass keeps colour 0 alone and reads the AO and attenuation inputs.
        string graph = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Graph.cs");
        string declaration = Between(graph, "private void DeclareFinalCompositionPass()", "private int FrameBufferIndexOf");
        Assert.Contains("ColorSlots = 1u", declaration);
        Assert.Contains("reads.Add(ambientOcclusionOutput);", declaration);
        Assert.Contains("AddColour(reads, PrimaryIndex, 3);", declaration);
        Assert.Contains("AddColour(reads, TransparentIndex, 1);", declaration);

        // The Vulkan platform runs GTAO only for shaders built with it, never a half-res min hack.
        string vulkan = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.AmbientOcclusion.cs");
        Assert.Contains("public override int RenderOptimumAmbientOcclusion(float[] projectMatrix)", vulkan);
        Assert.Contains("if (!OptimumConfig.AmbientOcclusionShadersUseGtao) return 0;", vulkan);
        Assert.Contains("OptimumTemporal.Frame.FrameIndex", vulkan);
        Assert.Contains("bool temporal = OptimumConfig.EffectiveTaa && TaaTargetsReady;", vulkan);
    }

    [Fact]
    public void TheShaderPrefixStampsOptimumAoFromTheBackendAndTheGBuffer()
    {
        string registry = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs");
        string prefixes = Between(registry, "private static void registerDefaultShaderCodePrefixes", "private static string HandleIncludes");
        Assert.Contains("bool optimumAo = OptimumConfig.EffectiveGtao && ClientSettings.SSAOQuality > 0;", prefixes);
        Assert.Contains("OptimumConfig.AmbientOcclusionShadersUseGtao = optimumAo;", prefixes);
        Assert.Contains("taaDefines = taaDefines + \"#define OPTIMUMAO \" + (optimumAo ? 1 : 0) + \"\\r\\n\";", prefixes);
        Assert.True(prefixes.IndexOf("#define OPTIMUMAO", StringComparison.Ordinal) <
                    prefixes.IndexOf("taaFrag.PrefixCode = taaFrag.PrefixCode + taaDefines;", StringComparison.Ordinal),
            "the define rides on the TAA defines both stages receive");
    }

    // ------------------------------------------------------------------ the class channel

    [Theory]
    [InlineData("sources/shaders/chunkopaque.fsh", "outGNormal.w = 1.0;", 2, new[] { "SSAOLEVEL > 0", "OPTIMUMAO > 0" })]
    [InlineData("sources/shaders/standard.fsh", "outGNormal.w = -1.0;", 1, new[] { "ALLOWDEPTHOFFSET > 0", "SSAOLEVEL > 0", "OPTIMUMAO > 0" })]
    [InlineData("sources/shaders/entityanimated.fsh", "outGNormal.w = -1.0;", 1, new[] { "ALLOWDEPTHOFFSET > 0", "USEOIT==0 && SSAOLEVEL > 0", "OPTIMUMAO > 0" })]
    public void ClassChannelWritesCompileInOnlyUnderTheirGuards(string path, string write, int expected, string[] guards)
    {
        string shader = Read(path);
        int found = 0;
        var stack = new List<string>();
        foreach (string raw in shader.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("#if", StringComparison.Ordinal)) stack.Add(line);
            else if (line.StartsWith("#endif", StringComparison.Ordinal)) stack.RemoveAt(stack.Count - 1);
            else if (line.StartsWith("#else", StringComparison.Ordinal) || line.StartsWith("#elif", StringComparison.Ordinal))
                stack[^1] = "#else of " + stack[^1];
            else if (line.Contains(write, StringComparison.Ordinal))
            {
                found++;
                foreach (string guard in guards)
                {
                    Assert.True(stack.Any(s => s.StartsWith("#if", StringComparison.Ordinal) && s.Contains(guard, StringComparison.Ordinal)),
                        path + ": '" + write + "' is not inside #if " + guard + " (open: " + string.Join(" | ", stack) + ")");
                }
            }
        }
        Assert.Equal(expected, found);
        // Every OPTIMUMAO block is additive: nothing vanilla sits in an #else of it.
        Assert.DoesNotContain("#else", Regex.Matches(shader, @"#if OPTIMUMAO > 0[\s\S]*?#endif").Select(m => m.Value).FirstOrDefault() ?? "");
    }

    [Fact]
    public void ThePlantFlagIsTheNoCullOpaquePassAndTheComposeDropsTheRowMin()
    {
        string chunk = Read("sources/shaders/chunkopaque.fsh");
        Assert.Equal(2, Regex.Matches(chunk, @"if \(haxyFade > 0\) outGNormal\.w = 1\.0;").Count);
        // ChunkRenderer sets HaxyFade = 1 exactly for the OpaqueNoCull pool (plants, grass, cross-quads).
        string renderer = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs");
        string opaque = Between(renderer, "public void RenderOpaque(float dt)", "ScreenManager.FrameProfiler.Mark(\"rend3D-ret-opnc\");");
        Assert.Matches(new Regex(@"chunkopaque\.HaxyFade = 1;\s*for \(int l = 0; l < textureIds\.Length; l\+\+\)\s*\{[^}]*poolsByRenderPass\[1\]"), opaque);

        string compose = Read("sources/shaders/scene-ssao.fsh").Replace("\r\n", "\n");
        Assert.Contains("#if OPTIMUMAO > 0\n    if (optimumAoMode == 1)", compose);
        Assert.Contains("ao = texelFetch(ssaoScene, texel, 0).r;", compose);
        Assert.Contains("max(0.0, 1.0 - texelFetch(revealageScene, texel, 0).r) * 0.75", compose);
        Assert.Contains("ao = 1.0 - (1.0 - ao) * (1.0 - attenuate);", compose);
        // The albedo hook exists, compiled out in the first version.
        Assert.Contains("#if OPTIMUMAO_MULTIBOUNCE > 0", compose);
        Assert.Contains("uniform sampler2D aoAlbedo;", compose);
        Assert.DoesNotContain("OPTIMUMAO_MULTIBOUNCE", Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs"));
    }

    // ------------------------------------------------------------------ outputs, patcher, licences

    [Fact]
    public void TheDebugOutputsAreOptInInTheParityDumpAndTheHeadlessHarness()
    {
        string device = Read("VintagestoryApi/Client/optimum-render-device.cs");
        Assert.Contains("public static readonly bool AmbientOcclusionOutputs = ResolveAmbientOcclusionOutputs();", device);
        Assert.Contains("Environment.GetEnvironmentVariable(\"OPTIMUM_AO_OUTPUTS\")", device);

        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        string dump = Between(platform, "private void OptimumRunParityDump()", "private string OptimumParitySlotName(int slot)");
        Assert.Contains("if (OptimumParityDump.AmbientOcclusionOutputs)", dump);
        Assert.Contains("OptimumAmbientOcclusionDebugTexture(aoIndex)", dump);
        string names = Between(platform, "private string OptimumParitySlotName(int slot)", "private int OptimumParityDumpAttachment");
        foreach (string name in new[] { "OptimumAoWorking", "OptimumAoEdges", "OptimumAoDepthMip0", "OptimumAoOutput" })
            Assert.Contains("return \"" + name + "\";", names);
        string capture = Between(platform, "private void OptimumHeadlessCaptureFrame(long worldFrame)", "private void OptimumRunParityDump()");
        Assert.Contains("OptimumHeadlessWriteAmbientOcclusion(worldFrame);", capture);
        // Through the dump's single readback and writer call site, into the frame directory.
        Assert.Contains("OptimumParityDumpAttachment(OptimumHeadless.FrameDirectory, aoSlot, OptimumParitySlotName(aoSlot), attachment, aoTexture);", capture);

        string vulkan = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.AmbientOcclusion.cs");
        string debug = Between(vulkan, "public override int OptimumAmbientOcclusionDebugTexture(int index)", "private GtaoSettings AmbientOcclusionSettings");
        Assert.True(debug.IndexOf("WorkingTermTexture", StringComparison.Ordinal) < debug.IndexOf("EdgesTexture", StringComparison.Ordinal) &&
                    debug.IndexOf("EdgesTexture", StringComparison.Ordinal) < debug.IndexOf("WorkingDepthTexture", StringComparison.Ordinal) &&
                    debug.IndexOf("WorkingDepthTexture", StringComparison.Ordinal) < debug.IndexOf("OutputTexture", StringComparison.Ordinal),
            "the Vulkan override answers in the slot order the dump names");
    }

    [Fact]
    public void EveryChangedMemberIsListedForTheCecilTransplant()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");
        foreach (string member in new[]
        {
            "RenderOptimumAmbientOcclusion", "OptimumAmbientOcclusionDebugTexture", "optimumAmbientOcclusionTexture",
            "OptimumAoWorkingSlot", "OptimumAoEdgesSlot", "OptimumAoDepthSlot", "OptimumAoOutputSlot", "OptimumAoOutputCount",
            "OptimumHeadlessWriteAmbientOcclusion",
            // Already transplanted members whose bodies changed.
            "ApplyOptimumSceneSsao", "OptimumRunParityDump", "OptimumParitySlotName", "OptimumHeadlessCaptureFrame",
        })
        {
            Assert.Contains("\"" + member + "\"", patcher);
        }
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"RenderPostprocessingEffects\", 1)", patcher);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ShaderRegistry\", \"registerDefaultShaderCodePrefixes\", 2)", patcher);

        string expected = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.cs");
        Assert.Contains("new(true, \"RenderOptimumAmbientOcclusion\", new[] { \"Single[]\" }),", expected);
        Assert.Contains("new(true, \"OptimumAmbientOcclusionDebugTexture\", new[] { \"Int32\" }),", expected);
    }

    [Fact]
    public void TheComputeShadersCarryTheirLicenceNoticesAndShipInsideTheRenderer()
    {
        const string permission = "Permission is hereby granted, free of charge, to any person obtaining a copy";
        foreach (string file in new[] { "common.glsl", "prefilter.comp", "main.comp", "denoise.comp" })
        {
            string source = Read("sources/shaders-vk/gtao/" + file);
            Assert.Contains("Copyright (C) 2016-2021, Intel Corporation", source);
            Assert.Contains("https://github.com/GameTechDev/XeGTAO", source);
            Assert.Contains(permission, source);
        }
        string main = Read("sources/shaders-vk/gtao/main.comp");
        Assert.Contains("https://github.com/bevyengine/bevy", main);
        Assert.Contains("Copyright (c) 2016, Intel Corporation", main); // ASSAO's normal-based edges, via Godot
        Assert.Contains("clayjohn: convert to Vulkan and Godot", main);

        string project = Read("Optimum.Render.Vulkan/Optimum.Render.Vulkan.csproj");
        Assert.Contains("<EmbeddedResource Include=\"..\\sources\\shaders-vk\\gtao\\*.comp;..\\sources\\shaders-vk\\gtao\\*.glsl\">", project);
        Assert.Contains("<LogicalName>shaders-vk/gtao/%(Filename)%(Extension)</LogicalName>", project);
        // No shipped noise texture in the first version (E.2.2): the Hilbert table is generated.
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Root(), "sources", "shaders-vk", "gtao"))
            .Where(f => !f.EndsWith(".comp", StringComparison.Ordinal) && !f.EndsWith(".glsl", StringComparison.Ordinal)));
    }

    // ------------------------------------------------------------------ the master switch

    [Fact]
    public void TheMasterSwitchDefaultsToOnAndPersistsThroughOptimumJson()
    {
        string config = Read("VintagestoryApi/Config/OptimumConfig.cs");
        Assert.Contains("public static bool AmbientOcclusionEnabled = true;", config);
        Assert.Contains("public bool AmbientOcclusionEnabled { get; set; } = true;", config);
        Assert.Contains("AmbientOcclusionEnabled = data.AmbientOcclusionEnabled;", config);
        Assert.Contains("AmbientOcclusionEnabled = AmbientOcclusionEnabled,", config);
        Assert.Contains("(nameof(OptimumConfigData.AmbientOcclusionEnabled), AmbientOcclusionEnabled.ToString())", config);
    }

    [Fact]
    public void TheMasterSwitchGatesBothAoPathsThroughRenderSsao()
    {
        // Vanilla SSAO and RenderOptimumAmbientOcclusion hang off the same condition, so gating
        // RenderSSAO switches off AO on both backends and in both modes.
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        Assert.Contains(
            "RenderSSAO = ClientSettings.SSAOQuality > 0 && base.DoPostProcessingEffects && OptimumConfig.AmbientOcclusionEnabled;",
            platform);
        // SetupSSAO is deliberately not gated: the G-buffer and its frame buffers stay, which is
        // what lets the switch flip without a rebuild.
        Assert.Contains("SetupSSAO = ClientSettings.SSAOQuality > 0;", platform);
        Assert.DoesNotContain("SetupSSAO = ClientSettings.SSAOQuality > 0 && OptimumConfig.AmbientOcclusionEnabled", platform);
    }

    [Fact]
    public void TheMasterSwitchIsAnOptimumTabSwitchWiredToTheConfig()
    {
        string gui = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs");
        Assert.Contains("Lang.Get(\"optimum-ao\")", gui);
        Assert.Contains("Lang.Get(\"optimum-ao-tooltip\")", gui);
        Assert.Contains("AddSwitch(onOptimumAmbientOcclusionChanged", gui);
        Assert.Contains("\"optAo\")", gui);
        Assert.Contains("composer.GetSwitch(\"optAo\").SetValue(Vintagestory.API.Config.OptimumConfig.AmbientOcclusionEnabled);", gui);

        string handler = Between(gui, "private void onOptimumAmbientOcclusionChanged(bool on)", "\n\t}");
        Assert.Contains("OptimumConfig.AmbientOcclusionEnabled = on;", handler);
        Assert.Contains("OptimumConfig.Save();", handler);
        // The point of the switch is the live A/B: no reload, no rebuild, no temporal reset.
        Assert.DoesNotContain("ReloadShaders", handler);
        Assert.DoesNotContain("RebuildFrameBuffers", handler);
        Assert.DoesNotContain("RequestReset", handler);
    }

    [Fact]
    public void TheMasterSwitchHasItsLangEntriesAndItsPatcherListing()
    {
        string lang = Read("sources/lang/en.json");
        Assert.Contains("\"optimum-ao\":", lang);
        Assert.Contains("\"optimum-ao-tooltip\":", lang);
        Assert.Contains("\"onOptimumAmbientOcclusionChanged\"", Read("Optimum.Patcher/Program.cs"));
    }

    // ------------------------------------------------------------------ the debug view

    [Fact]
    public void SwitchingAoOffAlsoStopsFinalFromMultiplyingByTheStaleSsaoTarget()
    {
        // The regression this pins: the scene shaders are still compiled with SSAOLEVEL > 0, so
        // final.fsh multiplies by ssaoScene unless optimumSsaoInScene says otherwise. With AO off
        // nothing renders into that target, so an unguarded multiply darkens the whole image.
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        Assert.Contains(
            "final.Uniform(\"optimumSsaoInScene\", (optimumSsaoInScene || !RenderSSAO) ? 1 : 0);",
            platform);
    }

    [Fact]
    public void TheDebugViewShowsWhicheverAoRanAndOnlyWhileAoIsOn()
    {
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        Assert.Contains("bool optimumAoDebugView = OptimumConfig.AmbientOcclusionDebugView && RenderSSAO;", platform);
        // GTAO's own visibility texture when it produced one, the vanilla blurred target otherwise.
        Assert.Contains("optimumAoDebugView && optimumAmbientOcclusionTexture != 0", platform);
        Assert.Contains("final.Uniform(\"optimumAoDebug\", optimumAoDebugView ? 1 : 0);", platform);

        string config = Read("VintagestoryApi/Config/OptimumConfig.cs");
        Assert.Contains("public static bool AmbientOcclusionDebugView = false;", config);
        Assert.Contains("public bool AmbientOcclusionDebugView { get; set; } = false;", config);
        Assert.Contains("AmbientOcclusionDebugView = data.AmbientOcclusionDebugView;", config);
        Assert.Contains("AmbientOcclusionDebugView = AmbientOcclusionDebugView,", config);
    }

    [Fact]
    public void TheDebugBranchIsTheSameInBothShaderTwins()
    {
        // The native program and the GLSL 330 override must agree on the uniform set (the parity
        // harness) and on what the branch does, or the two backends show different pictures.
        string gl = Read("sources/shaders/final.fsh");
        Assert.Contains("uniform int optimumAoDebug;", gl);
        Assert.Contains("float aoDebugTerm = texture(ssaoScene, texCoord).r;", gl);
        Assert.Contains("outColor = vec4(vec3(aoDebugTerm), 1.0);", gl);

        string vk = Read("sources/shaders-vk/final.frag");
        Assert.Contains("float aoDebugTerm = texture(optimumTextures2D[ssaoScene], texCoord).r;", vk);
        Assert.Contains("outColor = vec4(vec3(aoDebugTerm), 1.0);", vk);
        Assert.Contains("int optimumAoDebug;", Read("sources/shaders-vk/final.interface.glsl"));

        // Before colour grading and vignetting in both: the view is the AO term, nothing else.
        Assert.True(gl.IndexOf("optimumAoDebug != 0", StringComparison.Ordinal)
            < gl.IndexOf("vec4 gradedColor = ColorGrade(color);", StringComparison.Ordinal));
        Assert.True(vk.IndexOf("optimumAoDebug != 0", StringComparison.Ordinal)
            < vk.IndexOf("vec4 gradedColor = ColorGrade(color);", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDebugViewIsAnOptimumTabSwitchWithItsLangEntriesAndPatcherListing()
    {
        string gui = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs");
        Assert.Contains("Lang.Get(\"optimum-aodebug\")", gui);
        Assert.Contains("AddSwitch(onOptimumAmbientOcclusionDebugChanged", gui);
        Assert.Contains("composer.GetSwitch(\"optAoDebug\").SetValue(Vintagestory.API.Config.OptimumConfig.AmbientOcclusionDebugView);", gui);

        string handler = Between(gui, "private void onOptimumAmbientOcclusionDebugChanged(bool on)", "\n\t}");
        Assert.Contains("OptimumConfig.AmbientOcclusionDebugView = on;", handler);
        Assert.DoesNotContain("ReloadShaders", handler);
        Assert.DoesNotContain("RebuildFrameBuffers", handler);

        string lang = Read("sources/lang/en.json");
        Assert.Contains("\"optimum-aodebug\":", lang);
        Assert.Contains("\"optimum-aodebug-tooltip\":", lang);
        Assert.Contains("\"onOptimumAmbientOcclusionDebugChanged\"", Read("Optimum.Patcher/Program.cs"));
    }

    // ------------------------------------------------------------------ helpers

    private static string Between(string text, string start, string end)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "missing: " + start);
        int to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, "missing after " + start + ": " + end);
        return text[from..to];
    }

    private static string Root()
    {
        string root = Directory.GetCurrentDirectory();
        while (!Directory.Exists(Path.Combine(root, "Optimum.Patcher"))) root = Directory.GetParent(root)!.FullName;
        return root;
    }

    private static string Read(string path) => File.ReadAllText(Path.Combine(Root(), path));
}
