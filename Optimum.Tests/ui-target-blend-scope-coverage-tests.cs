using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// DLSS frame generation, design step 3: the blend seam of the HUD-less frame.
///
/// The GUI writes STRAIGHT alpha and the whole Ortho stage draws under
/// EnumBlendMode.Standard, whose vanilla factors are non-separate - SRC_ALPHA is
/// applied to the alpha channel too. Onto the opaque world that is invisible; into a
/// UI target cleared to (0,0,0,0) it gives out_a = src_a*src_a + dst_a*(1-src_a),
/// alpha squared per layer, and the premultiplied compose afterwards shows the world
/// through the HUD. The fix is scoped, not global: Standard keeps vanilla's factors
/// everywhere except while the UI target is the bound framebuffer, because the
/// unqualified GlToggleBlend(on: true) is also world transparency, particles, decals
/// and OIT, and changing their alpha result silently is the class of bug the SSAO
/// attachment and masked-clear hunts already cost us.
///
/// The pixels are proven on the GPU by
/// Optimum.Render.Vulkan.Tests/PremultipliedUiBlendTests.cs; these tests pin that the
/// scope exists in both backends, that it is a branch rather than a global change, and
/// that the injected members are registered with the patcher (an unlisted member is
/// dropped by the transplant at runtime and the scope would silently never open).
/// </summary>
public class UiTargetBlendScopeCoverageTests
{
    /// <summary>
    /// The GL body: Standard picks the separate-alpha form only under the flag, and the
    /// unscoped path is still the byte-for-byte vanilla BlendFunc(SRC_ALPHA,
    /// ONE_MINUS_SRC_ALPHA) - 770/771 in the decompiled constants.
    /// </summary>
    [Fact]
    public void TheGlStandardModeSwitchesAlphaFactorsOnlyUnderTheFlag()
    {
        string platform = VulkanPlatformSource.ReadClientPlatformWindows();

        Assert.Contains("public bool OptimumUiTargetBound", platform);
        Assert.Contains("private bool optimumUiTargetBound;", platform);

        string body = MethodBody(platform, "public override void GlToggleBlend(bool on, EnumBlendMode blendMode = EnumBlendMode.Standard)");

        Assert.Contains("if (optimumUiTargetBound)", body);
        // The scoped form: rgb unchanged (770, 771), alpha the over-operator (1, 771).
        Assert.Contains(
            "GL.BlendFuncSeparate((BlendingFactorSrc)770, (BlendingFactorDest)771, (BlendingFactorSrc)1, (BlendingFactorDest)771);",
            body);
        // The unscoped form: exactly what vanilla emits.
        Assert.Contains("GL.BlendFunc((BlendingFactor)770, (BlendingFactor)771);", body);

        // The scope is one branch in the Standard case, not a rewrite of the named
        // modes: Brighten, Multiply, PremultipliedAlpha, Glow and Overlay keep their
        // vanilla factor pairs verbatim.
        Assert.Contains("GL.BlendFunc((BlendingFactor)774, (BlendingFactor)1);", body);
        Assert.Contains(
            "GL.BlendFuncSeparate((BlendingFactorSrc)0, (BlendingFactorDest)771, (BlendingFactorSrc)1, (BlendingFactorDest)771);",
            body);
        Assert.Contains("GL.BlendFunc((BlendingFactor)1, (BlendingFactor)771);", body);
        Assert.Contains(
            "GL.BlendFuncSeparate((BlendingFactorSrc)770, (BlendingFactorDest)1, (BlendingFactorSrc)1, (BlendingFactorDest)0);",
            body);
        Assert.Contains(
            "GL.BlendFuncSeparate((BlendingFactorSrc)770, (BlendingFactorDest)771, (BlendingFactorSrc)1, (BlendingFactorDest)1);",
            body);

        // GlToggleBlend reads the scope in exactly one place: it is a blend-state switch,
        // not a mode the rest of the method may branch on a second time. (Who is allowed
        // to WRITE the field is TheScopeIsOneFlagWrittenOnlyByTheUiTarget, below - that
        // pair of counts is what the two streams' merge had to get right.)
        Assert.Equal(1, Occurrences(body, "optimumUiTargetBound"));
    }

