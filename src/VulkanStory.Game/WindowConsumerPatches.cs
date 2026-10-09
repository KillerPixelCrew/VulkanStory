using System.Reflection;
using HarmonyLib;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

/// <summary>Core platform window consumers. Further consumers are still required before activation.</summary>
/// <remarks>Patch discovery and installation belong to startup. Callbacks use the committed routing predicate; game object identity remains in the integration assembly.</remarks>
internal static class WindowConsumerPatches
{
    private const string Owner = "vulkanstory.routing.window-consumers";
    private static System.Func<bool>? routingEnabled;
    private sealed record Binding(string Target, Type Result, Type[] Parameters, string Prefix);
    private static readonly Binding[] Profile1227 =
    [
        new("get_IsFocused", typeof(bool), [], nameof(Focused)),
        new("get_ScreenSize", typeof(Size2i), [], nameof(ScreenSize)),
        new("get_MouseGrabbed", typeof(bool), [], nameof(MouseGrabbed)),
        new("set_MouseGrabbed", typeof(void), [typeof(bool)], nameof(SetMouseGrabbed)),
        new("get_WindowBorder", typeof(EnumWindowBorder), [], nameof(Border)),
        new("set_WindowBorder", typeof(void), [typeof(EnumWindowBorder)], nameof(SetBorder)),
        new("SetTitle", typeof(void), [typeof(string)], nameof(Title)),
        new("SetWindowSize", typeof(void), [typeof(int), typeof(int)], nameof(Size)),
        new("WindowFocus", typeof(void), [], nameof(Focus)),
        new("GetWindowState", typeof(WindowState), [], nameof(State)),
        new("SetWindowState", typeof(void), [typeof(WindowState)], nameof(SetState)),
        new("SetWindowAttribute", typeof(void), [typeof(WindowAttribute), typeof(bool)], nameof(Attribute)),
        new("SetDirectMouseMode", typeof(void), [typeof(bool)], nameof(DirectMouse)),
        new("LoadMouseCursor", typeof(bool), [typeof(string), typeof(int), typeof(int), typeof(BitmapRef)], nameof(LoadCursor)),
        new("UseMouseCursor", typeof(void), [typeof(string), typeof(bool)], nameof(UseCursor)),
        new("RestoreWindowCursor", typeof(void), [], nameof(RestoreCursor)),
    ];

    internal static void Validate1227()
    {
        foreach (Binding binding in Profile1227) Resolve(binding);
    }

