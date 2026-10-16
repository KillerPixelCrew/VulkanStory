using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Common.Input;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vintagestory.Client.NoObf;
using VulkanStory.Platform.Sdl;
using Window = OpenTK.Windowing.GraphicsLibraryFramework.Window;
using WindowImage = OpenTK.Windowing.Common.Input.Image;

namespace VulkanStory.Game;

/// <summary>OpenTK managed windows backed by the session's SDL pump and Vulkan device.</summary>
/// <remarks>Window pointers are opaque identity tokens, never native GLFW pointers. All window operations
/// remain on the session thread, and only the session drains SDL's shared event queue.</remarks>
internal static unsafe partial class ManagedWindowBridge
{
    private const string PatchOwner = "vulkanstory.routing.managed-windows";
    private static ProcessRuntime? runtime;
    private static Action? installAfterWindowCommit;
    private static readonly ConditionalWeakTable<NativeWindow, Entry> Windows = new();
    private static readonly Dictionary<nint, Entry> Tokens = new();
    private static readonly ConditionalWeakTable<GameRenderSession, ShareGroup> Groups = new();
    private static readonly HashSet<MethodBase> SupportedManagedEntries = [];
    private static long nextToken = 0x60000000;
    [ThreadStatic] private static Entry? current;
    [ThreadStatic] private static bool preparingPrimary;
    private static bool ManagedWindowRouting => preparingPrimary || runtime?.IsActive == true;
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>Matches an exact managed window entry point admitted by the prepared routing group.</summary>
    /// <remarks>Unsupported guards and arbitrary GLFW/native loading functions are never capabilities.</remarks>
    internal static bool SupportsManagedCall(Mono.Cecil.MethodReference reference)
    {
        if (reference.HasGenericParameters || reference is Mono.Cecil.GenericInstanceMethod) return false;
        string owner = reference.DeclaringType.FullName.Replace('/', '+');
        return SupportedManagedEntries.Any(method => method.DeclaringType?.FullName == owner && method.Name == reference.Name &&
            ManagedTypeName(method is MethodInfo info ? info.ReturnType : typeof(void)) == reference.ReturnType.FullName.Replace('/', '+') &&
            method.GetParameters().Select(parameter => ManagedTypeName(parameter.ParameterType)).SequenceEqual(
                reference.Parameters.Select(parameter => parameter.ParameterType.FullName.Replace('/', '+')), StringComparer.Ordinal));
    }

    private static string ManagedTypeName(Type type) => type.IsByRef ? ManagedTypeName(type.GetElementType()!) + "&" :
        type.IsPointer ? ManagedTypeName(type.GetElementType()!) + "*" : type.IsArray ? ManagedTypeName(type.GetElementType()!) +
            "[" + new string(',', type.GetArrayRank() - 1) + "]" : type.FullName!;

    private sealed class ShareGroup(GameRenderSession session)
    {
        internal readonly GameRenderSession Session = session;
        internal ManagedGlBridge Gl => Session.ManagedGl;
        internal readonly List<Entry> Entries = [];
        internal GameWindowNative? Primary;
    }

    private sealed class Entry(ShareGroup group, NativeWindow facade, SdlWindowHost host, bool primary)
    {
        internal readonly ShareGroup Group = group;
        internal readonly NativeWindow Facade = facade;
        internal readonly SdlWindowHost Host = host;
        internal readonly bool Primary = primary;
        internal readonly nint Token = checked((nint)Interlocked.Increment(ref nextToken));
        internal readonly int OwnerThread = Environment.CurrentManagedThreadId;
        internal int AuxiliaryTarget;
        internal int SwapInterval;
        internal bool Disposed, Exiting;
        internal string Title = "";
        internal KeyModifiers Modifiers;
        internal readonly Stopwatch UpdateClock = Stopwatch.StartNew();
        internal KeyboardState Keyboard = null!;
        internal MouseState Mouse = null!;
        internal void RequireOwner()
        {
            if (Environment.CurrentManagedThreadId != OwnerThread)
                throw new InvalidOperationException("Managed SDL windows belong to the session owner thread.");
            ObjectDisposedException.ThrowIf(Disposed, Facade);
        }
        internal void MakeCurrent()
        {
            RequireOwner();
            var size = Host.PixelSize;
            int target = Primary ? 0 : Group.Session.Device.AuxiliaryFramebuffer(AuxiliaryTarget);
            Group.Gl.SelectContext(Token, target, size.Width, size.Height);
            current = this;
        }
        internal void SwapBuffers()
        {
            RequireOwner();
            // The game owns primary presentation, including SR/FG. Auxiliary swaps
            // enqueue only this window on the device's ordinary presentation path.
            if (!Primary) Group.Session.Device.QueueAuxiliaryPresent(AuxiliaryTarget);
        }
    }

