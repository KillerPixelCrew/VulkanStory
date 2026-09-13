using System;
using System.Collections.Generic;
using System.IO;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// DLSS plan, Phase 6: the upscaler, quality-preset and latency rows in the
/// Optimum settings tab.
///
/// Before this, the only way to switch DLSS on, change its preset or compare it
/// against TAA was to edit ModConfig/optimum.json and restart, which made a
/// quality comparison impossible. The rows are asserted here the way the rest of
/// the tab is: the page declares them with their handlers and hover texts, the
/// handlers persist and then re-plan through one injected virtual on
/// <c>ClientPlatformAbstract</c> (the page never reaches into the renderer), the
/// patcher carries every new member, the renderer's self-check expects the
/// virtuals, and every label has a translation.
///
/// The honest-state rule has its own cases: a machine with no upscaler must be
/// told so rather than offered a choice the frame would not honour, and the
/// preset row must be dead while nothing is upscaling.
///
/// The setting the rows write is asserted here too (DLSS plan, Phase 2 step 3):
/// "Upscaler" (off|dlss) and a quality preset live in OptimumConfig with the usual
/// defensive load - an unrecognised upscaler means off, an unrecognised preset means
/// quality, and neither can fail the parse - and the renderer can stand the upscaler
/// down for the session without touching the persisted value. A row and the field it
/// writes are one subject: a default that moves under a row nobody re-read is how a
/// tab starts lying about the frame.
/// </summary>
public class UpscalerSettingsUiCoverageTests
{
    private const string GuiPatch = "patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch";
    private const string GuiSource = "build/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs";
    private const string AbstractPatch = "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs.patch";
    private const string AbstractSource = "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs";

    // ---- (a) the rows ------------------------------------------------------