    /// <summary>Creates the dormant patch group for this consumer path; validation and installation remain separate transaction steps.</summary>
    /// <param name="enabled">Predicate read by routed callbacks after the complete startup transaction commits.</param>
    /// <returns>Validation, installation and removal callbacks for the startup transaction.</returns>
    /// <remarks>Binding or IL-anchor mismatches reject the group. Creating the group does not enable graphics routing.</remarks>
    internal static StartupPatchGroup CreateCoreGroup(System.Func<bool> enabled)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        var harmony = new Harmony(Owner);
        (MethodInfo Original, MethodInfo Prefix)[]? methods = null;
        bool attempted = false;
        return new StartupPatchGroup("window-consumers", () =>
        {
            methods = Profile1227.Select(binding => (Resolve(binding), Prefix(binding.Prefix))).ToArray();
        }, () =>
        {
            if (methods is null) throw new InvalidOperationException("Window targets were not validated.");
            if (routingEnabled != null) throw new InvalidOperationException("Window routing already has an owner.");
            routingEnabled = enabled;
            attempted = true;
            foreach (var method in methods)
                harmony.Patch(method.Original, prefix: new HarmonyMethod(method.Prefix) { priority = Priority.First });
        }, () =>
        {
            if (!attempted) return;
            // The transaction disables routing before rollback/removal.
            harmony.UnpatchAll(Owner);
            if (ReferenceEquals(routingEnabled, enabled)) routingEnabled = null;
            attempted = false;
        });
    }

    private static MethodInfo Resolve(Binding binding)
    {
        MethodInfo? method = typeof(ClientPlatformWindows).GetMethod(binding.Target,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            binder: null, types: binding.Parameters, modifiers: null);
        if (method is null || method.ReturnType != binding.Result ||
            method.ContainsGenericParameters || method.GetMethodBody() is null)
            throw new MissingMethodException(typeof(ClientPlatformWindows).FullName, binding.Target);
        return method;
    }

    private static MethodInfo Prefix(string name) => typeof(WindowConsumerPatches).GetMethod(name,
        BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingMethodException(name);

    private static bool TryAdapter(ClientPlatformWindows platform, out GamePlatformAdapter adapter)
    {
        adapter = null!;
        if (routingEnabled?.Invoke() != true) return false;
        if (!GamePlatformAdapter.TryGet(platform, out var found) || found is null)
            throw new InvalidOperationException("Active window routing has no SDL adapter for this platform.");
        found.RequireWindowRouting();
        adapter = found;
        return true;
    }

    private static bool Focused(ClientPlatformWindows __instance, ref bool __result)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        __result = adapter.Window.IsFocused;
        return false;
    }

    private static bool ScreenSize(ClientPlatformWindows __instance, ref Size2i __result)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        // Pixels, like the original's monitor size (e.g. the >3000 GUI-scale range check).
        var size = adapter.Window.DisplaySize;
        __result = new Size2i(size.Width, size.Height);
        return false;
    }

    private static bool MouseGrabbed(ClientPlatformWindows __instance, ref bool __result)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        __result = adapter.Window.RelativeMouseMode;
        return false;
    }

    private static bool SetMouseGrabbed(ClientPlatformWindows __instance, bool __0)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.SetMouseGrabbed(__0);
        return false;
    }

    private static bool Border(ClientPlatformWindows __instance, ref EnumWindowBorder __result)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        __result = adapter.WindowBorder;
        return false;
    }

    private static bool SetBorder(ClientPlatformWindows __instance, EnumWindowBorder __0)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.WindowBorder = __0;
        return false;
    }

    private static bool Title(ClientPlatformWindows __instance, string __0)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.Window.SetTitle(__0);
        return false;
    }

    private static bool Size(ClientPlatformWindows __instance, int __0, int __1)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.SetWindowSize(__0, __1);
        return false;
    }

    private static bool Focus(ClientPlatformWindows __instance)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.Window.Focus();
        return false;
    }

    private static bool State(ClientPlatformWindows __instance, ref WindowState __result)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        var window = adapter.Window;
        __result = (WindowState)(window.IsFullscreen ? 3 : window.IsMinimized ? 1 : window.IsMaximized ? 2 : 0);
        return false;
    }

    private static bool SetState(ClientPlatformWindows __instance, WindowState __0)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.SetWindowState(__0);
        return false;
    }

    private static bool Attribute(ClientPlatformWindows __instance, WindowAttribute __0, bool __1)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        if ((int)__0 == 131078)
        {
            if (!adapter.Window.SetMinimizeOnFocusLoss(__1))
                __instance.Logger.Warning("SDL could not update fullscreen minimize-on-focus-loss behavior");
        }
        else __instance.Logger.Warning("No SDL equivalent for window attribute {0}", __0);
        return false;
    }

    private static bool DirectMouse(ClientPlatformWindows __instance, bool __0)
    {
        // SDL owns relative/raw input configuration; never call GLFW on its route.
        return !TryAdapter(__instance, out _);
    }

    private static bool LoadCursor(ClientPlatformWindows __instance, string __0, int __1, int __2,
        BitmapRef __3, ref bool __result)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        __result = adapter.Cursor.Load(__0, __1, __2, __3);
        return false;
    }

    private static bool UseCursor(ClientPlatformWindows __instance, string? __0, bool __1)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.Cursor.Use(__0, __1);
        return false;
    }

    private static bool RestoreCursor(ClientPlatformWindows __instance)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.Cursor.Restore();
        return false;
    }
}
