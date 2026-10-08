using System.Reflection;

namespace VulkanStory.Game;

/// <summary>Exact original member signature and diagnostic event used to bind a supported startup boundary.</summary>
internal sealed record StartupTarget(string TypeName, string Member, bool IsStatic, string[] Parameters, string Event);

/// <summary>Pinned startup targets for official 1.22.7; resolution requires one concrete managed body with the exact signature.</summary>
internal static class StartupTargets
{
    internal static readonly StartupTarget[] Profile1227 =
    [
        new("Vintagestory.Client.ClientProgram", "Main", true, ["System.String[]"], "game.client.main"),
        new("Vintagestory.Client.ClientProgram", "Start", false,
            ["Vintagestory.Client.ClientProgramArgs", "System.String[]"], "game.client.start"),
        new("Vintagestory.Client.NoObf.ClientPlatformWindows", ".ctor", false, ["Vintagestory.Logger"], "game.platform.construct"),
        new("Vintagestory.Client.ClientProgram", "AttemptToOpenWindow", false,
            ["OpenTK.Windowing.Desktop.GameWindowSettings", "OpenTK.Windowing.Desktop.NativeWindowSettings", "System.Int32", "System.Int32", "System.Int32"],
            "game.window.request"),
        new("Vintagestory.Client.NoObf.GameWindowNative", ".ctor", false,
            ["OpenTK.Windowing.Desktop.GameWindowSettings", "OpenTK.Windowing.Desktop.NativeWindowSettings"], "game.window.construct"),
        new("Vintagestory.Client.ScreenManager", "Start", false,
            ["Vintagestory.Client.ClientProgramArgs", "System.String[]"], "game.screens.start"),
        new("Vintagestory.Client.NoObf.ClientPlatformWindows", "Start", false, [], "game.platform.start")
    ];

    /// <summary>Resolves one concrete original method or constructor using its complete pinned signature.</summary>
    /// <param name="type">Declared game type to inspect.</param>
    /// <param name="target">Static/instance shape and exact parameter type names.</param>
    /// <returns>The sole matching managed body.</returns>
    /// <remarks>Missing, ambiguous, generic or bodyless targets throw before patch mutation.</remarks>
    internal static MethodBase Resolve(Type type, StartupTarget target)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                   BindingFlags.Static | BindingFlags.DeclaredOnly;
        IEnumerable<MethodBase> candidates = target.Member == ".ctor" ? type.GetConstructors(flags) : type.GetMethods(flags);
        MethodBase[] matches = candidates.Where(method => method.Name == target.Member &&
            method.IsStatic == target.IsStatic && !method.ContainsGenericParameters &&
            method.GetParameters().Select(parameter => parameter.ParameterType.FullName).SequenceEqual(target.Parameters)).ToArray();
        if (matches.Length != 1 || matches[0].GetMethodBody() is null)
            throw new MissingMethodException($"Expected one managed target: {type.FullName}.{target.Member}({string.Join(", ", target.Parameters)}); found {matches.Length}.");
        return matches[0];
    }
}
