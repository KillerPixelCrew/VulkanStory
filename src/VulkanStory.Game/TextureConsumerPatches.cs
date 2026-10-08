using System.Reflection;
using Cairo;
using HarmonyLib;
using Vintagestory.Client;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

/// <summary>Texture subset for eventual composition into the complete graphics-api group.</summary>
/// <remarks>Patch discovery and installation belong to startup. Callbacks use the committed routing predicate; game object identity remains in the integration assembly.</remarks>
internal static class TextureConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-textures";
    private static System.Func<bool>? routingEnabled;
    private sealed record Binding(string Target, Type Result, Type[] Parameters, string Prefix);
    private static readonly Binding[] Profile1227 =
    [
        new("GenTexture", typeof(void), [typeof(RawTexture)], nameof(Generate)),
        new("BuildMipMaps", typeof(void), [typeof(int)], nameof(Mipmaps)),
        new("GLDeleteTexture", typeof(void), [typeof(int)], nameof(Delete)),
        new("LoadTexture", typeof(int), [typeof(IBitmap), typeof(bool), typeof(int), typeof(bool)], nameof(Load)),
        new("LoadIntoTexture", typeof(void), [typeof(IBitmap), typeof(int), typeof(int), typeof(int), typeof(bool)], nameof(Upload)),
        new("LoadOrUpdateTextureFromBgra_DeferMipMap", typeof(void),
            [typeof(int[]), typeof(bool), typeof(int), typeof(LoadedTexture).MakeByRefType()], nameof(BgraDeferred)),
        new("LoadOrUpdateTextureFromBgra", typeof(void),
            [typeof(int[]), typeof(bool), typeof(int), typeof(LoadedTexture).MakeByRefType()], nameof(Bgra)),
        new("LoadOrUpdateTextureFromRgba", typeof(void),
            [typeof(int[]), typeof(bool), typeof(int), typeof(LoadedTexture).MakeByRefType()], nameof(Rgba)),
        new("LoadCairoTexture", typeof(int), [typeof(ImageSurface), typeof(bool)], nameof(CairoLoad)),
        new("LoadOrUpdateCairoTexture", typeof(void),
            [typeof(ImageSurface), typeof(bool), typeof(LoadedTexture).MakeByRefType()], nameof(CairoUpdate)),
        new("Load3DTextureCube", typeof(int), [typeof(BitmapRef[])], nameof(Cube)),
    ];

    private static readonly Type[] SvgParameters =
        [typeof(IAsset), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int?)];

    internal static void ValidateBindings()
    {
        foreach (var binding in Profile1227) Resolve(binding);
        ResolveSvgLoad();
    }

    // This subset is not a complete mandatory graphics-api group and cannot
    // satisfy StartupRoutingTransaction.RequiredGroups by itself.
    /// <summary>Creates the dormant patch group for this consumer path; validation and installation remain separate transaction steps.</summary>
    /// <param name="enabled">Predicate read by routed callbacks after the complete startup transaction commits.</param>
    /// <returns>Validation, installation and removal callbacks for the startup transaction.</returns>
    /// <remarks>Binding or IL-anchor mismatches reject the group. Creating the group does not enable graphics routing.</remarks>
    internal static StartupPatchGroup CreateSubset(System.Func<bool> enabled)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        var harmony = new Harmony(Owner);
        (MethodInfo Original, MethodInfo Prefix)[]? methods = null;
        bool attempted = false;
        return new StartupPatchGroup("graphics-textures", () =>
        {
            methods = Profile1227.Select(binding => (Resolve(binding),
                typeof(TextureConsumerPatches).GetMethod(binding.Prefix,
                    BindingFlags.NonPublic | BindingFlags.Static)!))
                .Append((ResolveSvgLoad(), typeof(TextureConsumerPatches).GetMethod(nameof(SvgLoad),
                    BindingFlags.NonPublic | BindingFlags.Static)!)).ToArray();
        }, () =>
        {
            if (methods is null) throw new InvalidOperationException("Texture targets were not validated.");
            if (routingEnabled != null) throw new InvalidOperationException("Texture routing already has an owner.");
            routingEnabled = enabled;
            attempted = true;
            foreach (var method in methods)
                harmony.Patch(method.Original, prefix: new HarmonyMethod(method.Prefix) { priority = Priority.First });
        }, () =>
        {
            if (!attempted) return;
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

    private static MethodInfo ResolveSvgLoad()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        MethodInfo? load = typeof(SvgLoader).GetMethod(nameof(SvgLoader.LoadSvg), flags, null, SvgParameters, null);
        MethodInfo? rasterize = typeof(SvgLoader).GetMethod(nameof(SvgLoader.rasterizeSvg), flags, null, SvgParameters, null);
        FieldInfo? api = typeof(SvgLoader).GetField("capi", BindingFlags.NonPublic | BindingFlags.Instance);
        if (load?.ReturnType != typeof(LoadedTexture) || load.GetMethodBody() is null ||
            rasterize?.ReturnType != typeof(byte[]) || rasterize.GetMethodBody() is null ||
            api?.FieldType != typeof(ICoreClientAPI))
            throw new MissingMethodException(typeof(SvgLoader).FullName, "LoadSvg/rasterizeSvg/capi (1.22.7)");
        return load;
    }

    private static unsafe bool SvgLoad(SvgLoader __instance, ICoreClientAPI ___capi,
        IAsset __0, int __1, int __2, int __3, int __4, int? __5, ref LoadedTexture __result)
    {
        if (routingEnabled?.Invoke() != true) return true;
        if (ScreenManager.Platform is not ClientPlatformWindows platform || !TryAdapter(platform, out var adapter))
            throw new InvalidOperationException("Active SVG routing has no renderer platform.");
        // Keep the game's rasterizer and byte ordering exactly as its GL upload did.
        // Texture allocation dimensions differ from LoadedTexture's logical dimensions.
        int texture;
        fixed (byte* pixels = __instance.rasterizeSvg(__0, __1, __2, __3, __4, __5))
            texture = adapter.LoadTextureFromRgbaPointer(__1, __2, (IntPtr)pixels);
        __result = new LoadedTexture(___capi, texture, __3, __4);
        return false;
    }

    private static bool TryAdapter(ClientPlatformWindows platform, out GameGraphicsAdapter adapter)
    {
        adapter = null!;
        if (routingEnabled?.Invoke() != true) return false;
        if (!GameGraphicsAdapter.TryGet(platform, out var found) || found is null)
            throw new InvalidOperationException("Active graphics routing has no renderer adapter for this platform.");
        adapter = found;
        return true;
    }

    private static bool Generate(ClientPlatformWindows __instance, RawTexture __0)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.GenTexture(__0);
        return false;
    }
    private static bool Mipmaps(ClientPlatformWindows __instance, int __0)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.BuildMipMaps(__0);
        return false;
    }
    private static bool Delete(ClientPlatformWindows __instance, int __0)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.DeleteTexture(__0);
        return false;
    }
    private static bool Load(ClientPlatformWindows __instance, IBitmap __0, bool __1, int __2,
        bool __3, ref int __result)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        __result = adapter.LoadTexture(__0, __1, __2, __3);
        return false;
    }
    private static bool Upload(ClientPlatformWindows __instance, IBitmap __0, int __1,
        int __2, int __3, bool __4)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.LoadIntoTexture(__0, __1, __2, __3, __4);
        return false;
    }
    private static bool BgraDeferred(ClientPlatformWindows __instance, int[] __0, bool __1,
        int __2, ref LoadedTexture __3)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.LoadOrUpdateTextureFromBgra_DeferMipMap(__0, __1, __2, ref __3);
        return false;
    }
    private static bool Bgra(ClientPlatformWindows __instance, int[] __0, bool __1,
        int __2, ref LoadedTexture __3)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.LoadOrUpdateTextureFromBgra(__0, __1, __2, ref __3);
        return false;
    }
    private static bool Rgba(ClientPlatformWindows __instance, int[] __0, bool __1,
        int __2, ref LoadedTexture __3)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.LoadOrUpdateTextureFromRgba(__0, __1, __2, ref __3);
        return false;
    }
    private static bool CairoLoad(ClientPlatformWindows __instance, ImageSurface __0,
        bool __1, ref int __result)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        __result = adapter.LoadCairoTexture(__0, __1);
        return false;
    }
    private static bool CairoUpdate(ClientPlatformWindows __instance, ImageSurface __0,
        bool __1, ref LoadedTexture __2)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.LoadOrUpdateCairoTexture(__0, __1, ref __2);
        return false;
    }
    private static bool Cube(ClientPlatformWindows __instance, BitmapRef[] __0, ref int __result)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        __result = adapter.Load3DTextureCube(__0);
        return false;
    }
}
