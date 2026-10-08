using System.ComponentModel;
using System.Reflection;
using HarmonyLib;
using OpenTK.Windowing.Common;
using Vintagestory.Client.NoObf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VulkanStory.Game;

/// <summary>Cached access to existing official platform state. Adds no game members.</summary>
internal sealed class GamePlatformBindings
{
    private readonly ClientPlatformWindows platform;
    private readonly AccessTools.FieldRef<ClientPlatformWindows, float> mouseX;
    private readonly AccessTools.FieldRef<ClientPlatformWindows, float> mouseY;
    private readonly AccessTools.FieldRef<ClientPlatformWindows, float> wheel;
    private readonly AccessTools.FieldRef<ClientPlatformWindows, long> keyUpMs;
    private readonly AccessTools.FieldRef<ClientPlatformWindows, int> keyUpKey;
    private readonly AccessTools.FieldRef<ClientPlatformWindows, List<ClientPlatformAbstract.OnFocusChanged>> focusHandlers;
    internal Action<float, float> SetMousePosition { get; }
    private readonly Action<CancelEventArgs> closing;
    private readonly Action<FrameEventArgs> frame;
    internal Action<IXPlatformInterface> SetXPlatform { get; }
    internal Action<string?> SetCurrentCursor { get; }

    /// <summary>Validates and caches original platform fields, setters and callback delegates.</summary>
    /// <param name="platform">Borrowed original platform instance.</param>
    internal GamePlatformBindings(ClientPlatformWindows platform)
    {
        Validate();
        this.platform = platform;
        mouseX = AccessTools.FieldRefAccess<ClientPlatformWindows, float>("mouseX");
        mouseY = AccessTools.FieldRefAccess<ClientPlatformWindows, float>("mouseY");
        wheel = AccessTools.FieldRefAccess<ClientPlatformWindows, float>("prevWheelValue");
        keyUpMs = AccessTools.FieldRefAccess<ClientPlatformWindows, long>("lastKeyUpMs");
        keyUpKey = AccessTools.FieldRefAccess<ClientPlatformWindows, int>("lastKeyUpKey");
        focusHandlers = AccessTools.FieldRefAccess<ClientPlatformWindows,
            List<ClientPlatformAbstract.OnFocusChanged>>("focusChangedDelegates");
        SetMousePosition = Method("SetMousePosition", typeof(float), typeof(float))
            .CreateDelegate<Action<float, float>>(platform);
        closing = Method("window_Closing", typeof(CancelEventArgs))
            .CreateDelegate<Action<CancelEventArgs>>(platform);
        frame = Method("window_RenderFrame", typeof(FrameEventArgs))
            .CreateDelegate<Action<FrameEventArgs>>(platform);
        SetXPlatform = Setter(typeof(ClientPlatformAbstract), "XPlatInterface", typeof(IXPlatformInterface))
            .CreateDelegate<Action<IXPlatformInterface>>(platform);
        SetCurrentCursor = Setter(typeof(ClientPlatformWindows), "CurrentMouseCursor", typeof(string))
            .CreateDelegate<Action<string?>>(platform);
    }

    internal float MouseX => mouseX(platform);
    internal float MouseY => mouseY(platform);
    internal float Wheel { get => wheel(platform); set => wheel(platform) = value; }
    internal long LastKeyUpMs { get => keyUpMs(platform); set => keyUpMs(platform) = value; }
    internal int LastKeyUpKey { get => keyUpKey(platform); set => keyUpKey(platform) = value; }

    /// <summary>Invokes the original close callback with a cancellable request.</summary>
    /// <returns>True when the original game permits closing; false when its handler cancels.</returns>
    internal bool RequestClose()
    {
        var request = new CancelEventArgs();
        closing(request);
        return !request.Cancel;
    }

    // The sidecar must gate this call on complete graphics/window routing.
    /// <summary>Invokes the original platform frame callback through the cached delegate.</summary>
    /// <remarks>The adapter must gate this call on complete active startup routing.</remarks>
    internal void RenderFrame() => frame(default);

    /// <summary>Invokes the original platform focus-change subscribers.</summary>
    /// <param name="focused">Current SDL focus state.</param>
    internal void NotifyFocus(bool focused)
    {
        foreach (ClientPlatformAbstract.OnFocusChanged handler in focusHandlers(platform))
            handler(focused);
    }

    internal static void Validate()
    {
        Field("mouseX", typeof(float));
        Field("mouseY", typeof(float));
        Field("prevWheelValue", typeof(float));
        Field("lastKeyUpMs", typeof(long));
        Field("lastKeyUpKey", typeof(int));
        Field("focusChangedDelegates", typeof(List<ClientPlatformAbstract.OnFocusChanged>));
        Method("SetMousePosition", typeof(float), typeof(float));
        Method("window_Closing", typeof(CancelEventArgs));
        Method("window_RenderFrame", typeof(FrameEventArgs));
        Setter(typeof(ClientPlatformAbstract), "XPlatInterface", typeof(IXPlatformInterface));
        Setter(typeof(ClientPlatformWindows), "CurrentMouseCursor", typeof(string));
    }

    private static MethodInfo Setter(Type owner, string name, Type value)
    {
        MethodInfo? method = owner.GetProperty(name, BindingFlags.Public | BindingFlags.Instance |
            BindingFlags.DeclaredOnly)?.GetSetMethod(nonPublic: true);
        if (method is null || method.IsStatic || method.ReturnType != typeof(void) ||
            !method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { value }))
            throw new MissingMethodException(owner.FullName, "set_" + name);
        return method;
    }

    private const BindingFlags PrivateInstance = BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.DeclaredOnly;

    private static void Field(string name, Type expected)
    {
        FieldInfo? field = typeof(ClientPlatformWindows).GetField(name, PrivateInstance);
        if (field is null || field.FieldType != expected || field.IsInitOnly)
            throw new MissingFieldException(typeof(ClientPlatformWindows).FullName, name);
    }

    private static MethodInfo Method(string name, params Type[] parameters)
    {
        MethodInfo? method = typeof(ClientPlatformWindows).GetMethod(name, PrivateInstance,
            binder: null, types: parameters, modifiers: null);
        if (method is null || method.ReturnType != typeof(void) ||
            method.ContainsGenericParameters || method.GetMethodBody() is null)
            throw new MissingMethodException(typeof(ClientPlatformWindows).FullName, name);
        return method;
    }
}

/// <summary>Metadata-only check; does not construct a platform or invoke game callbacks.</summary>
public static class GamePlatformBindingProfile
{
    /// <summary>Checks the supported original platform binding profile before routing installation.</summary>
    public static void Validate1227()
    {
        GamePlatformBindings.Validate();
        Input.GameGuiBindings.Validate();
        WindowConsumerPatches.Validate1227();
    }
}
