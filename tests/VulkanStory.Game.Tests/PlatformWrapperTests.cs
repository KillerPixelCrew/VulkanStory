using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Xunit;

namespace VulkanStory.Game.Tests;

public sealed class PlatformWrapperTests
{
    public class OsServices : DispatchProxy
    {
        internal string Clipboard = "original";
        internal int FocusCalls;
        internal readonly Size2i Screen = new(640, 480);
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "SetClipboardText" => SetClipboard((string)args![0]!),
            "GetClipboardText" => Clipboard,
            "FocusWindow" => Focus(),
            "GetScreenSize" => Screen,
            "GetCpuInfo" => "cpu",
            _ => throw new InvalidOperationException("Unexpected OS service: " + method.Name),
        };
        private object? SetClipboard(string text) { Clipboard = text; return null; }
        private object? Focus() { FocusCalls++; return null; }
    }

    [Fact]
    public void DormantWrapperForwardsWithoutTouchingAnSdlWindow()
    {
        var inner = DispatchProxy.Create<IXPlatformInterface, OsServices>();
        var probe = (OsServices)inner;
        var wrapper = new SdlXPlatformInterface(inner, null!, () => false,
            () => throw new InvalidOperationException("SDL must stay dormant"));
        Assert.Same(probe.Screen, wrapper.GetScreenSize());
        Assert.Equal("original", wrapper.GetClipboardText());
        wrapper.SetClipboardText("updated");
        wrapper.FocusWindow();
        Assert.Equal("updated", probe.Clipboard);
        Assert.Equal(1, probe.FocusCalls);
        Assert.Equal("cpu", wrapper.GetCpuInfo());
    }

    [Fact]
    public void ActiveWindowServicesCheckOwnerBeforeNativeAccess()
    {
        var inner = DispatchProxy.Create<IXPlatformInterface, OsServices>();
        var wrapper = new SdlXPlatformInterface(inner, null!, () => true,
            () => throw new InvalidOperationException("owner-check"));
        Assert.Equal("owner-check", Assert.Throws<InvalidOperationException>(() => wrapper.GetClipboardText()).Message);
        Assert.Equal("owner-check", Assert.Throws<InvalidOperationException>(() => wrapper.GetScreenSize()).Message);
        Assert.Equal("owner-check", Assert.Throws<InvalidOperationException>(() => wrapper.FocusWindow()).Message);
    }

    [Fact]
    public void ProtectedOriginalSettersUseClosedDelegates()
    {
        var platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
        var bindings = new GamePlatformBindings(platform);
        var services = DispatchProxy.Create<IXPlatformInterface, OsServices>();
        bindings.SetXPlatform(services);
        bindings.SetCurrentCursor("test-cursor");
        Assert.Same(services, platform.XPlatInterface);
        Assert.Equal("test-cursor", platform.CurrentMouseCursor);
        bindings.SetCurrentCursor(null);
        Assert.Null(platform.CurrentMouseCursor);
    }
}
