using System.Reflection;
using HarmonyLib;
using OpenTK.Mathematics;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

/// <summary>Observes original shader uniform writes to retain per-draw model and deformation state for motion producers.</summary>
/// <remarks>Patch discovery and installation belong to startup. Callbacks use the committed routing predicate; game object identity remains in the integration assembly.</remarks>
internal static class MotionUniformConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-motion-uniforms";
    private static ProcessRuntime? runtime;
    private static readonly (string Name, Type[] Parameters, string Prefix)[] Routes =
    [
        ("Uniform", [typeof(string), typeof(float)], nameof(Warp)),
        ("UniformMatrix", [typeof(string), typeof(float[])], nameof(Model)),
        ("UniformMatrix", [typeof(string), typeof(Matrix4).MakeByRefType()], nameof(ModelRef)),
    ];
    private static MethodInfo Target(string name, Type[] parameters) => typeof(ShaderProgramBase).GetMethod(name,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, parameters, null) ??
        throw new MissingMethodException(typeof(ShaderProgramBase).FullName, name);
    /// <summary>Creates the dormant patch group for this consumer path; validation and installation remain separate transaction steps.</summary>
    /// <param name="owner">Process runtime that owns this group and its session.</param>
    /// <returns>Validation, installation and removal callbacks for the startup transaction.</returns>
    /// <remarks>Binding or IL-anchor mismatches reject the group. Creating the group does not enable graphics routing.</remarks>
    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner)
    {
        var harmony = new Harmony(Owner); bool attempted = false;
        return new StartupPatchGroup("graphics-motion-uniforms", () =>
        {
            foreach (var route in Routes)
            {
                var method = Target(route.Name, route.Parameters);
                if (method.ReturnType != typeof(void) || method.GetMethodBody() == null) throw new MissingMethodException("Original motion uniform target changed.");
            }
        }, () =>
        {
            if (runtime != null) throw new InvalidOperationException("Motion uniform routing already has an owner.");
            runtime = owner; attempted = true;
            foreach (var route in Routes) harmony.Patch(Target(route.Name, route.Parameters),
                prefix: new HarmonyMethod(typeof(MotionUniformConsumerPatches), route.Prefix));
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner); if (ReferenceEquals(runtime, owner)) runtime = null;
            attempted = false;
        });
    }
    private static EntityMotionHistory? History()
    {
        if (runtime?.Routing.RoutingEnabled != true) return null;
        EntityMotionHistory history = runtime.Session.Temporal.EntityMotion;
        return history.Enabled ? history : null;
    }
    private static void Warp(string __0, float __1) => History()?.NoteWarpUniform(__0, __1);
    private static void Model(string __0, float[] __1)
    { if (__0 == "modelMatrix") History()?.NoteModelMatrix(__1); }
    private static void ModelRef(string __0, ref Matrix4 __1)
    {
        if (__0 != "modelMatrix" || History() is not { } history) return;
        history.NoteModelMatrix(stackalloc float[] { __1.M11, __1.M12, __1.M13, __1.M14, __1.M21, __1.M22, __1.M23, __1.M24,
            __1.M31, __1.M32, __1.M33, __1.M34, __1.M41, __1.M42, __1.M43, __1.M44 });
    }
}