    [Fact]
    public void TheTabHasAnUpscalerAQualityAndALatencyRow()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);

        Assert.Contains("Lang.Get(\"optimum-upscaler\")", gui);
        Assert.Contains("onOptimumUpscalerChanged", gui);
        Assert.Contains("\"optUpscaler\")", gui);

        Assert.Contains("Lang.Get(\"optimum-upscalerquality\")", gui);
        Assert.Contains("onOptimumUpscalerQualityChanged", gui);
        Assert.Contains("\"optUpscalerQuality\")", gui);

        Assert.Contains("Lang.Get(\"optimum-latency\")", gui);
        Assert.Contains("onOptimumLatencyChanged", gui);
        Assert.Contains("\"optLatency\")", gui);

        // A switch cannot express three upscaler presets, let alone five, so all
        // three rows are dropdowns - the shape the render-scale row already uses.
        Assert.Contains("AddDropDown(new string[] { \"off\", \"dlss\", \"passthrough\" }", gui);
        Assert.Contains(
            "AddDropDown(new string[] { \"dlaa\", \"quality\", \"balanced\", \"performance\", \"ultraperformance\" }",
            gui);
        Assert.Contains("AddDropDown(new string[] { \"off\", \"on\", \"boost\" }", gui);

        // Every row in this tab carries a hover text.
        Assert.Contains("Lang.Get(\"optimum-upscaler-tooltip\")", gui);
        Assert.Contains("Lang.Get(\"optimum-upscalerquality-tooltip\")", gui);
        Assert.Contains("Lang.Get(\"optimum-latency-tooltip\")", gui);
    }

    /// <summary>
    /// The preset values the row offers are exactly the ones the config declares,
    /// DLAA included, so a preset can never be selected that the parser degrades.
    /// </summary>
    [Fact]
    public void TheQualityRowOffersEveryPresetTheConfigDeclares()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);
        string config = Read("VintagestoryApi/Config/OptimumConfig.cs");
        string declared = Between(config, "UpscalerQualityNames =", ";");

        foreach (string preset in new[] { "dlaa", "quality", "balanced", "performance", "ultraperformance" })
        {
            Assert.Contains("\"" + preset + "\"", declared);
            Assert.Contains("Lang.Get(\"optimum-upscalerquality-" + preset + "\")", gui);
        }

        // And the upscaler row offers exactly the slots the config knows about -
        // the passthrough upscaler included, which is the only one a GPU with no
        // vendor path can select.
        string slots = Between(config, "UpscalerNames = {", "}");
        foreach (string slot in new[] { "off", "dlss", "passthrough" })
        {
            Assert.Contains("\"" + slot + "\"", slots);
            Assert.Contains("Lang.Get(\"optimum-upscaler-" + slot + "\")", gui);
        }
    }

    /// <summary>
    /// The rows come up showing what the frame is really doing: the effective
    /// upscaler (a renderer stand-down has already made this session's answer
    /// "off"), not the persisted string.
    /// </summary>
    [Fact]
    public void TheRowsAreBackedByTheEffectiveStateWhenTheTabOpens()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);

        Assert.Contains(
            "composer.GetDropDown(\"optUpscaler\").SetSelectedValue(Vintagestory.API.Config.OptimumConfig.EffectiveUpscaler);",
            gui);
        Assert.Contains(
            "composer.GetDropDown(\"optUpscalerQuality\").SetSelectedValue(Vintagestory.API.Config.OptimumConfig.UpscalerQuality);",
            gui);
        Assert.Contains(
            "composer.GetDropDown(\"optLatency\").SetSelectedValue(Vintagestory.API.Config.OptimumConfig.LatencyMode);",
            gui);
        Assert.Contains("optimumUpdateUpscalerRows();", gui);
    }

    // ---- (a2) the tab the rows live on -------------------------------------

    /// <summary>
    /// PR #3, "needs its own settings tab": upscaling is a page beside the Extra
    /// one, registered exactly the way every other tab in this dialog is - a
    /// toggle button in both <c>ComposerHeader</c> branches (main menu and
    /// in-game), its own bounds measured in <c>updateButtonBounds</c>, its toggle
    /// key set from the current tab, and a page method that composes through
    /// <c>ComposerHeader</c> with that key.
    /// </summary>
    [Fact]
    public void UpscalingIsItsOwnTabRegisteredLikeEveryOtherTab()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);

        // The bounds, beside the Extra tab's own. No initializer: the field is
        // Cecil-injected and the vanilla .ctor never assigns it, so it is allocated
        // lazily (see CecilInjectedFieldInitializerTests). In the shipped DLL the tab
        // row is added by the injected _AddOptimumTab hook; ComposerHeader and
        // updateButtonBounds below are donor source, not transplant targets.
        Assert.Contains("private ElementBounds uButtonBounds;", gui);
        Assert.Contains("uButtonBounds ??= ElementBounds.Fixed(0.0, 0.0, 0.0, 40.0)", gui);
        Assert.Contains("uButtonBounds.ParentBounds = elementBounds;", gui);   // main menu
        Assert.Contains("uButtonBounds.ParentBounds = elementBounds3;", gui);  // in game

        // The button, in both branches, with the same toggle key the page composes with.
        Assert.Contains(
            "AddToggleButton(Lang.Get(\"optimum-upscaling-tab-header\"), font, OnOptimumUpscalingOptions, uButtonBounds, \"optimumupscaling\")",
            gui);
        Assert.Equal(2, Occurrences(gui,
            "AddToggleButton(Lang.Get(\"optimum-upscaling-tab-header\"), font, OnOptimumUpscalingOptions, uButtonBounds, \"optimumupscaling\")"));
        Assert.Contains("result.GetToggleButton(\"optimumupscaling\")?.SetValue(currentTab == \"optimumupscaling\");", gui);

        // The tab-width measurement that sizes the button row, and the Back button
        // that now follows the new tab rather than the Extra one.
        Assert.Contains("cairoFont.GetTextExtents(Lang.Get(\"optimum-upscaling-tab-header\"));", gui);
        Assert.Contains("uButtonBounds.WithFixedWidth(width10).FixedRightOf(oButtonBounds, 15.0);", gui);
        Assert.Contains("backButtonBounds.WithFixedWidth(width8).FixedRightOf(uButtonBounds, 25.0);", gui);

        // The page itself.
        Assert.Contains("private void OnOptimumUpscalingOptions(bool on)", gui);
        Assert.Contains("ComposerHeader(\"gamesettings-optimumupscalingoptions\", \"optimumupscaling\")", gui);
        Assert.Contains("composer.GetToggleButton(\"optimumupscaling\")?.SetValue(true);", gui);
    }

    /// <summary>
    /// The Cecil path adds its tab buttons through one injected hook helper
    /// (<c>_AddOptimumTab</c>, called after <c>EndIf</c> in vanilla's
    /// <c>ComposerHeader</c>), so the new tab has to be added there too or it
    /// exists only in the developer build. The Back button and the dialog-width
    /// walk measure from the last button in the row, which is now the new one -
    /// the walk itself is what keeps the Cairo surface from being overrun.
    /// </summary>
    [Fact]
    public void TheCecilHookAddsBothTabButtons()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);
        string hook = Between(gui, "private Vintagestory.API.Client.GuiComposer _AddOptimumTab(", "\n\t}");

        Assert.Contains("Lang.Get(\"optimum-upscaling-tab-header\")", hook);
        Assert.Contains("OnOptimumUpscalingOptions, uButtonBounds, \"optimumupscaling\")", hook);
        Assert.Contains("uButtonBounds.FixedRightOf(oButtonBounds, 10.0);", hook);
        Assert.Contains("backButtonBounds.FixedRightOf(uButtonBounds, 15.0);", hook);
        Assert.Contains("? uButtonBounds.fixedX + uButtonBounds.fixedWidth", hook);
    }

    /// <summary>
    /// The readout the tab buys with the space: the plan the frame is really
    /// running, asked of the platform that owns the upscaler and re-read once per
    /// frame, never predicted from the preset.
    /// </summary>
    [Fact]
    public void TheTabShowsTheLivePlan()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);
        string platform = ReadPatchedOrSource(AbstractPatch, AbstractSource);

        Assert.Contains("public virtual string OptimumUpscalerPlan()", platform);
        Assert.Contains(
            "AddDynamicText(optimumUpscalePlanText(), CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, y0 + rowH * 8, 650, 60), \"optUpscalePlan\")",
            gui);

        string text = Between(gui, "private string optimumUpscalePlanText()", "\n\t}");
        Assert.Contains("ScreenManager.Platform.OptimumUpscalerPlan()", text);
        Assert.Contains("Lang.Get(\"optimum-upscaleplan-none\")", text);

        // Live: the readout follows the frame, because the plan only becomes the new
        // one on the frame that creates the feature.
        string refresh = Between(gui, "internal void Refresh()", "\n\t}");
        Assert.Contains("optimumUpdateUpscalePlanReadout();", refresh);
        string readout = Between(gui, "private void optimumUpdateUpscalePlanReadout()", "\n\t}");
        Assert.Contains("composer.GetDynamicText(\"optUpscalePlan\")", readout);
        Assert.Contains("readout.SetNewText(optimumUpscalePlanText());", readout);
    }

    /// <summary>
    /// The Vulkan platform answers the readout from the live feature's own plan,
    /// and null while none is serving one - the tab then says so rather than
    /// printing numbers no frame used.
    /// </summary>
    [Fact]
    public void TheVulkanPlatformAnswersThePlanFromTheLiveFeature()
    {
        string upscale = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Upscale.cs");
        string plan = Between(upscale, "public override string OptimumUpscalerPlan()", "\n    }");

        // The passthrough upscaler holds no feature, so its readout is the plan the
        // last frame really blitted, marked for what it is.
        Assert.Contains("if (PassthroughUpscaler.Requested)", plan);
        Assert.Contains("passthrough, no reconstruction, jitter ", plan);
        Assert.Contains("if (upscaler == null) return null;", plan);
        Assert.Contains("UpscalePlan plan = upscaler.Plan;", plan);
        Assert.Contains("return plan.IsValid ? plan.ToString() : null;", plan);
    }

    // ---- (b) honest state --------------------------------------------------

    [Fact]
    public void AnUnavailableUpscalerIsShownRatherThanOffered()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);

        // The page asks the platform - the renderer already knows every way DLSS
        // can be absent and produces the sentence.
        Assert.Contains("ScreenManager.Platform.OptimumUpscalerUnavailable()", gui);
        // The entry says so, and the reason goes into the row's hover text.
        Assert.Contains("Lang.Get(\"optimum-upscaler-dlss-unavailable\")", gui);
        Assert.Contains("Lang.Get(\"optimum-upscaler-unavailable\") + \" \" + upscalerUnavailable", gui);

        // And selecting it anyway is refused, with the row put back to what is
        // running rather than left claiming an upscale.
        // Asked per entry, not about the slot: the passthrough upscaler needs no
        // vendor runtime and must stay selectable where DLSS cannot come up.
        string handler = Between(gui, "private void onOptimumUpscalerChanged(", "\n\t}\n");
        Assert.Contains("ScreenManager.Platform.OptimumUpscalerUnavailableFor(code)", handler);
        Assert.Contains(
            "if (unavailable != null && !string.Equals(code, \"off\", StringComparison.OrdinalIgnoreCase))",
            handler);
        // The refusal returns before anything is persisted or rebuilt, having put
        // the row back to what is running.
        int refusal = handler.IndexOf("if (unavailable != null", StringComparison.Ordinal);
        int returned = handler.IndexOf("return;", refusal, StringComparison.Ordinal);
        int persisted = handler.IndexOf("OptimumConfig.Upscaler = code;", StringComparison.Ordinal);
        Assert.InRange(returned, refusal, persisted);
        Assert.InRange(
            handler.IndexOf("optimumUpdateUpscalerRows();", refusal, StringComparison.Ordinal), refusal, returned);
    }

    [Fact]
    public void TheQualityRowIsDeadWhileNothingIsUpscaling()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);
        string refresh = Between(gui, "private void optimumUpdateUpscalerRows()", "\n\t}\n");

        // Any upscaler that owns the resolve, not DLSS alone: the preset chooses the
        // render size for the passthrough upscaler too.
        Assert.Contains(
            "quality.Enabled = Vintagestory.API.Config.OptimumConfig.UpscalerReplacesTaa;",
            refresh);
        // And the selection follows the effective upscaler, not the setting.
        Assert.Contains(
            "upscaler.SetSelectedValue(Vintagestory.API.Config.OptimumConfig.EffectiveUpscaler);",
            refresh);
    }

    /// <summary>
    /// While an upscaler owns the temporal resolve the TAA switch does nothing,
    /// and the page says so instead of implying two resolves.
    /// </summary>
    [Fact]
    public void TheTaaRowSaysWhenAnUpscalerOwnsTheResolve()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);
        Assert.Contains("if (Vintagestory.API.Config.OptimumConfig.UpscalerReplacesTaa)", gui);
        Assert.Contains("Lang.Get(\"optimum-taa-upscaler-owns-resolve\")", gui);
        Assert.Contains(".AddHoverText(taaTooltip,", gui);
    }

    // ---- (c) live apply ----------------------------------------------------

    [Fact]
    public void TheUpscalerHandlerPersistsReplansReloadsAndDropsTheHistory()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);
        string handler = Between(gui, "private void onOptimumUpscalerChanged(", "\n\t}\n");

        Assert.Contains("Vintagestory.API.Config.OptimumConfig.Upscaler = code;", handler);
        Assert.Contains("Vintagestory.API.Config.OptimumConfig.Save();", handler);
        // Through the virtual, never into the renderer: the settings page must not
        // know that Optimum.Render.Vulkan exists.
        Assert.Contains("ScreenManager.Platform.ApplyOptimumUpscalerSettings();", handler);
        Assert.DoesNotContain("Optimum.Render", handler);
        // The motion writers are compiled against "does anything temporal want this
        // frame jittered", which changes when the upscaler is the only consumer.
        Assert.Contains("handler.ReloadShaders();", handler);
        Assert.Contains("OptimumTemporal.RequestReset(EnumTemporalResetReason.Toggle);", handler);
    }

    [Fact]
    public void ThePresetHandlerReplansWithoutReloadingShaders()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);
        string handler = Between(gui, "private void onOptimumUpscalerQualityChanged(", "\n\t}\n");

        Assert.Contains("Vintagestory.API.Config.OptimumConfig.UpscalerQuality = code;", handler);
        Assert.Contains("Vintagestory.API.Config.OptimumConfig.Save();", handler);
        Assert.Contains("ScreenManager.Platform.ApplyOptimumUpscalerSettings();", handler);
        Assert.Contains("OptimumTemporal.RequestReset(EnumTemporalResetReason.RenderScale);", handler);
        // Only the render size changes: every define, motion writer and uniform is
        // the same on both sides of a preset change.
        Assert.DoesNotContain("ReloadShaders", handler);
    }

    [Fact]
    public void TheLatencyHandlerOnlyReAppliesTheMode()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);
        string handler = Between(gui, "private void onOptimumLatencyChanged(", "\n\t}\n");

        Assert.Contains("Vintagestory.API.Config.OptimumConfig.LatencyMode = code;", handler);
        Assert.Contains("Vintagestory.API.Config.OptimumConfig.Save();", handler);
        Assert.Contains("ScreenManager.Platform.ApplyOptimumLatencySettings();", handler);
        // No target changes size and nothing temporal is invalidated.
        Assert.DoesNotContain("RebuildFrameBuffers", handler);
        Assert.DoesNotContain("ReloadShaders", handler);
        Assert.DoesNotContain("RequestReset", handler);
    }

    /// <summary>
    /// The seam itself: three virtuals on the abstract platform with neutral
    /// bodies. The rebuild is the whole of what the OpenGL path needs - it
    /// re-plans the render size and reallocates every target, which is what
    /// restores the pre-upscaler chain when the slot goes back to off - and the
    /// OpenGL path has no latency backend at all.
    /// </summary>
    [Fact]
    public void TheAbstractPlatformDeclaresTheThreeNeutralVirtuals()
    {
        string platform = ReadPatchedOrSource(AbstractPatch, AbstractSource);

        Assert.Contains("public virtual string OptimumUpscalerUnavailable()", platform);
        Assert.Contains("return Vintagestory.API.Config.Lang.Get(\"optimum-upscaler-unavailable-renderer\");", platform);

        string apply = Between(platform, "public virtual void ApplyOptimumUpscalerSettings()", "\n\t}");
        Assert.Contains("RebuildFrameBuffers();", apply);

        string latency = Between(platform, "public virtual void ApplyOptimumLatencySettings()", "\n\t}");
        Assert.DoesNotContain("RebuildFrameBuffers", latency);
    }

    // ---- (d) transplant and self-check -------------------------------------

    [Fact]
    public void EveryNewMemberIsListedForTheCecilTransplant()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");
        foreach (string member in new[]
        {
            // The injected virtuals on ClientPlatformAbstract.
            "\"OptimumUpscalerUnavailable\"",
            "\"ApplyOptimumUpscalerSettings\"",
            "\"ApplyOptimumLatencySettings\"",
            // The settings page's new handlers and its row refresh.
            "\"onOptimumUpscalerChanged\"",
            "\"onOptimumUpscalerQualityChanged\"",
            "\"onOptimumLatencyChanged\"",
            "\"optimumUpdateUpscalerRows\"",
            // The page method that builds the rows was already a target; it has to
            // stay one, or the new rows never reach the shipped client.
            "\"OnOptimumOptions\"",
            // PR #3 follow-up: the upscaling tab, its bounds, its readout and the
            // per-frame refresh that feeds it.
            "\"uButtonBounds\"",
            "\"OnOptimumUpscalingOptions\"",
            "\"optimumUpscalePlanText\"",
            "\"optimumUpdateUpscalePlanReadout\"",
            "\"OptimumUpscalerPlan\"",
        })
        {
            Assert.Contains(member, patcher);
        }

        // Refresh is vanilla, so member injection would skip it ("MEMBER EXISTS")
        // and the readout would never get its frame: its body is transplanted
        // through the method-target list instead.
        Assert.Contains(
            "new(\"Vintagestory.Client.NoObf.GuiCompositeSettings\", \"Refresh\", 0)",
            patcher);
    }

    [Fact]
    public void TheRendererSelfCheckExpectsTheThreeVirtuals()
    {
        string platform = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.cs");
        string expected = Between(platform, "ExpectedVirtuals =", "};");

        Assert.Contains("new(true, \"OptimumUpscalerUnavailable\", Array.Empty<string>())", expected);
        Assert.Contains("new(true, \"ApplyOptimumUpscalerSettings\", Array.Empty<string>())", expected);
        Assert.Contains("new(true, \"ApplyOptimumLatencySettings\", Array.Empty<string>())", expected);
    }

    /// <summary>
    /// The Vulkan side of the same three: the reason comes from the host that
    /// produced it, a slot or preset change retires the live feature before the
    /// rebuild (NGX sizes its buffers at creation, so a plan change is a new
    /// feature), and the frame stops upscaling the moment the setting says off.
    /// </summary>
    [Fact]
    public void TheVulkanPlatformOverridesAllThree()
    {
        string upscale = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Upscale.cs");

        Assert.Contains("public override string OptimumUpscalerUnavailable()", upscale);
        Assert.Contains("return upscaler.Active ? null : upscaler.Unavailable;", upscale);
        Assert.Contains("public override string OptimumUpscalerUnavailableFor(string upscaler)", upscale);

        string apply = Between(upscale, "public override void ApplyOptimumUpscalerSettings()", "\n    }");
        Assert.Contains("upscaler.RetireFeature();", apply);
        Assert.Contains("base.ApplyOptimumUpscalerSettings();", apply);

        Assert.Contains("public override void ApplyOptimumLatencySettings()", upscale);
        Assert.Contains("device?.ReapplyLatencySettings();", upscale);

        // The setting, not just the host's liveness, decides whether the frame
        // upscales: the tab can turn it off while the host is still up.
        Assert.Contains(
            "(UpscalerActive || PassthroughUpscaler.Requested) &&\n" +
            "        OptimumConfig.UpscalerReplacesTaa && MotionAttachmentIndex >= 0",
            upscale);
    }

    // ---- (e) language ------------------------------------------------------

    [Fact]
    public void EveryNewLabelAndTooltipHasAnEnglishString()
    {
        string lang = Read("sources/lang/en.json");
        foreach (string key in new[]
        {
            "optimum-upscaler", "optimum-upscaler-tooltip",
            "optimum-upscaler-off", "optimum-upscaler-dlss", "optimum-upscaler-dlss-unavailable",
            "optimum-upscaler-unavailable", "optimum-upscaler-unavailable-renderer",
            "optimum-upscalerquality", "optimum-upscalerquality-tooltip",
            "optimum-upscalerquality-dlaa", "optimum-upscalerquality-quality",
            "optimum-upscalerquality-balanced", "optimum-upscalerquality-performance",
            "optimum-upscalerquality-ultraperformance",
            "optimum-latency", "optimum-latency-tooltip",
            "optimum-latency-off", "optimum-latency-on", "optimum-latency-boost",
            "optimum-taa-upscaler-owns-resolve",
            // PR #3 follow-up: the tab and its plan readout.
            "optimum-upscaling-tab-header",
            "optimum-upscaleplan", "optimum-upscaleplan-tooltip", "optimum-upscaleplan-none",
            // The passthrough upscaler: the slot entry, its filter row and the
            // comparison experiment's jitter switch.
            "optimum-upscaler-passthrough",
            "optimum-upscalerpassthroughfilter", "optimum-upscalerpassthroughfilter-tooltip",
            "optimum-upscalerpassthroughfilter-linear", "optimum-upscalerpassthroughfilter-nearest",
            "optimum-upscalerjitter", "optimum-upscalerjitter-tooltip",
            "optimum-upscalerjitter-on", "optimum-upscalerjitter-off",
        })
        {
            Assert.Contains("\"" + key + "\":", lang);
        }
    }

    /// <summary>
    /// The other direction, so a row cannot reference a key nobody wrote: every
    /// <c>optimum-upscaler*</c> and <c>optimum-latency*</c> key the settings page
    /// asks for really exists in en.json.
    /// </summary>
    [Fact]
    public void ThePageNeverAsksForAKeyEnJsonDoesNotHave()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);
        string lang = Read("sources/lang/en.json");

        var asked = new List<string>();
        int at = 0;
        while (true)
        {
            int from = gui.IndexOf("Lang.Get(\"", at, StringComparison.Ordinal);
            if (from < 0) break;
            from += "Lang.Get(\"".Length;
            int to = gui.IndexOf('"', from);
            if (to < 0) break;
            at = to;
            string key = gui[from..to];
            // "optimum-upscal" covers optimum-upscaler*, optimum-upscaling-tab-header
            // and the optimum-upscaleplan* readout keys in one prefix.
            if (key.StartsWith("optimum-upscal", StringComparison.Ordinal) ||
                key.StartsWith("optimum-latency", StringComparison.Ordinal) ||
                key.StartsWith("optimum-framegeneration", StringComparison.Ordinal) ||
                key.StartsWith("optimum-taa-upscaler", StringComparison.Ordinal))
            {
                asked.Add(key);
            }
        }

        Assert.NotEmpty(asked);
        foreach (string key in asked)
        {
            Assert.Contains("\"" + key + "\":", lang);
        }
    }

    // ---- (e) the setting the rows write ------------------------------------

    [Fact]
    public void TheSettingIsDeclaredPersistedAndDefensivelyParsed()
    {
        foreach (string config in new[]
        {
            Read("VintagestoryApi/Config/OptimumConfig.cs"),
            Read("sources/VintagestoryApi/Config/OptimumConfig.cs"),
        })
        {
            // Off by default, in the live value and in the persisted data object, so a
            // config that never mentions the setting comes up with the old render chain.
            Assert.Contains("public static string Upscaler = \"off\";", config);
            Assert.Contains("public string Upscaler { get; set; } = \"off\";", config);
            Assert.Contains("public static string UpscalerQuality = \"quality\";", config);
            Assert.Contains("public string UpscalerQuality { get; set; } = \"quality\";", config);

            Assert.Contains("(nameof(OptimumConfigData.Upscaler), Upscaler),", config);
            Assert.Contains("(nameof(OptimumConfigData.UpscalerQuality), UpscalerQuality),", config);
            Assert.Contains("Upscaler = Upscaler,", config);
            Assert.Contains("UpscalerQuality = UpscalerQuality,", config);

            // Defensive load, the same shape the renderer selection uses.
            Assert.Contains("string requestedUpscaler = data.Upscaler?.Trim() ?? \"\";", config);
            Assert.Contains("string requestedUpscalerQuality = data.UpscalerQuality?.Trim() ?? \"\";", config);

            // A runtime stand-down that never touches the persisted value, and reports
            // itself exactly once so the renderer logs one line.
            Assert.Contains("public static bool UpscalerRuntimeDisabled { get; private set; }", config);
            Assert.Contains("public static bool DisableUpscalerAtRuntime()", config);
            Assert.Contains("public static string EffectiveUpscaler => UpscalerRuntimeDisabled ? \"off\" : Upscaler;", config);
            // Every upscaler that owns the resolve, not DLSS alone: the passthrough
            // upscaler takes the identical path and stands the in-house resolve, the
            // sharpen pass and the FSR blit down exactly as DLSS does.
            Assert.Contains(
                "public static bool UpscalerReplacesTaa => EffectiveUpscalerIsDlss || EffectiveUpscalerIsPassthrough;",
                config);
        }
    }

    /// <summary>
    /// The round trip through a real optimum.json: the default is off, a config that
    /// never mentions the setting comes up off, an explicit value survives load and
    /// save, and an unknown one degrades instead of failing the parse.
    /// </summary>
    [Theory]
    [InlineData("\"dlss\"", "\"performance\"", "dlss", "performance")]
    [InlineData("\"DLSS\"", "\"ULTRAPERFORMANCE\"", "dlss", "ultraperformance")]
    [InlineData("\"  dlss  \"", "\"  dlaa  \"", "dlss", "dlaa")]
    [InlineData("\"off\"", "\"balanced\"", "off", "balanced")]
    [InlineData("\"xess\"", "\"nonsense\"", "off", "quality")]
    [InlineData("null", "null", "off", "quality")]
    [InlineData(null, null, "off", "quality")]
    public void TheUpscalerRoundTripsThroughOptimumJson(
        string? upscalerJson, string? qualityJson, string expectedUpscaler, string expectedQuality)
    {
        string originalUpscaler = OptimumConfig.Upscaler;
        string originalQuality = OptimumConfig.UpscalerQuality;
        string dataPath = Path.Combine(Path.GetTempPath(), "optimum-upscaler-" + Guid.NewGuid().ToString("N"));
        try
        {
            // A scan report that succeeded and disabled nothing leaves the assembly's
            // shader-compatibility state exactly as a fresh process has it.
            Directory.CreateDirectory(Path.Combine(dataPath, ".optimum"));
            File.WriteAllText(Path.Combine(dataPath, ".optimum", "shader-compatibility.json"),
                "{ \"ScanFailed\": false, \"DisabledFeatures\": [] }");

            OptimumConfig.SetDataPath(dataPath);
            string configPath = Path.Combine(dataPath, "ModConfig", "optimum.json");
            File.WriteAllText(configPath, upscalerJson == null
                ? "{}"
                : "{ \"Upscaler\": " + upscalerJson + ", \"UpscalerQuality\": " + qualityJson + " }");

            OptimumConfig.Upscaler = "dlss";
            OptimumConfig.UpscalerQuality = "ultraperformance";
            OptimumConfig.Load();
            Assert.Equal(expectedUpscaler, OptimumConfig.Upscaler);
            Assert.Equal(expectedQuality, OptimumConfig.UpscalerQuality);

            string written = File.ReadAllText(configPath);
            Assert.Contains("\"Upscaler\": \"" + expectedUpscaler + "\"", written);
            Assert.Contains("\"UpscalerQuality\": \"" + expectedQuality + "\"", written);
        }
        finally
        {
            OptimumConfig.Upscaler = originalUpscaler;
            OptimumConfig.UpscalerQuality = originalQuality;
            try { Directory.Delete(dataPath, recursive: true); } catch (IOException) { }
        }
    }

    // ---- (f) frame generation ----------------------------------------------
    //
    // DLSS-G ships paced, with Reflex, or not at all (docs/ROADMAP.md, "The paced present:
    // the design"; user, 2026-09-13: "DLSSFG without pacing (Reflex) is useless and
    // unplayable"). The row, the setting it writes and the gates it feeds are one subject.

    /// <summary>
    /// The row sits in the upscaling tab as a dropdown whose "on" entry can say it is
    /// unavailable, and its tooltip states every requirement and what the player gets.
    /// </summary>
    [Fact]
    public void TheTabHasAFrameGenerationRowThatStatesItsRequirements()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);
        string page = Between(gui, "private void OnOptimumUpscalingOptions(bool on)", "\n\t}\n");

        Assert.Contains(
            "AddDynamicText(optimumFrameGenerationLabelText(), CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, y0 + rowH * 6, 440, 30), \"optFrameGenerationLabel\")",
            page);
        Assert.Contains(
            "AddDropDown(new string[] { \"off\", \"on\" }, new string[] { Lang.Get(\"optimum-framegeneration-off\"), frameGenerationOnLabel }, 0, onOptimumFrameGenerationChanged, ElementBounds.Fixed(450, y0 + rowH * 6 + 2, 200, 25), \"optFrameGeneration\")",
            page);
        Assert.Contains("string frameGenerationTooltip = Lang.Get(\"optimum-framegeneration-tooltip\");", page);
        Assert.Contains(".AddHoverText(frameGenerationTooltip,", page);
        // The row opens on what the next session starts with, which is what it writes.
        Assert.Contains(
            "composer.GetDropDown(\"optFrameGeneration\").SetSelectedValue(Vintagestory.API.Config.OptimumConfig.FrameGenerationNextSession ? \"on\" : \"off\");",
            page);

        string tooltip = LangValue("optimum-framegeneration-tooltip");
        Assert.Contains("DLSS", tooltip);
        Assert.Contains("NVIDIA GPU with Reflex", tooltip);
        Assert.Contains("second graphics queue", tooltip);
        Assert.Contains("two frames per rendered frame", tooltip);
        Assert.Contains("pacer", tooltip);
        // The established restart wording (the greedy-mesh row's "Needs a game restart to
        // fully apply."), and the refusal to run unpaced said in so many words.
        Assert.Contains("Needs a game restart to apply.", tooltip);
        Assert.Contains("rather than running unpaced", tooltip);

        foreach (string key in new[]
        {
            "optimum-framegeneration", "optimum-framegeneration-tooltip", "optimum-framegeneration-off",
            "optimum-framegeneration-on", "optimum-framegeneration-on-unavailable",
            "optimum-framegeneration-restart", "optimum-framegeneration-unavailable-dlss",
            "optimum-framegeneration-unavailable-needs-dlss",
        })
        {
            Assert.Contains("\"" + key + "\":", Read("sources/lang/en.json"));
        }
    }

    /// <summary>
    /// Unavailable is shown, the upscaler row's way, and asked in a fixed order: the
    /// renderer's own report (the hook the wiring fills through
    /// <c>OptimumConfig.DisableFrameGenerationAtRuntime</c>) before anything the page can
    /// infer, then DLSS's own availability, then whether DLSS is the upscaler in effect.
    /// Turning it on where it cannot run is refused before anything is persisted.
    /// </summary>
    [Fact]
    public void AnUnavailableFrameGenerationIsShownAndRefused()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);
        string hook = Between(gui, "private string optimumFrameGenerationUnavailable()", "\n\t}\n");

        int reported = hook.IndexOf("Vintagestory.API.Config.OptimumConfig.FrameGenerationUnavailableReason", StringComparison.Ordinal);
        int dlss = hook.IndexOf("ScreenManager.Platform.OptimumUpscalerUnavailableFor(\"dlss\")", StringComparison.Ordinal);
        int effective = hook.IndexOf("Vintagestory.API.Config.OptimumConfig.EffectiveUpscalerIsDlss", StringComparison.Ordinal);
        Assert.True(reported >= 0 && dlss > reported && effective > dlss,
            "the renderer's report, then DLSS availability, then the upscaler in effect");
        Assert.EndsWith("return null;", hook.TrimEnd());

        string page = Between(gui, "private void OnOptimumUpscalingOptions(bool on)", "\n\t}\n");
        Assert.Contains("string frameGenerationUnavailable = optimumFrameGenerationUnavailable();", page);
        Assert.Contains("Lang.Get(\"optimum-framegeneration-on-unavailable\")", page);
        Assert.Contains("Lang.Get(\"optimum-upscaler-unavailable\") + \" \" + frameGenerationUnavailable", page);

        string handler = Between(gui, "private void onOptimumFrameGenerationChanged(", "\n\t}\n");
        int refusal = handler.IndexOf("if (on && optimumFrameGenerationUnavailable() != null)", StringComparison.Ordinal);
        int returned = handler.IndexOf("return;", refusal, StringComparison.Ordinal);
        int persisted = handler.IndexOf("FrameGenerationNextSession = on;", StringComparison.Ordinal);
        Assert.True(refusal >= 0, "turning frame generation on where it cannot run is not refused");
        Assert.InRange(returned, refusal, persisted);
        Assert.InRange(handler.IndexOf("optimumUpdateUpscalerRows();", refusal, StringComparison.Ordinal), refusal, returned);
    }

    /// <summary>
    /// Restart required, as behaviour: the handler writes only the next session's value and
    /// persists it - no re-plan, no rebuild, no shader reload, no temporal reset, and never
    /// the session's value, which is what both gates read. The label carries the note while
    /// the two differ, and the row refresh keeps it current.
    /// </summary>
    [Fact]
    public void TheFrameGenerationHandlerOnlyPersistsTheNextSession()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);
        string handler = Between(gui, "private void onOptimumFrameGenerationChanged(", "\n\t}\n");

        Assert.Contains("Vintagestory.API.Config.OptimumConfig.FrameGenerationNextSession = on;", handler);
        Assert.Contains("Vintagestory.API.Config.OptimumConfig.Save();", handler);
        Assert.DoesNotContain("OptimumConfig.FrameGeneration =", handler);
        Assert.DoesNotContain("ApplyOptimumUpscalerSettings", handler);
        Assert.DoesNotContain("RebuildFrameBuffers", handler);
        Assert.DoesNotContain("ReloadShaders", handler);
        Assert.DoesNotContain("RequestReset", handler);

        string label = Between(gui, "private string optimumFrameGenerationLabelText()", "\n\t}\n");
        Assert.Contains(
            "if (Vintagestory.API.Config.OptimumConfig.FrameGenerationNextSession != Vintagestory.API.Config.OptimumConfig.FrameGeneration)",
            label);
        Assert.Contains("Lang.Get(\"optimum-framegeneration-restart\")", label);

        string rows = Between(gui, "private void optimumUpdateUpscalerRows()", "\n\t}\n");
        Assert.Contains("frameGenerationLabel.SetNewText(optimumFrameGenerationLabelText());", rows);
        Assert.Contains(
            "frameGeneration.SetSelectedValue(Vintagestory.API.Config.OptimumConfig.FrameGenerationNextSession ? \"on\" : \"off\");",
            rows);
    }

    /// <summary>
    /// The setting is declared off, persisted from the next session's value, loaded into
    /// both halves, reported, and its effective value is the conjunction the gates need.
    /// </summary>
    [Fact]
    public void TheFrameGenerationSettingIsDeclaredPersistedAndGated()
    {
        foreach (string config in new[]
        {
            Read("VintagestoryApi/Config/OptimumConfig.cs"),
            Read("sources/VintagestoryApi/Config/OptimumConfig.cs"),
        })
        {
            // No initializer on the statics is the CLR default, false; the data object
            // says false out loud so a config that never mentions it comes up off.
            Assert.Contains("public static bool FrameGeneration;", config);
            Assert.Contains("public static bool FrameGenerationNextSession;", config);
            Assert.Contains("public bool FrameGeneration { get; set; } = false;", config);
            Assert.Contains("FrameGeneration = data.FrameGeneration;", config);
            Assert.Contains("FrameGenerationNextSession = data.FrameGeneration;", config);
            Assert.Contains("FrameGeneration = FrameGenerationNextSession,", config);
            Assert.Contains("(nameof(OptimumConfigData.FrameGeneration), FrameGeneration.ToString()),", config);
            Assert.Contains("public static bool DisableFrameGenerationAtRuntime(string reason)", config);
            Assert.Contains(
                "FrameGeneration && EffectiveUpscalerIsDlss && !FrameGenerationRuntimeDisabled;", config);
        }
    }

    /// <summary>
    /// The truth table the two gates rely on, driven on the config: on only with the
    /// session setting on, DLSS the upscaler in effect, and no stand-down. The row's value
    /// (the next session's) never moves it.
    /// </summary>
    [Fact]
    public void EffectiveFrameGenerationNeedsTheSettingDlssAndNoStandDown()
    {
        OptimumConfigSnapshot snapshot = OptimumConfigSnapshot.Capture();
        bool upscalerWasDisabled = OptimumConfig.UpscalerRuntimeDisabled;
        try
        {
            OptimumConfig.ResetUpscalerRuntimeDisabledForTests();
            OptimumConfig.ResetFrameGenerationRuntimeDisabledForTests();

            OptimumConfig.FrameGeneration = true;
            OptimumConfig.Upscaler = "dlss";
            Assert.True(OptimumConfig.EffectiveFrameGeneration);

            foreach (string other in new[] { "off", "passthrough" })
            {
                OptimumConfig.Upscaler = other;
                Assert.False(OptimumConfig.EffectiveFrameGeneration);
            }
            OptimumConfig.Upscaler = "dlss";

            OptimumConfig.FrameGeneration = false;
            OptimumConfig.FrameGenerationNextSession = true;
            Assert.False(OptimumConfig.EffectiveFrameGeneration);
            OptimumConfig.FrameGeneration = true;
            OptimumConfig.FrameGenerationNextSession = false;
            Assert.True(OptimumConfig.EffectiveFrameGeneration);

            // An upscaler stand-down takes frame generation with it: DLSS is its input.
            OptimumConfig.DisableUpscalerAtRuntime();
            Assert.False(OptimumConfig.EffectiveFrameGeneration);
            OptimumConfig.ResetUpscalerRuntimeDisabledForTests();

            Assert.Null(OptimumConfig.FrameGenerationUnavailableReason);
            Assert.True(OptimumConfig.DisableFrameGenerationAtRuntime("no second graphics queue"));
            Assert.False(OptimumConfig.DisableFrameGenerationAtRuntime("a later reason"));
            Assert.False(OptimumConfig.EffectiveFrameGeneration);
            Assert.True(OptimumConfig.FrameGenerationRuntimeDisabled);
            Assert.Equal("no second graphics queue", OptimumConfig.FrameGenerationUnavailableReason);
            // The persisted choice is untouched by the stand-down.
            Assert.True(OptimumConfig.FrameGeneration);

            // A blank reason still stands down: it cannot be stored as "not stood down".
            OptimumConfig.ResetFrameGenerationRuntimeDisabledForTests();
            Assert.True(OptimumConfig.DisableFrameGenerationAtRuntime(""));
            Assert.True(OptimumConfig.FrameGenerationRuntimeDisabled);
            Assert.False(OptimumConfig.EffectiveFrameGeneration);
        }
        finally
        {
            OptimumConfig.ResetUpscalerRuntimeDisabledForTests();
            if (upscalerWasDisabled) OptimumConfig.DisableUpscalerAtRuntime();
            snapshot.Restore();
        }
    }

    /// <summary>
    /// The stand-down is a cross-thread path (render thread, present thread, settings
    /// dialog), so it is raced: many threads released together by a barrier, many rounds.
    /// Exactly one wins each round, the reason shown is the winner's, and a reader never
    /// sees "stood down" without a reason. Every wait is bounded so a hang fails the test.
    /// </summary>
    [Fact]
    public void TheFrameGenerationStandDownIsWonByExactlyOneThreadAndKeepsItsReason()
    {
        const int Threads = 8;
        const int Rounds = 300;
        OptimumConfigSnapshot snapshot = OptimumConfigSnapshot.Capture();
        try
        {
            for (int round = 0; round < Rounds; round++)
            {
                OptimumConfig.ResetFrameGenerationRuntimeDisabledForTests();
                using var barrier = new System.Threading.Barrier(Threads + 1);
                int winners = 0;
                string? winnerReason = null;
                int torn = 0;
                var workers = new System.Threading.Tasks.Task[Threads + 1];
                for (int t = 0; t < Threads; t++)
                {
                    string reason = "reason " + t;
                    workers[t] = System.Threading.Tasks.Task.Run(() =>
                    {
                        if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10))) return;
                        if (OptimumConfig.DisableFrameGenerationAtRuntime(reason))
                        {
                            System.Threading.Interlocked.Increment(ref winners);
                            System.Threading.Volatile.Write(ref winnerReason, reason);
                        }
                    });
                }
                workers[Threads] = System.Threading.Tasks.Task.Run(() =>
                {
                    if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10))) return;
                    for (int i = 0; i < 2000; i++)
                    {
                        bool down = OptimumConfig.FrameGenerationRuntimeDisabled;
                        string? why = OptimumConfig.FrameGenerationUnavailableReason;
                        // Read in that order, a stand-down seen means its reason is already there.
                        if (down && why == null) System.Threading.Interlocked.Increment(ref torn);
                    }
                });

                Assert.True(System.Threading.Tasks.Task.WaitAll(workers, TimeSpan.FromSeconds(30)),
                    "the stand-down race did not finish; something blocked");
                Assert.Equal(1, winners);
                Assert.Equal(0, torn);
                Assert.Equal(winnerReason, OptimumConfig.FrameGenerationUnavailableReason);
            }
        }
        finally
        {
            snapshot.Restore();
        }
    }

    /// <summary>
    /// The round trip through a real optimum.json: off by default, an explicit value
    /// survives load and save, and a Save from any row persists the next session's value,
    /// never the running one.
    /// </summary>
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData(null, false)]
    public void FrameGenerationRoundTripsThroughOptimumJson(string? json, bool expected)
    {
        OptimumConfigSnapshot snapshot = OptimumConfigSnapshot.Capture();
        string dataPath = Path.Combine(Path.GetTempPath(), "optimum-framegeneration-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dataPath, ".optimum"));
            File.WriteAllText(Path.Combine(dataPath, ".optimum", "shader-compatibility.json"),
                "{ \"ScanFailed\": false, \"DisabledFeatures\": [] }");
            OptimumConfig.SetDataPath(dataPath);
            string configPath = Path.Combine(dataPath, "ModConfig", "optimum.json");
            File.WriteAllText(configPath, json == null ? "{}" : "{ \"FrameGeneration\": " + json + " }");

            OptimumConfig.FrameGeneration = !expected;
            OptimumConfig.FrameGenerationNextSession = !expected;
            OptimumConfig.Load();
            Assert.Equal(expected, OptimumConfig.FrameGeneration);
            Assert.Equal(expected, OptimumConfig.FrameGenerationNextSession);
            Assert.Contains("\"FrameGeneration\": " + (expected ? "true" : "false"), File.ReadAllText(configPath));

            // The row changes the next session; the session keeps what it started with.
            OptimumConfig.FrameGenerationNextSession = !expected;
            OptimumConfig.Save();
            Assert.Equal(expected, OptimumConfig.FrameGeneration);
            Assert.Contains("\"FrameGeneration\": " + (!expected ? "true" : "false"), File.ReadAllText(configPath));
        }
        finally
        {
            snapshot.Restore();
            try { Directory.Delete(dataPath, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// OpenGL stands frame generation down at startup, in its own framebuffer setup, right
    /// beside the upscaler's stand-down and before anything reads the two gates - with one
    /// line, and touching nothing but the config and the platform's logger. The 2026-09-13
    /// crash was a stand-down that reached ShaderRegistry, whose type initializer publishes
    /// uncompiled programs into ShaderPrograms.* before vanilla has touched the type.
    /// </summary>
    [Fact]
    public void OpenGlStandsFrameGenerationDownWithoutTouchingAVanillaStatic()
    {
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        string setup = Between(platform, "public virtual List<FrameBufferRef> SetupDefaultFrameBuffers()", "\n\t}\n");

        const string StandDown = "if (Vintagestory.API.Config.OptimumConfig.FrameGeneration && Vintagestory.API.Config.OptimumConfig.DisableFrameGenerationAtRuntime(";
        int upscaler = setup.IndexOf("DisableOptimumUpscaler(\"this renderer cannot plan an upscale", StringComparison.Ordinal);
        int frameGeneration = setup.IndexOf(StandDown, StringComparison.Ordinal);
        int taa = setup.IndexOf("bool taaRequested =", StringComparison.Ordinal);
        int sceneGate = setup.IndexOf("OptimumSceneNoHudRequested", StringComparison.Ordinal);
        int uiGate = setup.IndexOf("OptimumUiTargetRequested", StringComparison.Ordinal);
        Assert.True(upscaler >= 0 && frameGeneration > upscaler, "the stand-down belongs beside the upscaler's");
        Assert.True(taa > frameGeneration, "the stand-down must come before the framebuffer build reads the config");
        Assert.True(sceneGate > frameGeneration && uiGate > frameGeneration,
            "both gates must be read after the stand-down, or the GL build allocates frame generation's inputs");

        string block = Between(setup.Substring(frameGeneration), StandDown, "\n\t\t}\n");
        Assert.Contains("logger.Notification(", block);
        Assert.Equal(1, Occurrences(block, "logger."));
        foreach (string forbidden in new[]
        {
            "ShaderRegistry", "ShaderPrograms", "ScreenManager", "ClientSettings", "RuntimeEnv",
            "DisableOptimumUpscaler", "ApplyOptimumLodBias", "Lang.",
        })
        {
            Assert.DoesNotContain(forbidden, block);
        }

        // The GL path alone: Vulkan overrides the whole setup and never calls this body.
        string vulkan = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.FrameBuffers.cs");
        Assert.Contains("public override List<FrameBufferRef> SetupDefaultFrameBuffers()", vulkan);
        Assert.DoesNotContain("base.SetupDefaultFrameBuffers", vulkan);
    }

    /// <summary>
    /// Every new settings-page member is a Cecil target; the gates and the setup were
    /// already transplanted and stay so.
    /// </summary>
    [Fact]
    public void EveryFrameGenerationMemberIsListedForTheTransplant()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");
        foreach (string member in new[]
        {
            "onOptimumFrameGenerationChanged", "optimumFrameGenerationUnavailable",
            "optimumFrameGenerationLabelText", "OptimumSceneNoHudRequested", "OptimumUiTargetRequested",
        })
        {
            Assert.Contains("\"" + member + "\"", patcher);
        }
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"SetupDefaultFrameBuffers\", 0)", patcher);
    }

    private static string LangValue(string key)
    {
        string lang = Read("sources/lang/en.json");
        string value = Between(lang, "\"" + key + "\": \"", "\",");
        return value.Substring(("\"" + key + "\": \"").Length);
    }

    // ---- helpers -----------------------------------------------------------

    private static int Occurrences(string text, string needle)
    {
        int count = 0;
        int at = 0;
        while (true)
        {
            int found = text.IndexOf(needle, at, StringComparison.Ordinal);
            if (found < 0) return count;
            count++;
            at = found + needle.Length;
        }
    }

    private static string Between(string text, string start, string end)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "not found: " + start);
        int to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to >= 0, "not found after " + start + ": " + end);
        return text[from..to];
    }

    private static string ReadPatchedOrSource(string patchPath, string sourcePath)
    {
        string? resolvedPatch = TryFind(patchPath);
        return resolvedPatch != null ? PatchReader.ReadPatchedContent(resolvedPatch) : Read(sourcePath);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }

    private static string? TryFind(string relativePath)
    {
        try
        {
            return PatchReader.FindRepositoryFile(relativePath);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
