using System.Text.Json;
using System.Text.Json.Nodes;
using Vintagestory.API.Client;

namespace VulkanStory.Settings;

// Shared source linked into the early menu integration and ordinary API-only mod.
internal sealed class RendererSettingsPanel
{
    private readonly JsonObject draft;
    private readonly Func<string, string?> save;
    private readonly Func<string, string?> preview;
    private string committed;
    private bool previewApplied;
    private readonly Action<string> notify;
    private readonly Action close;
    private readonly Func<bool>? editingActive;
    private readonly Func<string?> controllerOpened;
    private bool controllerPending;
    private int page;
    internal static readonly string[] PageNames = ["Image", "Generation", "Effects", "Device", "Status"];
    internal string CurrentPageName => PageNames[page];
    private bool refresh;
    private string? errorMessage;
    internal long ErrorRevision { get; private set; }
    internal double? ErrorContentY { get; private set; }
    private long nextStatusRefresh;
    private Action? refreshImageStatus;
    internal RendererSettingsPanel(string json, Func<string, string?> apply, Action<string> notify, Action close,
        Func<bool>? editingActive = null, Func<string?>? controllerOpened = null)
    {
        draft = JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("Renderer settings are unavailable.");
        committed = json;
        preview = AppContext.GetData("VulkanStory.Runtime.PreviewSettings") as Func<string, string?>
            ?? (_ => "Live renderer settings are unavailable.");
        save = apply; this.notify = notify; this.close = close;
        this.editingActive = editingActive; this.controllerOpened = controllerOpened ?? (() => { close(); return null; });
    }
    internal bool TakeRefresh()
    {
        bool value = refresh; refresh = false;
        if (Environment.TickCount64 >= nextStatusRefresh)
        {
            nextStatusRefresh = Environment.TickCount64 + 1000;
            if (page == 0) refreshImageStatus?.Invoke();
            if (page == 4) return true;
        }
        return value;
    }
    private bool Save()
    {
        string? error = save(draft.ToJsonString());
        if (error != null) { ShowError("Could not save: " + error); return true; }
        committed = draft.ToJsonString(); previewApplied = false;
        notify("VulkanStory settings saved. Options marked restart apply after restarting the game.");
        close(); return true;
    }
    private bool Cancel()
    {
        string? error = EndPreview();
        if (error != null) { ShowError("Could not restore settings: " + error); return true; }
        close(); return true;
    }
    internal string? EndPreview()
    {
        if (!previewApplied) return null;
        string? error = preview(committed);
        if (error == null) previewApplied = false;
        return error;
    }
    private void Preview()
    {
        string? error = preview(draft.ToJsonString());
        if (error == null) previewApplied = true;
        if (error != null || errorMessage != null)
            ShowError(error == null ? null : "Could not apply settings: " + error);
    }
    private void ShowError(string? error)
    {
        errorMessage = error;
        if (error != null) ErrorRevision++;
        refresh = true;
    }
    private bool SaveAndOpenController()
    {
        if (controllerPending) return true;
        if (AppContext.GetData("VulkanStory.Runtime.ControllerSettingsAfterSave") is not
            Func<string, Func<bool>, Action<string?>, string?> request)
        { ShowError("Controller settings are unavailable."); return true; }
        var weak = new WeakReference<RendererSettingsPanel>(this);
        controllerPending = true;
        string? error = request(draft.ToJsonString(),
            () => weak.TryGetTarget(out var panel) && panel.controllerPending && panel.editingActive?.Invoke() != false,
            result =>
            {
                if (!weak.TryGetTarget(out var panel) || !panel.controllerPending || panel.editingActive?.Invoke() == false) return;
                panel.controllerPending = false;
                panel.ShowError(result ?? panel.controllerOpened());
            });
        if (error != null) { controllerPending = false; ShowError(error); }
        else { committed = draft.ToJsonString(); previewApplied = false; }
        refresh = true;
        return true;
    }
    internal GuiComposer Compose(GuiComposer composer, Func<bool>? canInteract = null)
    {
        Action initialize = AddContent(composer, 550, out _, embedded: false, canInteract: canInteract);
        GuiComposer result = composer.EndChildElements().Compose();
        initialize();
        return result;
    }
    internal double AddPageNavigation(GuiComposer composer, double width, double y = 0,
        Func<bool>? canInteract = null)
    {
        string[] pages = PageNames;
        int columns = Math.Max(1, Math.Min(pages.Length, (int)(width / 110)));
        for (int index = 0; index < pages.Length; index++)
        {
            int selected = index;
            composer.AddSmallButton(pages[index], () =>
                { if (canInteract?.Invoke() != false && !controllerPending) { page = selected; refresh = true; } return true; },
                ElementBounds.Fixed(index % columns * 110, y + index / columns * 36, 100, 28),
                key: "vulkanstory-page-" + pages[index]);
        }
        return Math.Ceiling((double)pages.Length / columns) * 36 + 12;
    }
    internal Action AddContent(GuiComposer composer, double width, out double height, bool embedded = true,
        Func<bool>? canInteract = null, bool includeFooter = true, bool includePages = true)
    {
        bool Live() => canInteract?.Invoke() != false;
        bool initializing = true;
        bool Editable() => Live() && !controllerPending && !initializing;
        var initialize = new List<Action>();
        refreshImageStatus = null;
        ErrorContentY = null;
        double y = includePages ? AddPageNavigation(composer, width, canInteract: canInteract) : 0;
        double rowHeight = embedded ? 60 : 40;
        double labelHeight = embedded ? 46 : 26;
        bool stacked = embedded && width < 480;
        double controlX = stacked ? 0 : embedded ? width * 0.55 + 10 : 315;
        double controlWidth = width - controlX;
        double labelWidth = stacked ? width : controlX - 15;
        double controlY = y;
        double lastLabelHeight = 0;
        int textSequence = 0;
        switch (page)
        {
            case 0:
                Choice("Upscaler", "Upscaler", ["off", "dlss", "xess", "fsr3", "fsr4"], ["Off", "DLSS", "XeSS", "FSR 3.1", "FSR 4"]);
                if (draft["Upscaler"]?.GetValue<string>() == "xess")
                    Choice("Quality", "UpscalerQuality", ["dlaa", "ultraqualityplus", "ultraquality", "quality", "balanced", "performance", "ultraperformance"],
                        ["Native AA", "Ultra Quality+", "Ultra Quality", "Quality", "Balanced", "Performance", "Ultra Performance"]);
                else Choice("Quality", "UpscalerQuality", ["dlaa", "quality", "balanced", "performance", "ultraperformance"],
                    ["Native AA", "Quality", "Balanced", "Performance", "Ultra Performance"]);
                Slider("Render scale (%)", "RenderScale", 25, 100, 5, 100);
                Switch("Temporal anti-aliasing", "Taa");
                Slider("TAA sharpness (%)", "TaaSharpness", 0, 100, 5, 100);
                Slider("TAA mip bias", "TaaMipBias", -40, 0, 1, 10);
                Slider("Upscaler mip adjustment (%)", "UpscalerLodBiasOffset", 0, 100, 5, 100);
                const string imageStatusKey = "vulkanstory-image-status";
                string ImageStatus() => AppContext.GetData("VulkanStory.Runtime.Presentation") is Func<string> read
                    ? string.Join("\n", read().Split('\n').Where(line =>
                        line.StartsWith("Upscaler", StringComparison.Ordinal) || line.StartsWith("Render resolution:", StringComparison.Ordinal))) +
                        "\n" + (AppContext.GetData("VulkanStory.Runtime.FpsText") is Func<string> fps ? fps() : "FPS unavailable")
                    : "Renderer runtime is unavailable.";
                Label(ImageStatus(), CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, y, width, 125), imageStatusKey);
                refreshImageStatus = () => composer.GetDynamicText(imageStatusKey).SetNewText(ImageStatus(), false, true, false);
                y += Math.Max(135, lastLabelHeight + 10);
                Text("Quality controls the selected upscaler's internal resolution. Render scale applies when the upscaler is Off. FPS gains can be limited by VSync, a frame cap or the CPU.");
                break;
            case 1:
                Choice("Frame generation", "FrameGeneration", ["off", "dlss", "xess", "fsr3"], ["Off", "DLSS-G", "XeSS-FG", "FSR 3 FG"]);
                Slider("Requested multiplier", "FrameGenerationMultiplier", 2, 6, 1, 1);
                Choice("Low latency", "LowLatencyMode", ["off", "on", "boost"], ["Off", "On", "Boost"]);
                Switch("Show FPS counter", "ShowFpsCounter");
                Text("Available providers and multipliers depend on your GPU. Unsupported settings fall back automatically.");
                Text("With frame generation selected, low latency remains enabled. Boost depends on the provider.");
                break;
            case 2:
                Choice("Ambient occlusion", "AmbientOcclusion", ["auto", "vanilla", "gtao"], ["Auto", "SSAO", "GTAO"]);
                Choice("AO quality", "AmbientOcclusionPreset", ["low", "medium", "high", "ultra"], ["Low", "Medium", "High", "Ultra"]);
                Switch("Show ambient occlusion", "AmbientOcclusionDebugView");
                Switch("Reduce god-ray sample cost", "GodRaysSampleCap");
                Switch("Handheld shadow tier", "HandheldShadowTier");
                DebugView();
                break;
            case 3:
                Switch("Enable VulkanStory (restart)", "Enabled");
                Switch("Controllers", "ControllerEnabled");
                Switch("Touch input", "TouchEnabled");
                Switch("Native shaders (restart)", "NativeShaders");
                Switch("Streamline (restart)", "Streamline");
                Text("Controller remapping, gyro and haptics are available from the controller settings panel.");
                composer.AddSmallButton("Save and open controller settings", () => Editable() ? SaveAndOpenController() : true,
                    ElementBounds.Fixed(0, y, width, 30));
                y += 42;
                break;
            default:
                string status = AppContext.GetData("VulkanStory.Runtime.Presentation") is Func<string> read
                    ? read() : "Renderer runtime is unavailable.";
                foreach (string line in status.Split('\n'))
                {
                    Label(line, CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, y, width, embedded ? 65 : 35));
                    y += embedded ? lastLabelHeight + 12 : Math.Max(40, lastLabelHeight + 10);
                }
                Text("Input preparation is not proof that generated frames were presented. Reported counts are shown when available.");
                break;
        }
        if (errorMessage != null) { ErrorContentY = y; Text(errorMessage); }
        if (controllerPending) Text("Applying settings and opening controller settings...");
        Text(controllerPending
            ? "Settings have been saved. Cancel stops opening the controller panel; saved settings remain applied."
            : "Changes apply immediately. Save keeps them; Cancel restores the previous settings. Options marked restart still require a restart.");
        if (includeFooter) AddFooter(composer, width, y + 8, canInteract);
        height = y + (includeFooter ? 46 : 0);
        return () => { foreach (var action in initialize) action(); initializing = false; };

        void Text(string label)
        { Label(label, CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, y, width, embedded ? 75 : 55)); y += Math.Max(embedded ? 85 : 65, lastLabelHeight + 10); }
        GuiComposer Label(string text, CairoFont font, ElementBounds bounds, string? elementKey = null)
        {
            lastLabelHeight = bounds.fixedHeight;
            controlY = y;
            if (!embedded && elementKey == null) return composer.AddStaticText(text, font, bounds);
            if (string.IsNullOrEmpty(text)) return composer;
            var label = new GuiElementDynamicText(composer.Api, text, font, bounds);
            composer.AddInteractiveElement(label, elementKey ?? "vulkanstory-text-" + textSequence++);
            bounds.CalcWorldBounds();
            label.AutoHeight();
            lastLabelHeight = bounds.fixedHeight;
            controlY = y + (stacked ? lastLabelHeight + 6 : 0);
            return composer;
        }
        void DebugView()
        {
            string[] labels = ["Off", "Motion vectors", "Reactive mask", "Writer validity", "Motion overlay"];
            int value = Math.Clamp(draft["TaaDebugView"]?.Deserialize<int>() ?? 0, 0, labels.Length - 1);
            Label("Temporal debug view", CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, y, labelWidth, labelHeight))
                .AddSmallButton(labels[value], () => { if (Editable()) { draft["TaaDebugView"] = (value + 1) % labels.Length; Preview(); refresh = true; } return true; },
                    ElementBounds.Fixed(controlX, controlY, controlWidth, 28));
            y += stacked ? lastLabelHeight + 46 : Math.Max(rowHeight, lastLabelHeight + 12);
        }
        void Switch(string label, string key)
        {
            bool value = draft[key]?.GetValue<bool>() ?? false;
            Label(label, CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, y, labelWidth, labelHeight))
                .AddSwitch(next => { if (Editable()) { draft[key] = next; Preview(); } }, ElementBounds.Fixed(controlX, controlY, 35, 26), key);
            initialize.Add(() => composer.GetSwitch(key).SetValue(value)); y += stacked ? lastLabelHeight + 46 : Math.Max(rowHeight, lastLabelHeight + 12);
        }
        void Slider(string label, string key, int min, int max, int step, float scale)
        {
            int value = (int)MathF.Round((draft[key]?.Deserialize<float>() ?? 0f) * scale);
            string? valueKey = key == "FrameGenerationMultiplier" ? "vulkanstory-multiplier-value" : null;
            string Caption(int current) => valueKey == null ? label : label + " (" + current + "x)";
            Label(Caption(Math.Clamp(value, min, max)), CairoFont.WhiteSmallishText(),
                ElementBounds.Fixed(0, y, labelWidth, labelHeight), valueKey)
                .AddSlider(next =>
                {
                    if (!Editable()) return true;
                    draft[key] = next / scale;
                    Preview();
                    if (valueKey != null)
                        composer.GetDynamicText(valueKey).SetNewText(Caption(next), false, true, false);
                    return true;
                }, ElementBounds.Fixed(controlX, controlY, controlWidth, 26), key);
            initialize.Add(() => composer.GetSlider(key).SetValues(Math.Clamp(value, min, max), min, max, step)); y += stacked ? lastLabelHeight + 46 : Math.Max(rowHeight, lastLabelHeight + 12);
        }
        void Choice(string label, string key, string[] values, string[] labels)
        {
            int index = Array.FindIndex(values, value => value == draft[key]?.GetValue<string>());
            if (index < 0) index = 0;
            Label(label, CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, y, labelWidth, labelHeight))
                .AddSmallButton(labels[index], () =>
                {
                    if (!Editable()) return true;
                    draft[key] = values[(index + 1) % values.Length];
                    if (key == "Upscaler" && draft[key]?.GetValue<string>() != "xess" &&
                        draft["UpscalerQuality"]?.GetValue<string>() is "ultraquality" or "ultraqualityplus")
                        draft["UpscalerQuality"] = "quality";
                    Preview();
                    refresh = true; return true;
                },
                    ElementBounds.Fixed(controlX, controlY, controlWidth, 28));
            y += stacked ? lastLabelHeight + 46 : Math.Max(rowHeight, lastLabelHeight + 12);
        }
    }
    internal GuiComposer AddFooter(GuiComposer composer, double width, double y, Func<bool>? canInteract = null)
    {
        bool Live() => canInteract?.Invoke() != false;
        return composer.AddSmallButton("Cancel", () => Live() ? Cancel() : true,
                ElementBounds.Fixed(width - 260, y, 120, 30))
            .AddSmallButton("Save", () => Live() && !controllerPending ? Save() : true,
                ElementBounds.Fixed(width - 120, y, 120, 30));
    }
}