    private sealed class Context(Entry entry) : IGLFWGraphicsContext
    {
        public nint WindowPtr => entry.Token;
        public bool IsCurrent => !entry.Disposed && ReferenceEquals(current, entry) && ReferenceEquals(ManagedGlBridge.Current, entry.Group.Gl);
        public int SwapInterval
        {
            get
            {
                entry.RequireOwner();
                return entry.Primary ? (entry.Group.Session.Device.PresentationVsyncEnabled ? 1 : 0) : entry.SwapInterval;
            }
            set
            {
                entry.RequireOwner();
                if (value is < -1 or > 1) throw new ArgumentOutOfRangeException(nameof(value));
                entry.SwapInterval = value;
                if (entry.Primary)
                {
                    // Preserve the game's frame-limiter mode when VSync is already on.
                    // The session applies this same setting at its next frame boundary.
                    if (value == 0) ClientSettings.VsyncMode = 0;
                    else if (ClientSettings.VsyncMode == 0) ClientSettings.VsyncMode = 1;
                }
                else entry.Group.Session.Device.SetAuxiliaryVsync(entry.AuxiliaryTarget, value != 0);
            }
        }
        public void MakeCurrent() => entry.MakeCurrent();
        public void MakeNoneCurrent()
        {
            entry.RequireOwner();
            if (!ReferenceEquals(current, entry)) return;
            entry.Group.Gl.SelectContext(0, 0, 0, 0); current = null;
        }
        public void SwapBuffers() => entry.SwapBuffers();
    }

