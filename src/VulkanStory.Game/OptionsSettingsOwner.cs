using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Settings;
using VulkanStory.Game.Input;

namespace VulkanStory.Game;

// Sidecar state only: the official Options composite and its host remain intact.
internal sealed class OptionsSettingsOwner
{
    private static readonly ConditionalWeakTable<GuiCompositeSettings, OptionsSettingsOwner> Owners = new();
    private static readonly List<WeakReference<OptionsSettingsOwner>> Known = new();
    private static readonly FieldInfo Handler = AccessTools.Field(typeof(GuiCompositeSettings), "handler");
    private static readonly FieldInfo Composer = AccessTools.Field(typeof(GuiCompositeSettings), "composer");
    private static readonly MethodInfo Graphics = AccessTools.Method(typeof(GuiCompositeSettings), "OnGraphicsOptions", [typeof(bool)]);
    private static readonly Type HostInterface = AccessTools.TypeByName("Vintagestory.Client.NoObf.IGuiCompositeHandler");
    private static readonly MethodInfo Load = HostInterface.GetMethod("LoadComposer")!;
    private static readonly MethodInfo ApiGetter = HostInterface.GetProperty("Api")!.GetMethod!;
    private static readonly MethodInfo ManagerGetter = HostInterface.GetProperty("GuiComposers")!.GetMethod!;
    private static readonly FieldInfo Sidebar = AccessTools.Field(typeof(ScreenManager), "guiMainmenuLeft");
    private static readonly MethodInfo SidebarWidth = AccessTools.PropertyGetter(typeof(GuiCompositeMainMenuLeft), "Width");
    private static long sequence;
    private readonly WeakReference<GuiCompositeSettings> composite;
    private readonly WeakReference<object> host;
    private readonly string cachePrefix = "gamesettings-vulkanstory-" + Interlocked.Increment(ref sequence) + "-";
    private long composition;
    private long editingGeneration;
    private RendererSettingsPanel? panel;
    private GuiComposer? current;
    private string? cacheKey;
    private bool returnRequested;
    private bool? savedShowMainMenu;
    private float scrollOffset;
    private long shownErrorRevision;
    private string? composedPage;
    private (int Width, int Height, double Scale)? lastGeometry;
    private (int Width, int Height, double Scale)? failedGeometry;

    internal static void ValidateProfile()
    {
        if (Handler == null || Composer?.FieldType != typeof(GuiComposer) || Graphics?.ReturnType != typeof(void) ||
            Load?.ReturnType != typeof(void) || ApiGetter?.ReturnType != typeof(ICoreClientAPI) ||
            ManagerGetter?.ReturnType != typeof(GuiComposerManager) || Sidebar == null || SidebarWidth?.ReturnType != typeof(double))
            throw new InvalidOperationException("Original Options owner bindings changed.");
    }
    private OptionsSettingsOwner(GuiCompositeSettings owner)
    {
        composite = new(owner); host = new(Handler.GetValue(owner)!); Known.Add(new(this));
    }
    internal static OptionsSettingsOwner Get(GuiCompositeSettings owner) => Owners.GetValue(owner, value => new(value));
    private ICoreClientAPI Api(object owner) => (ICoreClientAPI)ApiGetter.Invoke(owner, null)!;
    private GuiComposerManager Manager(object owner) => (GuiComposerManager)ManagerGetter.Invoke(owner, null)!;