    /// <summary>
    /// With the scope closed the GL Standard factors are the vanilla line itself: the
    /// same GL.BlendFunc call the decompiled vanilla ClientPlatformWindows emits, taken
    /// from _ref/ rather than retyped here.
    /// </summary>
    [Fact]
    public void TheUnscopedGlStandardFactorsAreTheVanillaLine()
    {
        string? vanillaPath = TryFind("_ref/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        if (vanillaPath == null) return; // _ref/ is only present where the lib is built.

        string vanillaBody = MethodBody(File.ReadAllText(vanillaPath),
            "public override void GlToggleBlend(bool on, EnumBlendMode blendMode = EnumBlendMode.Standard)");
        const string standardLine = "GL.BlendFunc((BlendingFactor)770, (BlendingFactor)771);";
        Assert.Contains(standardLine, vanillaBody);

        string body = MethodBody(VulkanPlatformSource.ReadClientPlatformWindows(),
            "public override void GlToggleBlend(bool on, EnumBlendMode blendMode = EnumBlendMode.Standard)");
        Assert.Contains(standardLine, body);
        // One call, in the else arm of the scope test - not a second unconditional one.
        Assert.Equal(1, Occurrences(body, standardLine));
    }

    /// <summary>
    /// The contract the other half of the feature has to honour is written down where
    /// the flag is declared, or the integration guesses at it.
    /// </summary>
    [Fact]
    public void TheScopeContractIsDocumentedOnTheFlag()
    {
        string platform = VulkanPlatformSource.ReadClientPlatformWindows();
        int flag = platform.IndexOf("public bool OptimumUiTargetBound", StringComparison.Ordinal);
        Assert.True(flag > 0);
        string doc = platform.Substring(Math.Max(0, flag - 3000), Math.Min(3000, flag));

        Assert.Contains("OptimumBindUiTarget", doc);
        Assert.Contains("OptimumComposeUiTarget", doc);
        Assert.Contains("src_a*src_a", doc);
    }

    /// <summary>
    /// There is exactly one such flag, and only the UI target's own bind, compose,
    /// publisher and the resize teardown write it.
    ///
    /// <para>Why this is a test.</para> The scope was built by one stream and consumed
    /// by another, each of which had to assume the flag existed; a merge that kept both
    /// declarations would compile, and the reader and the writer would then be different
    /// fields - the branch in GlToggleBlend would simply never fire and the HUD would
    /// come back alpha-squared with nothing failing anywhere.
    /// </summary>
    [Fact]
    public void TheScopeIsOneFlagWrittenOnlyByTheUiTarget()
    {
        string platform = VulkanPlatformSource.ReadClientPlatformWindows();

        Assert.Equal(1, Occurrences(platform, "private bool optimumUiTargetBound;"));
        Assert.Equal(1, Occurrences(platform, "public bool OptimumUiTargetBound"));

        // Writes, and the member each one lives in: the publisher, the resize teardown
        // (the buffers it described were just disposed), the bind, the compose, and the
        // frame's first clear, which heals a frame that threw between the bind and the
        // compose and never closed the scope. A sixth write means someone gave the scope
        // another owner.
        Assert.Equal(5, Occurrences(platform, "optimumUiTargetBound = false;"));
        Assert.Equal(1, Occurrences(platform, "optimumUiTargetBound = true;"));

        foreach (string owner in new[]
        {
            "public void SetOptimumUiTargetIndex(int index)",
            "public void OptimumBindUiTarget()",
            "public override void OptimumComposeUiTarget()",
        })
        {
            Assert.Contains("optimumUiTargetBound", MethodBody(platform, owner));
        }

        // The bind arms it last, so nothing between the clear and that statement can draw
        // under the scoped factors, and the compose disarms it first, so an early return
        // in the compose cannot leave it armed for the rest of the frame.
        string bind = MethodBody(platform, "public void OptimumBindUiTarget()");
        Assert.EndsWith("optimumUiTargetBound = true;\n\t}", bind.Replace("\r\n", "\n").TrimEnd());
        string compose = MethodBody(platform, "public override void OptimumComposeUiTarget()");
        int guard = compose.IndexOf("if (!optimumUiTargetBound) return;", StringComparison.Ordinal);
        int disarm = compose.IndexOf("optimumUiTargetBound = false;", StringComparison.Ordinal);
        Assert.True(guard >= 0 && disarm > guard);
        Assert.True(disarm < compose.IndexOf("RenderFullscreenTriangle", StringComparison.Ordinal));
    }

    /// <summary>
    /// The Vulkan mirror: the tracker takes the scope as an argument that defaults to
    /// false, applies it to Standard alone, and the platform passes the platform flag.
    /// </summary>
    [Fact]
    public void TheVulkanTrackerMirrorsTheScopedFactors()
    {
        string tracker = Read("Optimum.Render.Vulkan/Core/GlStateTracker.cs");
        Assert.Contains("public void SetBlend(bool enabled, EnumBlendMode mode, bool uiTargetBound = false)", tracker);
        Assert.Contains("_ => uiTargetBound", tracker);
        Assert.Contains(
            "? (BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha,\n                    BlendFactor.One, BlendFactor.OneMinusSrcAlpha)",
            tracker.Replace("\r\n", "\n"));
        Assert.Contains(
            ": (BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha,\n                    BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha)",
            tracker.Replace("\r\n", "\n"));

        string device = Read("Optimum.Render.Vulkan/VulkanDevice.cs");
        Assert.Contains("public void SetBlend(bool enabled, EnumBlendMode mode, bool uiTargetBound = false)", device);
        Assert.Contains("_state.SetBlend(enabled, mode, uiTargetBound);", device);

        string state = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.State.cs");
        Assert.Contains("device.SetBlend(on, blendMode, OptimumUiTargetBound);", state);
    }

    /// <summary>
    /// Both injected members are listed for the Cecil transplant and as Optimum-owned
    /// regions of ClientPlatformWindows. An unlisted member compiles here and vanishes
    /// in the patched DLL.
    /// </summary>
    [Fact]
    public void TheInjectedMembersAreRegistered()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("\"OptimumUiTargetBound\",", patcher);
        Assert.Contains("\"optimumUiTargetBound\",", patcher);

        string regions = Read("Optimum.Tests/client-platform-windows-vanilla-regions-tests.cs");
        Assert.Contains("\"OptimumUiTargetBound\", \"optimumUiTargetBound\",", regions);
        // GlToggleBlend's body changes, so it stays a transplant target.
        Assert.Contains("\"GlToggleBlend\", 2)", patcher);
    }

    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "method not found: " + signature);
        int brace = source.IndexOf('{', start);
        int depth = 0;
        for (int i = brace; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(brace, i - brace + 1);
        }
        Assert.Fail("unbalanced braces after " + signature);
        return string.Empty;
    }

    private static int Occurrences(string text, string needle)
    {
        int count = 0;
        int at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
    }

    private static string? TryFind(string relativePath)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "VintageStory.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }
        if (directory == null) return null;
        string full = Path.Combine(directory, relativePath);
        return File.Exists(full) ? full : null;
    }

    private static string Read(string relativePath)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "VintageStory.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!, relativePath));
    }
}