    internal static GameWindowNative CreatePrimary(GameRenderSession session)
    {
        if (runtime?.IsActive != true || !ReferenceEquals(runtime.Session, session))
            throw new InvalidOperationException("Managed window routing requires the committed SDL/Vulkan session.");
        // Patching OpenTK types can run their one-shot static initialization.
        // Keep native display startup intact until the original window request
        // has selected and committed SDL/Vulkan, including the disabled path.
        Action? install = installAfterWindowCommit;
        installAfterWindowCommit = null;
        install?.Invoke();
        SdlNativeLibrary.RegisterAssemblyImports(typeof(ManagedWindowBridge).Assembly);
        ShareGroup group = Groups.GetValue(session, value => new(value));
        if (group.Primary != null) return group.Primary;
        preparingPrimary = true;
        try
        {
            var facade = (GameWindowNative)RuntimeHelpers.GetUninitializedObject(typeof(GameWindowNative));
            GC.SuppressFinalize(facade); // No original GLFW cleanup is valid for this object, including initialization failure.
            var entry = new Entry(group, facade, session.Window, true);
            // NativeWindowSettings' real constructor initializes GLFW and queries a native
            // monitor. The primary SDL host already exists, so initialize only its managed settings.
            var settings = (NativeWindowSettings)RuntimeHelpers.GetUninitializedObject(typeof(NativeWindowSettings));
            settings.Title = Marshal.PtrToStringUTF8(SDL_GetWindowTitle(session.Window.NativeHandle)) ?? "";
            settings.ClientSize = Size(entry.Host.WindowSize);
            settings.API = ContextAPI.OpenGL; settings.Profile = ContextProfile.Core;
            settings.APIVersion = new Version(3, 3);
            Initialize(entry, settings);
            InitializeGameWindow(facade, GameWindowSettings.Default);
            group.Primary = facade;
            entry.MakeCurrent();
            return facade;
        }
        finally { preparingPrimary = false; }
    }

    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner)
    {
        var harmony = new Harmony(PatchOwner);
        bool attempted = false;
        List<(MethodBase Method, MethodInfo Prefix)> bindings = [];
        return new StartupPatchGroup("graphics-managed-windows", () =>
        {
            bindings.Clear();
            Add(typeof(NativeWindow).GetConstructor([typeof(NativeWindowSettings)])!, nameof(ConstructNative));
            Add(typeof(GameWindow).GetConstructor([typeof(GameWindowSettings), typeof(NativeWindowSettings)])!, nameof(ConstructGame));
            string[] properties = ["MousePosition", "VSync", "AutoIconify", "Icon", "ClipboardString", "Title", "CurrentMonitor",
                "IsFocused", "IsVisible", "Exists", "IsExiting", "WindowState", "WindowBorder", "AlwaysOnTop", "Bounds", "Location",
                "ClientLocation", "Size", "ClientSize", "FramebufferSize", "ClientRectangle", "IsFullscreen"];
            foreach (string name in properties)
            {
                PropertyInfo property = typeof(NativeWindow).GetProperty(name) ?? throw new MissingMemberException(typeof(NativeWindow).FullName, name);
                if (property.GetMethod is { } getter)
                    bindings.Add((getter, typeof(ManagedWindowBridge).GetMethod(nameof(GetProperty), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(property.PropertyType)));
                if (property.SetMethod is { } setter) Add(setter, nameof(SetProperty));
            }
            foreach (string name in new[] { "Focus", "Close", "MakeCurrent", "NewInputFrame", "ProcessWindowEvents", "CenterWindow", "Dispose" })
                foreach (MethodInfo method in typeof(NativeWindow).GetMethods(Fields | BindingFlags.DeclaredOnly).Where(method => method.Name == name))
                    Add(method, nameof(WindowOperation));
            Add(AccessTools.Method(typeof(NativeWindow), "ProcessEvents", [typeof(double)])!, nameof(ProcessEvents));
            Add(AccessTools.Method(typeof(GameWindow), "SwapBuffers")!, nameof(Swap));
            Add(AccessTools.Method(typeof(GameWindow), "Run")!, nameof(Run));
            foreach (string name in new[] { "TryGetCurrentMonitorScale", "TryGetCurrentMonitorDpi", "TryGetCurrentMonitorDpiRaw" })
                Add(AccessTools.Method(typeof(NativeWindow), name)!, nameof(MonitorScale));
            Add(typeof(GLFW).GetMethod(nameof(GLFW.GetCurrentContext), Type.EmptyTypes)!, nameof(GetCurrentContext));
            Add(typeof(GLFW).GetMethod(nameof(GLFW.MakeContextCurrent), [typeof(Window).MakePointerType()])!, nameof(MakeContextCurrent));
            Add(typeof(GLFW).GetMethod(nameof(GLFW.GetProcAddress), [typeof(string)])!, nameof(GetGlProcedure));
            Add(typeof(GLFW).GetMethod(nameof(GLFW.GetClipboardString), [typeof(Window).MakePointerType()])!, nameof(GetClipboard));
            Add(typeof(GLFW).GetMethod(nameof(GLFW.SetClipboardString), [typeof(Window).MakePointerType(), typeof(string)])!, nameof(SetClipboard));
            Add(typeof(GLFW).GetMethod(nameof(GLFW.GetWindowOpacity), [typeof(Window).MakePointerType()])!, nameof(GetOpacity));
            Add(typeof(GLFW).GetMethod(nameof(GLFW.SetWindowOpacity), [typeof(Window).MakePointerType(), typeof(float)])!, nameof(SetOpacity));
            Add(typeof(GLFWProvider).GetMethod(nameof(GLFWProvider.EnsureInitialized), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!, nameof(SkipGlfwInitialization));
            PrepareMonitorBindings(bindings);
            SupportedManagedEntries.Clear();
            foreach (var binding in bindings)
                if (binding.Method.Name != ".cctor" && binding.Method.Name != "Run" &&
                    binding.Method.Name != "EnsureInitialized") SupportedManagedEntries.Add(binding.Method);
            // An opaque token must never reach a GLFW native call through an
            // unported public method. Supported operations above take precedence;
            // other native window operations fail at the managed boundary.
            var routed = bindings.Select(value => value.Method).ToHashSet();
            foreach (MethodInfo method in typeof(NativeWindow).GetMethods(Fields | BindingFlags.DeclaredOnly)
                .Where(value => value.IsPublic && !routed.Contains(value)))
                if (PatchProcessor.GetOriginalInstructions(method).Any(instruction =>
                    instruction.operand is MethodInfo call && call.DeclaringType == typeof(GLFW)))
                    Add(method, nameof(UnsupportedWindowOperation));
            foreach (var binding in bindings)
                if (binding.Method == null) throw new MissingMethodException("Managed OpenTK window surface changed.");
        }, () =>
        {
            if (runtime != null) throw new InvalidOperationException("Managed window routing is already installed.");
            runtime = owner; attempted = true;
            installAfterWindowCommit = () =>
            {
                foreach (var binding in bindings)
                    harmony.Patch(binding.Method, prefix: new HarmonyMethod(binding.Prefix) { priority = Priority.First });
            };
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(PatchOwner);
            if (ReferenceEquals(runtime, owner)) { runtime = null; installAfterWindowCommit = null; }
            SupportedManagedEntries.Clear();
            attempted = false;
        });
        void Add(MethodBase method, string prefix) => bindings.Add((method,
            typeof(ManagedWindowBridge).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)!));
    }

    private static bool ConstructNative(NativeWindow __instance, NativeWindowSettings __0)
    {
        if (runtime?.IsActive != true) return true;
        CreateSecondary(__instance, __0); return false;
    }
    private static bool ConstructGame(GameWindow __instance, GameWindowSettings __0, NativeWindowSettings __1)
    {
        if (runtime?.IsActive != true) return true;
        CreateSecondary(__instance, __1); InitializeGameWindow(__instance, __0); return false;
    }
    private static void CreateSecondary(NativeWindow facade, NativeWindowSettings settings)
    {
        GC.SuppressFinalize(facade);
        ShareGroup group = Groups.GetValue(runtime!.Session, value => new(value));
        if (settings.SharedContext is { } shared && shared is not Context)
            throw new InvalidOperationException("An SDL/Vulkan managed window cannot share an external OpenGL context.");
        if (settings.SharedContext is { } context &&
            (!Tokens.TryGetValue(context.WindowPtr, out Entry? sharedEntry) || !ReferenceEquals(sharedEntry.Group, group)))
            throw new InvalidOperationException("The shared context belongs to a different session.");
        if (settings.API != ContextAPI.OpenGL)
            throw new NotSupportedException("Managed Vulkan windows provide desktop OpenGL command semantics only.");
        var host = SdlWindowHost.Create(settings.Title, settings.ClientSize.X, settings.ClientSize.Y,
            hidden: !settings.StartVisible, keepHidden: group.Session.Window.KeepsHidden);
        var entry = new Entry(group, facade, host, false);
        try
        {
            host.SetBordered(settings.WindowBorder != WindowBorder.Hidden);
            host.SetResizable(settings.WindowBorder == WindowBorder.Resizable);
            if (settings.Location is { } location) host.SetPosition(location.X, location.Y);
            if (settings.MinimumClientSize is { } minimum) host.SetMinimumSize(minimum.X, minimum.Y);
            SetWindowState(entry, settings.WindowState);
            entry.AuxiliaryTarget = group.Session.Device.CreateAuxiliaryTarget(host);
            Initialize(entry, settings);
            if (settings.StartFocused && settings.StartVisible) host.Focus();
            host.SetTextInputActive(true);
            entry.MakeCurrent();
        }
        catch (Exception failure)
        {
            try
            {
                if (Windows.TryGetValue(facade, out _)) DisposeEntry(entry);
                else
                {
                    if (entry.AuxiliaryTarget != 0) group.Session.Device.DeleteAuxiliaryTarget(entry.AuxiliaryTarget);
                    host.Dispose();
                }
            }
            catch (Exception cleanup) { throw new AggregateException("Managed SDL window initialization and release failed.", failure, cleanup); }
            throw;
        }
    }
    private static void Initialize(Entry entry, NativeWindowSettings settings)
    {
        NativeWindow facade = entry.Facade;
        entry.Title = settings.Title;
        entry.Keyboard = (KeyboardState)Activator.CreateInstance(typeof(KeyboardState), nonPublic: true)!;
        entry.Mouse = (MouseState)Activator.CreateInstance(typeof(MouseState), nonPublic: true)!;
        Put(facade, "<WindowPtr>k__BackingField", Pointer.Box((Window*)entry.Token, typeof(Window).MakePointerType()));
        Put(facade, "<Context>k__BackingField", new Context(entry));
        Put(facade, "<KeyboardState>k__BackingField", entry.Keyboard);
        Put(facade, "<MouseState>k__BackingField", entry.Mouse);
        Put(facade, "_joystickStates", new JoystickState[16]);
        Put(facade, "<API>k__BackingField", ContextAPI.OpenGL);
        Put(facade, "<Profile>k__BackingField", settings.Profile);
        Put(facade, "<Flags>k__BackingField", settings.Flags);
        Put(facade, "<APIVersion>k__BackingField", settings.APIVersion);
        Put(facade, "<Exists>k__BackingField", true);
        Put(facade, "<IsEventDriven>k__BackingField", settings.IsEventDriven);
        Put(facade, "_managedCursor", MouseCursor.Default);
        Put(facade, "_title", settings.Title);
        Put(facade, "_isFocused", entry.Host.IsFocused);
        Put(facade, "_isVisible", entry.Host.IsVisible);
        Put(facade, "_windowState", WindowStateOf(entry));
        Put(facade, "_windowBorder", BorderOf(entry));
        Put(facade, "_minimumClientSize", settings.MinimumClientSize);
        Put(facade, "_maximumClientize", settings.MaximumClientSize);
        Put(facade, "_aspectRatio", settings.AspectRatio);
        Put(facade, "_cachedWindowClientSize", Size(entry.Host.WindowSize));
        Put(facade, "_cachedWindowLocation", ClientLocation(entry));
        Windows.Add(facade, entry); Tokens.Add(entry.Token, entry); entry.Group.Entries.Add(entry);
    }
    private static void InitializeGameWindow(GameWindow facade, GameWindowSettings settings)
    {
        Put(facade, "_watchUpdate", Stopwatch.StartNew());
        Put(facade, "<ExpectedSchedulerPeriod>k__BackingField", 16);
        Put(facade, "_updateFrequency", settings.UpdateFrequency);
        Put(facade, "<IsMultiThreaded>k__BackingField", false);
    }

    internal static void BeginInputFrame(GameRenderSession session)
    {
        if (!Groups.TryGetValue(session, out ShareGroup? group)) return;
        foreach (Entry entry in group.Entries.ToArray())
        {
            if (entry.Disposed) continue;
            entry.RequireOwner();
            CopyBits(entry.Keyboard, "_keys", "_keysPrevious");
            CopyBits(entry.Mouse, "_buttons", "_buttonsPrevious");
            Put(entry.Mouse, "<PreviousPosition>k__BackingField", entry.Mouse.Position);
            Put(entry.Mouse, "<PreviousScroll>k__BackingField", entry.Mouse.Scroll);
            if (entry.Facade is GameWindow game)
            {
                Put(game, "<UpdateTime>k__BackingField", entry.UpdateClock.Elapsed.TotalSeconds);
                entry.UpdateClock.Restart();
            }
        }
    }

    internal static void MakePrimaryCurrent(GameRenderSession session)
    {
        if (Groups.TryGetValue(session, out ShareGroup? group) && group.Primary != null &&
            Windows.TryGetValue(group.Primary, out Entry? entry)) entry.MakeCurrent();
    }

    /// <summary>Mirrors one already-decoded SDL event without polling or removing native events.</summary>
    internal static bool DispatchSdlEvent(GameRenderSession session, SdlInputEvent e)
    {
        if (!Groups.TryGetValue(session, out ShareGroup? group)) return false;
        Entry? entry = group.Entries.FirstOrDefault(value => !value.Disposed && value.Host.WindowId == e.WindowId);
        if (entry == null) return false;
        entry.RequireOwner();
        switch (e.Type)
        {
            case SdlEventPump.KeyDown:
            case SdlEventPump.KeyUp:
                Keys key = ToKey(e.Scancode);
                entry.Modifiers = ToModifiers(e.Modifiers);
                if (key == Keys.Unknown) break;
                Bits(entry.Keyboard, "_keys")[(int)key] = e.Type == SdlEventPump.KeyDown;
                Raise(entry, e.Type == SdlEventPump.KeyDown ? "OnKeyDown" : "OnKeyUp", new KeyboardKeyEventArgs(key, e.Scancode, entry.Modifiers, e.Repeat));
                break;
            case SdlEventPump.TextInput:
                foreach (Rune rune in (e.Text ?? "").EnumerateRunes()) Raise(entry, "OnTextInput", new TextInputEventArgs(rune.Value));
                break;
            case SdlEventPump.MouseMotion:
                Put(entry.Mouse, "<Position>k__BackingField", new Vector2(e.X, e.Y));
                Raise(entry, "OnMouseMove", new MouseMoveEventArgs(e.X, e.Y, e.DeltaX, e.DeltaY));
                break;
            case SdlEventPump.MouseButtonDown:
            case SdlEventPump.MouseButtonUp:
                MouseButton button = e.Button switch { 1 => MouseButton.Left, 2 => MouseButton.Middle, 3 => MouseButton.Right, _ => (MouseButton)(e.Button - 1) };
                if ((int)button is < 0 or > 7) break;
                Bits(entry.Mouse, "_buttons")[(int)button] = e.Type == SdlEventPump.MouseButtonDown;
                Raise(entry, e.Type == SdlEventPump.MouseButtonDown ? "OnMouseDown" : "OnMouseUp",
                    new MouseButtonEventArgs(button, e.Type == SdlEventPump.MouseButtonDown ? InputAction.Press : InputAction.Release, entry.Modifiers));
                break;
            case SdlEventPump.MouseWheel:
                Vector2 wheel = new(e.DeltaX, e.DeltaY); if (e.Data1 == 1) wheel = -wheel;
                Put(entry.Mouse, "<Scroll>k__BackingField", entry.Mouse.Scroll + wheel);
                Raise(entry, "OnMouseWheel", new MouseWheelEventArgs(wheel)); break;
            case 0x205: // SDL_EVENT_WINDOW_MOVED
                Put(entry.Facade, "_cachedWindowLocation", ClientLocation(entry));
                Raise(entry, "OnMove", new WindowPositionEventArgs(ClientLocation(entry))); break;
            case SdlEventPump.WindowResized:
                Put(entry.Facade, "_cachedWindowClientSize", Size(entry.Host.WindowSize));
                Raise(entry, "OnResize", new ResizeEventArgs(Size(entry.Host.WindowSize))); break;
            case SdlEventPump.WindowPixelSizeChanged:
                Raise(entry, "OnFramebufferResize", new FramebufferResizeEventArgs(Size(entry.Host.PixelSize))); break;
            case 0x204: Raise(entry, "OnRefresh"); break; // SDL_EVENT_WINDOW_EXPOSED
            case 0x209: Raise(entry, "OnMinimized", new MinimizedEventArgs(true)); break;
            case SdlEventPump.WindowMaximized: Raise(entry, "OnMaximized", new MaximizedEventArgs(true)); break;
            case SdlEventPump.WindowRestored:
                Raise(entry, "OnMinimized", new MinimizedEventArgs(false));
                Raise(entry, "OnMaximized", new MaximizedEventArgs(false)); break;
            case SdlEventPump.WindowFocusGained:
            case SdlEventPump.WindowFocusLost:
                bool focused = e.Type == SdlEventPump.WindowFocusGained;
                Put(entry.Facade, "_isFocused", focused);
                if (!focused) { Bits(entry.Keyboard, "_keys").SetAll(false); Bits(entry.Mouse, "_buttons").SetAll(false); entry.Modifiers = 0; }
                Raise(entry, "OnFocusedChanged", new FocusedChangedEventArgs(focused)); break;
            case SdlEventPump.DropFile: Raise(entry, "OnFileDrop", new FileDropEventArgs([e.Text ?? ""])); break;
            case 0x20C: Raise(entry, "OnMouseEnter"); break;
            case 0x20D: Raise(entry, "OnMouseLeave"); break;
            case SdlEventPump.WindowCloseRequested:
                if (!entry.Primary) RequestClose(entry);
                else
                {
                    var cancel = new CancelEventArgs(); Raise(entry, "OnClosing", cancel);
                    if (cancel.Cancel) return true;
                }
                break;
        }
        return !entry.Primary;
    }

    internal static void DisposeForSession(GameRenderSession session)
    {
        if (!Groups.TryGetValue(session, out ShareGroup? group)) return;
        foreach (Entry entry in group.Entries.ToArray().Reverse()) DisposeEntry(entry);
        Groups.Remove(session); // The session owns and disposes the shared command/resource bridge.
    }
    private static void DisposeEntry(Entry entry)
    {
        if (entry.Disposed) return;
        entry.RequireOwner();
        if (!entry.Primary) entry.Group.Session.Device.DeleteAuxiliaryTarget(entry.AuxiliaryTarget);
        entry.Group.Gl.ReleaseContext(entry.Token);
        if (!entry.Primary) entry.Host.Dispose();
        entry.Disposed = true; entry.Exiting = true;
        Put(entry.Facade, "<WindowPtr>k__BackingField", Pointer.Box((Window*)0, typeof(Window).MakePointerType()));
        Put(entry.Facade, "<Exists>k__BackingField", false);
        Put(entry.Facade, "_disposedValue", true);
        if (entry.Facade is GameWindow game) Put(game, "_isDisposed", true);
        Tokens.Remove(entry.Token); entry.Group.Entries.Remove(entry);
        if (ReferenceEquals(current, entry)) current = null;
        GC.SuppressFinalize(entry.Facade);
    }

    private static bool GetProperty<T>(NativeWindow __instance, MethodBase __originalMethod, ref T __result)
    {
        if (!TryWindowEntry(__instance, out Entry? entry)) return true;
        string name = __originalMethod.Name[4..];
        if (name == "Exists") { __result = (T)(object)!entry.Disposed; return false; }
        if (name == "IsExiting") { __result = (T)(object)entry.Exiting; return false; }
        entry.RequireOwner();
        object value = name switch
        {
            "MousePosition" => entry.Mouse.Position, "VSync" => ((Context)__instance.Context).SwapInterval == 0 ? VSyncMode.Off : VSyncMode.On,
            "AutoIconify" => Field(__instance.GetType(), "_autoIconify").GetValue(__instance)!,
            "Icon" => Field(__instance.GetType(), "_icon").GetValue(__instance)!,
            "ClipboardString" => entry.Host.GetClipboardText(), "Title" => Marshal.PtrToStringUTF8(SDL_GetWindowTitle(entry.Host.NativeHandle)) ?? "",
            "CurrentMonitor" => MonitorFor(entry), "IsFocused" => entry.Host.IsFocused, "IsVisible" => entry.Host.IsVisible,
            "WindowState" => WindowStateOf(entry), "WindowBorder" => BorderOf(entry), "AlwaysOnTop" => entry.Host.AlwaysOnTop,
            "Bounds" => new Box2i(Location(entry), Location(entry) + OuterSize(entry)), "Location" => Location(entry),
            "ClientLocation" => ClientLocation(entry), "Size" => OuterSize(entry), "ClientSize" => Size(entry.Host.WindowSize),
            "FramebufferSize" => Size(entry.Host.PixelSize), "ClientRectangle" => new Box2i(ClientLocation(entry), ClientLocation(entry) + Size(entry.Host.WindowSize)),
            "IsFullscreen" => entry.Host.IsFullscreen,
            _ => throw new NotSupportedException("Managed window getter: " + name)
        };
        __result = (T)value; return false;
    }
    private static bool SetProperty(NativeWindow __instance, MethodBase __originalMethod, object[] __args)
    {
        if (!TryWindowEntry(__instance, out Entry? entry)) return true;
        entry.RequireOwner(); object value = __args[0];
        switch (__originalMethod.Name[4..])
        {
            case "MousePosition": Vector2 mouse = (Vector2)value; entry.Host.WarpMouse(mouse.X, mouse.Y); Put(entry.Mouse, "<Position>k__BackingField", mouse); break;
            case "VSync": ((Context)__instance.Context).SwapInterval = (VSyncMode)value == VSyncMode.Off ? 0 : 1; break;
            case "AutoIconify": entry.Host.SetMinimizeOnFocusLoss((bool)value); Put(__instance, "_autoIconify", value); break;
            case "Icon": ApplyIcon(entry, (WindowIcon)value); Put(__instance, "_icon", value); break;
            case "ClipboardString": entry.Host.SetClipboardText((string)value); break;
            case "Title": entry.Host.SetTitle((string)value); entry.Title = (string)value; Put(__instance, "_title", value); break;
            case "CurrentMonitor": MonitorInfo monitor = (MonitorInfo)value; entry.Host.SetPosition(monitor.ClientArea.Min.X, monitor.ClientArea.Min.Y); break;
            case "IsVisible": if ((bool)value) entry.Host.Show(); else entry.Host.Hide(); break;
            case "Exists": throw new InvalidOperationException("SDL owns managed window existence.");
            case "WindowState": SetWindowState(entry, (WindowState)value); break;
            case "WindowBorder": WindowBorder border = (WindowBorder)value; entry.Host.SetBordered(border != WindowBorder.Hidden); entry.Host.SetResizable(border == WindowBorder.Resizable); break;
            case "AlwaysOnTop": entry.Host.SetAlwaysOnTop((bool)value); break;
            case "Location": Vector2i position = (Vector2i)value; var edges = Borders(entry); entry.Host.SetPosition(position.X + edges.Left, position.Y + edges.Top); break;
            case "ClientLocation": Vector2i clientPosition = (Vector2i)value; entry.Host.SetPosition(clientPosition.X, clientPosition.Y); break;
            case "Size": Vector2i outer = (Vector2i)value; var margins = Borders(entry); entry.Host.SetSize(Math.Max(1, outer.X - margins.Left - margins.Right), Math.Max(1, outer.Y - margins.Top - margins.Bottom)); break;
            case "ClientSize": Vector2i size = (Vector2i)value; entry.Host.SetSize(size.X, size.Y); break;
            case "Bounds": Box2i bounds = (Box2i)value; __instance.Location = bounds.Min; __instance.Size = bounds.Size; break;
            case "ClientRectangle": Box2i rectangle = (Box2i)value; __instance.ClientLocation = rectangle.Min; __instance.ClientSize = rectangle.Size; break;
            default: throw new NotSupportedException("Managed window setter: " + __originalMethod.Name);
        }
        return false;
    }
    private static bool WindowOperation(NativeWindow __instance, MethodBase __originalMethod, object[] __args)
    {
        if (!TryWindowEntry(__instance, out Entry? entry)) return true;
        if (__originalMethod.Name == "Dispose") { DisposeEntry(entry); return false; }
        entry.RequireOwner();
        switch (__originalMethod.Name)
        {
            case "Focus": entry.Host.Focus(); break;
            case "Close": RequestClose(entry); break;
            case "MakeCurrent": entry.MakeCurrent(); break;
            case "CenterWindow": if (__args.Length > 0) { Vector2i size = (Vector2i)__args[0]; entry.Host.SetSize(size.X, size.Y); } entry.Host.Center(); break;
            case "NewInputFrame": case "ProcessWindowEvents": break; // Already performed once by the session's SDL pump.
            default: throw new NotSupportedException("Managed window operation: " + __originalMethod.Name);
        }
        return false;
    }
    private static bool ProcessEvents(NativeWindow __instance, ref bool __result)
    { if (!TryWindowEntry(__instance, out Entry? entry)) return true; entry.RequireOwner(); __result = !entry.Exiting; return false; }
    private static bool Swap(GameWindow __instance)
    { if (!TryWindowEntry(__instance, out Entry? entry)) return true; entry.SwapBuffers(); return false; }
    private static bool Run(GameWindow __instance)
    { if (!TryWindowEntry(__instance, out _)) return true; throw new InvalidOperationException("The session owns SDL polling and frame submission; use managed window frame callbacks from that loop."); }
    private static void RequestClose(Entry entry)
    {
        if (entry.Exiting) return;
        var cancel = new CancelEventArgs(); Raise(entry, "OnClosing", cancel);
        if (cancel.Cancel) return;
        if (entry.Primary) entry.Group.Session.Input.RequestWindowExit();
        else { entry.Exiting = true; entry.Host.Hide(); }
    }
    private static bool UnsupportedWindowOperation(NativeWindow __instance, MethodBase __originalMethod)
    {
        if (!TryWindowEntry(__instance, out _)) return true;
        throw new NotSupportedException("The SDL/Vulkan managed window does not provide " + __originalMethod.Name + ".");
    }
    private static bool SkipGlfwInitialization() => !ManagedWindowRouting;
    private static bool GetCurrentContext(ref Window* __result)
    {
        if (!ManagedWindowRouting) return true;
        __result = current is { Disposed: false } selected && Tokens.ContainsKey(selected.Token) &&
            ReferenceEquals(ManagedGlBridge.Current, selected.Group.Gl) ? (Window*)selected.Token : null;
        return false;
    }
    private static bool MakeContextCurrent(Window* __0)
    {
        if (!ManagedWindowRouting) return true;
        nint token = (nint)__0;
        if (token == 0) { current?.Group.Gl.SelectContext(0, 0, 0, 0); current = null; }
        else RequireTokenEntry(token).MakeCurrent();
        return false;
    }
    private static bool GetGlProcedure(string __0, ref nint __result)
    {
        if (!ManagedWindowRouting) return true;
        __result = ManagedGlBridge.Current is null ? 0 : ManagedGlCallPatches.ProcedureAddress(__0);
        return false;
    }
    private static bool GetClipboard(Window* __0, ref string __result)
    { if (!ManagedWindowRouting) return true; Entry entry = RequireTokenEntry((nint)__0); entry.RequireOwner(); __result = entry.Host.GetClipboardText(); return false; }
    private static bool SetClipboard(Window* __0, string __1)
    { if (!ManagedWindowRouting) return true; Entry entry = RequireTokenEntry((nint)__0); entry.RequireOwner(); entry.Host.SetClipboardText(__1); return false; }
    private static bool GetOpacity(Window* __0, ref float __result)
    { if (!ManagedWindowRouting) return true; Entry entry = RequireTokenEntry((nint)__0); entry.RequireOwner(); __result = entry.Host.Opacity; return false; }
    private static bool SetOpacity(Window* __0, float __1)
    { if (!ManagedWindowRouting) return true; Entry entry = RequireTokenEntry((nint)__0); entry.RequireOwner(); entry.Host.SetOpacity(__1); return false; }

    private static Entry RequireTokenEntry(nint token)
    {
        if (Tokens.TryGetValue(token, out Entry? entry)) return entry;
        if (token == 0 || (long)token > 0x60000000 && (long)token <= nextToken)
            throw new ObjectDisposedException(nameof(NativeWindow), "The managed window token is null or retired.");
        throw new ArgumentException("The window token is not owned by the active SDL session.", nameof(token));
    }
    private static bool TryWindowEntry(NativeWindow facade, [NotNullWhen(true)] out Entry? entry)
    {
        if (Windows.TryGetValue(facade, out entry)) return true;
        if (ManagedWindowRouting) throw new ArgumentException("The native window is not owned by the active SDL session.", nameof(facade));
        return false;
    }

    private static FieldInfo Field(Type type, string name)
    {
        for (Type? currentType = type; currentType != null; currentType = currentType.BaseType)
            if (currentType.GetField(name, Fields | BindingFlags.DeclaredOnly) is { } field) return field;
        throw new MissingFieldException(type.FullName, name);
    }
    private static void Put(object target, string name, object? value) => Field(target.GetType(), name).SetValue(target, value);
    private static BitArray Bits(object target, string name) => (BitArray)Field(target.GetType(), name).GetValue(target)!;
    private static void CopyBits(object target, string from, string to)
    { BitArray source = Bits(target, from), previous = Bits(target, to); previous.SetAll(false); previous.Or(source); }
    private static void Raise(Entry entry, string method, params object[] arguments)
    {
        // Invoke the original virtual event boundary, so derived windows retain their
        // own resize/focus behavior and the subscribed event delegates run normally.
        MethodInfo callback = AccessTools.Method(typeof(NativeWindow), method) ?? throw new MissingMethodException(method);
        try { callback.Invoke(entry.Facade, arguments); }
        catch (TargetInvocationException failure) when (failure.InnerException != null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure.InnerException).Throw(); }
    }
    private static Vector2i Size((int Width, int Height) size) => new(size.Width, size.Height);
    private static Vector2i ClientLocation(Entry entry) { var location = entry.Host.Position; return new(location.X, location.Y); }
    private static Vector2i Location(Entry entry) { var bounds = Borders(entry); return ClientLocation(entry) - new Vector2i(bounds.Left, bounds.Top); }
    private static Vector2i OuterSize(Entry entry) { var bounds = Borders(entry); return Size(entry.Host.WindowSize) + new Vector2i(bounds.Left + bounds.Right, bounds.Top + bounds.Bottom); }
    private static (int Top, int Left, int Bottom, int Right) Borders(Entry entry)
    { CheckSdl(SDL_GetWindowBordersSize(entry.Host.NativeHandle, out int top, out int left, out int bottom, out int right), "SDL_GetWindowBordersSize"); return (top, left, bottom, right); }
    private static WindowState WindowStateOf(Entry entry) => entry.Host.IsFullscreen ? WindowState.Fullscreen : entry.Host.IsMinimized ? WindowState.Minimized : entry.Host.IsMaximized ? WindowState.Maximized : WindowState.Normal;
    private static WindowBorder BorderOf(Entry entry) => entry.Host.IsBorderless ? WindowBorder.Hidden : entry.Host.IsResizable ? WindowBorder.Resizable : WindowBorder.Fixed;
    private static void SetWindowState(Entry entry, WindowState state)
    {
        entry.Host.SetFullscreen(state == WindowState.Fullscreen);
        switch (state) { case WindowState.Normal: entry.Host.Restore(); break; case WindowState.Minimized: entry.Host.Minimize(); break; case WindowState.Maximized: entry.Host.Maximize(); break; }
    }
    private static void ApplyIcon(Entry entry, WindowIcon icon)
    {
        if (icon.Images.Length == 0) throw new ArgumentException("A window icon needs an image.", nameof(icon));
        WindowImage image = icon.Images.OrderByDescending(value => value.Width * value.Height).First();
        entry.Host.SetIcon(image.Width, image.Height, image.Data);
    }
    private static KeyModifiers ToModifiers(ushort value) =>
        ((value & 0x0003) != 0 ? KeyModifiers.Shift : 0) | ((value & 0x00C0) != 0 ? KeyModifiers.Control : 0) |
        ((value & 0x0300) != 0 ? KeyModifiers.Alt : 0) | ((value & 0x0C00) != 0 ? KeyModifiers.Super : 0) |
        ((value & 0x2000) != 0 ? KeyModifiers.CapsLock : 0) | ((value & 0x1000) != 0 ? KeyModifiers.NumLock : 0);
    private static Keys ToKey(int scan) => scan switch
    {
        >= 4 and <= 29 => Keys.A + (scan - 4), >= 30 and <= 38 => Keys.D1 + (scan - 30), 39 => Keys.D0,
        40 => Keys.Enter, 41 => Keys.Escape, 42 => Keys.Backspace, 43 => Keys.Tab, 44 => Keys.Space,
        45 => Keys.Minus, 46 => Keys.Equal, 47 => Keys.LeftBracket, 48 => Keys.RightBracket, 49 or 50 or 100 => Keys.Backslash,
        51 => Keys.Semicolon, 52 => Keys.Apostrophe, 53 => Keys.GraveAccent, 54 => Keys.Comma, 55 => Keys.Period, 56 => Keys.Slash,
        57 => Keys.CapsLock, >= 58 and <= 69 => Keys.F1 + (scan - 58), 70 => Keys.PrintScreen, 71 => Keys.ScrollLock, 72 => Keys.Pause,
        73 => Keys.Insert, 74 => Keys.Home, 75 => Keys.PageUp, 76 => Keys.Delete, 77 => Keys.End, 78 => Keys.PageDown,
        79 => Keys.Right, 80 => Keys.Left, 81 => Keys.Down, 82 => Keys.Up, 83 => Keys.NumLock,
        84 => Keys.KeyPadDivide, 85 => Keys.KeyPadMultiply, 86 => Keys.KeyPadSubtract, 87 => Keys.KeyPadAdd, 88 => Keys.KeyPadEnter,
        >= 89 and <= 97 => Keys.KeyPad1 + (scan - 89), 98 => Keys.KeyPad0, 99 => Keys.KeyPadDecimal,
        101 or 118 => Keys.Menu, >= 104 and <= 115 => Keys.F13 + (scan - 104),
        224 => Keys.LeftControl, 225 => Keys.LeftShift, 226 => Keys.LeftAlt, 227 => Keys.LeftSuper,
        228 => Keys.RightControl, 229 => Keys.RightShift, 230 => Keys.RightAlt, 231 => Keys.RightSuper, _ => Keys.Unknown
    };
    private static void CheckSdl(bool result, string name)
    { if (!result) throw new InvalidOperationException(name + ": " + (Marshal.PtrToStringUTF8(SDL_GetError()) ?? "SDL error")); }
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_GetWindowBordersSize(nint window, out int top, out int left, out int bottom, out int right);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)] private static extern nint SDL_GetError();
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)] private static extern nint SDL_GetWindowTitle(nint window);
}