    internal void AddTab(GuiComposer composer, string currentTab)
    {
        // Graphics is the original Options landing tab in both hosts. Its
        // content starts at y=82; other vanilla tabs have different layouts.
        // Never mutate vanilla header bounds: failed custom composition cannot corrupt them.
        Clear();
        if (currentTab != "graphics") return;
        var weakState = new WeakReference<OptionsSettingsOwner>(this);
        var weakComposer = new WeakReference<GuiComposer>(composer);
        composer.AddToggleButton("VulkanStory", CairoFont.ButtonText().WithFontSize(18), on =>
        {
            if (on && weakState.TryGetTarget(out var state) && weakComposer.TryGetTarget(out var entry) &&
                state.host.TryGetTarget(out var owner) && ReferenceEquals(state.Shown(owner), entry)) state.Open();
        },
            ElementBounds.Fixed(0, 46, 145, 28), "vulkanstory");
        long generation = editingGeneration;
        composer.OnComposed += () =>
        {
            if (weakState.TryGetTarget(out var state) && weakComposer.TryGetTarget(out var entry))
                state.KeepEntryVisible(entry, generation);
        };
    }
    private void KeepEntryVisible(GuiComposer composer, long generation)
    {
        if (!RuntimeBootstrap.IsActive || RuntimeBootstrap.Current.Session.Stopping || !host.TryGetTarget(out var owner) ||
            (generation != editingGeneration && !ReferenceEquals(Shown(owner), composer))) return;
        var entry = composer.GetToggleButton("vulkanstory");
        if (entry == null) return;
        var (width, height, scale) = Geometry();
        if (!double.IsFinite(scale) || scale <= 0 || width <= 16 || height <= 16) return;
        double left = 0;
        if (owner is GuiScreen screen && screen.ShowMainMenu && Sidebar.GetValue(screen.ScreenManager) is { } sidebar)
        {
            left = (double)SidebarWidth.Invoke(sidebar, null)!;
            left = double.IsFinite(left) ? Math.Clamp(left, 0, width) : 0;
            if (width - left < entry.Bounds.OuterWidth + 16)
            {
                savedShowMainMenu ??= screen.ShowMainMenu;
                screen.ShowMainMenu = false;
                left = 0;
            }
        }
        ElementBounds root = composer.Bounds;
        // Move only this composer's root after original composition. Child
        // render/hit coordinates inherit these offsets, as does its text texture.
        // Never mutate the game's shared per-tab header bounds.
        if (owner is not GuiScreen) root.absOffsetX = root.fixedOffsetX * scale;
        root.absOffsetY = root.fixedOffsetY * scale;
        root.absOffsetX += Math.Max(0, left + 8 - entry.Bounds.renderX);
        root.absOffsetY += Math.Max(0, 8 - root.renderY);
        root.absOffsetY -= Math.Max(0, entry.Bounds.renderY + entry.Bounds.OuterHeight - (height - 8));
    }
    private void Open()
    {
        if (!host.TryGetTarget(out var owner) ||
            AppContext.GetData("VulkanStory.Runtime.ReadSettings") is not Func<string> read ||
            AppContext.GetData("VulkanStory.Runtime.ApplySettings") is not Func<string, string?> save) return;
        long generation = ++editingGeneration;
        var weakState = new WeakReference<OptionsSettingsOwner>(this);
        panel = new RendererSettingsPanel(read(), json =>
            weakState.TryGetTarget(out var state) && state.EditingLive(generation)
                ? save(json) : "The Options editing session is no longer active.",
            message =>
            {
                if (weakState.TryGetTarget(out var state) && state.EditingLive(generation) && state.host.TryGetTarget(out var liveOwner))
                    state.Api(liveOwner).Logger.Notification("{0}", message);
            }, () =>
            {
                if (weakState.TryGetTarget(out var state) && state.EditingLive(generation)) state.returnRequested = true;
            }, editingActive: () => weakState.TryGetTarget(out var state) && state.EditingLive(generation),
            controllerOpened: () => weakState.TryGetTarget(out var state)
                ? state.ControllerOpened(generation) : "The Options owner closed.");
        scrollOffset = 0; shownErrorRevision = 0; failedGeometry = null; TryCompose(owner);
    }
    private string? ControllerOpened(long generation)
    {
        if (!EditingLive(generation) || !host.TryGetTarget(out var owner) || owner is not GuiDialog parent ||
            !composite.TryGetTarget(out var target) || !RuntimeBootstrap.IsActive ||
            RuntimeBootstrap.Current.Session.Temporal.CurrentClient is not { } world)
            return "Controller settings require the active in-game Options dialog.";
        ControllerSettingsDialog? child = RuntimeBootstrap.Current.Session.OpenedControllerSettings(world);
        if (child == null) return "The controller settings dialog did not open.";
        var weakParent = new WeakReference<GuiDialog>(parent);
        var weakTarget = new WeakReference<GuiCompositeSettings>(target);
        var weakWorld = new WeakReference<ClientMain>(world);
        var weakChild = new WeakReference<ControllerSettingsDialog>(child);
        Action? closed = null;
        closed = () =>
        {
            if (weakChild.TryGetTarget(out var dialog)) dialog.OnClosed -= closed;
            if (!weakChild.TryGetTarget(out var actualChild) || !weakWorld.TryGetTarget(out var originalWorld) ||
                !RuntimeBootstrap.IsActive || RuntimeBootstrap.Current.Session.Stopping ||
                !ReferenceEquals(RuntimeBootstrap.Current.Session.Temporal.CurrentClient, originalWorld) ||
                !RuntimeBootstrap.Current.Session.OwnsControllerSettings(originalWorld, actualChild) ||
                !weakParent.TryGetTarget(out var originalParent) || !weakTarget.TryGetTarget(out var originalTarget)) return;
            if (originalParent.TryOpen()) InvokeGraphics(originalTarget);
        };
        child.OnClosed += closed;
        if (!parent.TryClose())
        {
            child.OnClosed -= closed;
            child.TryClose();
            parent.Focus();
            return "The parent Options dialog could not close; saved settings remain applied.";
        }
        child.Focus();
        return null;
    }
    private (int Width, int Height, double Scale) Geometry()
    {
        var size = RuntimeBootstrap.Current.Session.Window.PixelSize;
        return (size.Width, size.Height, ClientSettings.GUIScale);
    }
    private GuiComposer? Shown(object owner) => owner switch
    {
        GuiScreen screen when screen.IsOpened => screen.ElementComposer,
        GuiDialog dialog when dialog.IsOpened() => dialog.SingleComposer,
        _ => null
    };
    private bool EditingLive(long generation) => generation == editingGeneration && !returnRequested && panel != null &&
        current != null && host.TryGetTarget(out var owner) && ReferenceEquals(Shown(owner), current);
    private void Tick(object owner)
    {
        if (current == null || !ReferenceEquals(Shown(owner), current)) { Clear(); return; }
        if (returnRequested)
        {
            return;
        }
        var geometry = Geometry();
        if (geometry == failedGeometry) return;
        if (panel?.TakeRefresh() == true || geometry != lastGeometry) TryCompose(owner);
    }
    private void TryCompose(object owner)
    {
        var geometry = Geometry();
        string key = cachePrefix + ++composition;
        var manager = Manager(owner);
        float savedOffset = scrollOffset;
        float requestedOffset = panel?.CurrentPageName == composedPage ? savedOffset : 0;
        string? oldKey = null;
        try
        {
            var (pixelsWide, pixelsHigh, scale) = geometry;
            if (!double.IsFinite(scale) || scale <= 0) throw new InvalidOperationException("Invalid GUI scale.");
            double sidebar = owner is GuiScreen screen && screen.ShowMainMenu && Sidebar.GetValue(screen.ScreenManager) is { } left
                ? (double)SidebarWidth.Invoke(left, null)! : 0;
            double padding = GuiStyle.ElementToDialogPadding;
            double width = Math.Min(580, (pixelsWide - sidebar) / scale - 2 * padding - 20);
            double height = Math.Min(620, pixelsHigh / scale - 2 * padding - 30);
            if (width < 300 || height < 180)
                throw new InvalidOperationException("Resize the window or lower GUI scale to open VulkanStory Options.");
            double contentWidth = width - 30;
            var background = ElementBounds.Fixed(0, 0, width, height).WithFixedPadding(padding);
            var root = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterFixed);
            // Main-menu host normally centers to the right of its sidebar.
            root.fixedOffsetX = sidebar / (2 * scale);
            long generation = editingGeneration;
            GuiComposer? replacement = null;
            bool published = false;
            bool LiveComposition() => EditingLive(generation) && ReferenceEquals(current, replacement);
            replacement = manager.Create(key, root).AddShadedDialogBG(background, withTitleBar: false)
                .BeginChildElements(background)
                .AddSmallButton("Graphics", () => { if (LiveComposition()) returnRequested = true; return true; }, ElementBounds.Fixed(0, 0, 120, 28))
                .AddToggleButton("VulkanStory", CairoFont.ButtonText().WithFontSize(18), _ => { }, ElementBounds.Fixed(135, 0, 145, 28), "vulkanstory");
            double navigationHeight = panel!.AddPageNavigation(replacement, contentWidth, 45, LiveComposition);
            double contentTop = 45 + navigationHeight;
            double visibleHeight = height - 95 - navigationHeight;
            if (visibleHeight < 40)
                throw new InvalidOperationException("Resize the window or lower GUI scale to show Options navigation and content.");
            var clip = ElementBounds.Fixed(0, contentTop, contentWidth, visibleHeight);
            var body = ElementBounds.Fixed(0, 0, contentWidth, 1).WithParent(clip);
            replacement.AddVerticalScrollbar(value =>
            {
                // SetHeights/restore run before publication and must still move
                // this new body; a later callback from a replaced composer may not.
                if (published && !LiveComposition()) return;
                scrollOffset = value; body.fixedY = -value;
                body.MarkDirtyRecursive(); body.CalcWorldBounds();
            }, ElementBounds.Fixed(width - 20, contentTop, 20, visibleHeight), "vulkanstory-scroll")
                .BeginClip(clip).BeginChildElements(body);
            Action initialize = panel!.AddContent(replacement, contentWidth, out double contentHeight,
                canInteract: LiveComposition, includeFooter: false, includePages: false);
            body.fixedHeight = contentHeight;
            replacement.EndChildElements().EndClip();
            panel.AddFooter(replacement, contentWidth, height - 30, LiveComposition);
            replacement.EndChildElements().Compose();
            initialize(); replacement.GetToggleButton("vulkanstory").SetValue(true);
            var scrollbar = replacement.GetScrollbar("vulkanstory-scroll");
            scrollbar.SetHeights((float)visibleHeight, (float)Math.Max(visibleHeight, contentHeight));
            if (panel.ErrorRevision != shownErrorRevision && panel.ErrorContentY is double errorY)
                requestedOffset = (float)errorY;
            scrollbar.CurrentYPosition = Math.Clamp(requestedOffset, 0, (float)Math.Max(0, contentHeight - visibleHeight));
            scrollbar.TriggerChanged();
            // Publish a complete replacement with fresh bounds and cache name.
            Load.Invoke(owner, [replacement]);
            if (composite.TryGetTarget(out var target)) Composer.SetValue(target, replacement);
            oldKey = cacheKey;
            current = replacement; cacheKey = key; lastGeometry = geometry; failedGeometry = null;
            composedPage = panel.CurrentPageName;
            shownErrorRevision = panel.ErrorRevision;
            published = true;
        }
        catch (Exception error)
        {
            manager.Dispose(key); scrollOffset = savedOffset; failedGeometry = geometry;
            Api(owner).Logger.Warning("VulkanStory Options: {0}", error.GetBaseException().Message);
            if (current == null)
            {
                Shown(owner)?.GetToggleButton("vulkanstory")?.SetValue(false);
                panel = null;
            }
            return;
        }
        // Cleanup is after publication; a cleanup failure must not discard the
        // new composer that the host now renders.
        if (oldKey != null) manager.Dispose(oldKey);
    }
    private void Clear()
    {
        if (panel?.EndPreview() is string error && host.TryGetTarget(out var previewOwner))
            Api(previewOwner).Logger.Warning("VulkanStory: could not restore preview settings: {0}", error);
        editingGeneration++;
        if (current != null && composite.TryGetTarget(out var target) && ReferenceEquals(Composer.GetValue(target), current))
            Composer.SetValue(target, null);
        if (host.TryGetTarget(out var owner))
        {
            if (savedShowMainMenu is { } show && owner is GuiScreen menuScreen) menuScreen.ShowMainMenu = show;
            savedShowMainMenu = null;
            // The host also disposes its displayed composer. Detach our entry
            // before removing the manager cache to prevent duplicate disposal.
            if (current != null && owner is GuiScreen screen && ReferenceEquals(screen.ElementComposer, current))
                screen.ElementComposer = null!;
            if (current != null && owner is GuiDialog dialog && ReferenceEquals(dialog.SingleComposer, current))
                dialog.Composers.Remove("single");
            if (cacheKey != null) Manager(owner).Dispose(cacheKey);
        }
        panel = null; current = null; cacheKey = null; returnRequested = false;
        scrollOffset = 0; shownErrorRevision = 0; lastGeometry = null; failedGeometry = null;
        composedPage = null;
    }
    private static IEnumerable<OptionsSettingsOwner> Live()
    {
        for (int index = Known.Count - 1; index >= 0; index--)
            if (Known[index].TryGetTarget(out var state)) yield return state;
            else Known.RemoveAt(index);
    }
    internal static void RenderHost(object owner)
    {
        foreach (var state in Live())
            if (state.host.TryGetTarget(out var target) && ReferenceEquals(owner, target) && state.panel != null) state.Tick(owner);
    }
    internal static void ApplyPendingReturns()
    {
        foreach (var state in Live())
        {
            if (!state.returnRequested) continue;
            if (state.current == null || !state.host.TryGetTarget(out var owner) ||
                !ReferenceEquals(state.Shown(owner), state.current) || !state.composite.TryGetTarget(out var target))
            { state.Clear(); continue; }
            state.returnRequested = false;
            InvokeGraphics(target);
        }
    }
    private static void InvokeGraphics(GuiCompositeSettings target)
    {
        try { Graphics.Invoke(target, [true]); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }
    internal static void CloseHost(object owner)
    {
        foreach (var state in Live())
            if (state.host.TryGetTarget(out var target) && ReferenceEquals(owner, target)) state.Clear();
    }
    internal static void ClearAll() { foreach (var state in Live()) state.Clear(); }
    internal static string? DiagnosticPage(GuiComposer composer) =>
        Live().FirstOrDefault(state => ReferenceEquals(state.current, composer))?.panel?.CurrentPageName;
}
