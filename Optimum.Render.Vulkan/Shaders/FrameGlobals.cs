using System;
using System.Collections.Generic;

namespace Optimum.Render.Vulkan.Shaders;

/// <summary>
/// The per-frame values every program reads, as one block shared by all of them:
/// descriptor set 0, binding 0.
///
/// <c>ShaderProgramBase.Use()</c> writes the same values from
/// <c>DefaultShaderUniforms</c> into every program that includes the file that
/// declares them - fog and light, the shadow cascades, the vertex warp, the sky
/// colour, the colour map and the underwater effect - up to 56 writes per program
/// switch. Stored per program, each of those programs carries its own copy and a
/// draw copies all of it into the frame's uniform ring. Stored here, the values
/// live once: a write that changes nothing is a comparison, a write that changes
/// something bumps one version, and every draw in the frame binds the same
/// snapshot until the next change.
///
/// A name joins the shared block for a program only when that program's
/// <c>Use()</c> really writes it, which is when the program includes the file
/// listed as the name's owner. The GUI program sets <c>lightPosition</c> itself
/// without including fog and light; a program that includes only the fragment
/// half of fog and light declares <c>flatFogDensity</c> but never has it written.
/// Both keep a copy of their own, exactly as on OpenGL, where every program has
/// its own uniform storage.
///
/// Offsets are fixed for the whole process, independent of which programs exist:
/// arrays are sized for the largest declaration the game can produce (100 dynamic
/// lights is the settings slider's maximum), and a shader that declares a shorter
/// array reads a prefix of the same member. Scalar block layout, as in the
/// per-program block, so the game's packed float arrays land as a memcpy.
/// </summary>
internal static class FrameGlobals
{
    public const int Set = 0;
    public const int Binding = 0;
    public const string BlockTypeName = "OptimumFrameGlobals";

    /// <summary>The dynamic lights slider's maximum; DYNLIGHTS never exceeds it.</summary>
    public const int MaxDynamicLights = 100;

    private readonly record struct Entry(string Name, string TypeName, int Capacity, string Owner, string? Initializer = null);

    // Owners and initialisers mirror ShaderProgramBase.Use() and the vanilla
    // include declarations; FrameGlobalsTests pins both against the assets.
    private static readonly Entry[] Entries =
    {
        // fogandlight.fsh
        new("zNear", "float", 0, "fogandlight.fsh", "0.3"),
        new("zFar", "float", 0, "fogandlight.fsh", "1500.0"),
        new("lightPosition", "vec3", 0, "fogandlight.fsh"),
        new("shadowIntensity", "float", 0, "fogandlight.fsh", "1"),
        new("glitchStrength", "float", 0, "fogandlight.fsh", "0"),
        new("psychedelicStrength", "float", 0, "fogandlight.fsh", "0"),
        new("shadowMapWidthInv", "float", 0, "fogandlight.fsh"),
        new("shadowMapHeightInv", "float", 0, "fogandlight.fsh"),

        // fogandlight.vsh (also the unconditional writer of the view distances)
        new("viewDistance", "float", 0, "fogandlight.vsh"),
        new("viewDistanceLod0", "float", 0, "fogandlight.vsh"),
        new("fogSphereQuantity", "int", 0, "fogandlight.vsh"),
        new("pointLightQuantity", "int", 0, "fogandlight.vsh"),
        new("flatFogDensity", "float", 0, "fogandlight.vsh"),
        new("flatFogStart", "float", 0, "fogandlight.vsh"),
        new("glitchStrengthFL", "float", 0, "fogandlight.vsh"),
        new("nightVisionStrength", "float", 0, "fogandlight.vsh"),

        // shadowcoords.vsh
        new("shadowRangeNear", "float", 0, "shadowcoords.vsh"),
        new("shadowRangeFar", "float", 0, "shadowcoords.vsh"),

        // vertexwarp.vsh
        new("timeCounter", "float", 0, "vertexwarp.vsh"),
        new("windWaveCounter", "float", 0, "vertexwarp.vsh"),
        new("windWaveCounterHighFreq", "float", 0, "vertexwarp.vsh"),
        new("windSpeed", "float", 0, "vertexwarp.vsh"),
        new("waterWaveCounter", "float", 0, "vertexwarp.vsh"),
        new("playerpos", "vec3", 0, "vertexwarp.vsh"),
        new("globalWarpIntensity", "float", 0, "vertexwarp.vsh"),
        new("glitchWaviness", "float", 0, "vertexwarp.vsh", "0"),
        new("windWaveIntensity", "float", 0, "vertexwarp.vsh", "1"),
        new("waterWaveIntensity", "float", 0, "vertexwarp.vsh", "1"),
        new("perceptionEffectId", "int", 0, "vertexwarp.vsh", "1"),
        new("perceptionEffectIntensity", "float", 0, "vertexwarp.vsh", "1"),

        // skycolor.fsh (sky and glow are samplers and stay per program)
        new("fogWaveCounter", "float", 0, "skycolor.fsh"),
        new("sunsetMod", "float", 0, "skycolor.fsh"),
        new("ditherSeed", "int", 0, "skycolor.fsh"),
        new("horizontalResolution", "int", 0, "skycolor.fsh"),
        new("playerToSealevelOffset", "float", 0, "skycolor.fsh"),

        // colormap.vsh
        new("seasonRel", "float", 0, "colormap.vsh"),
        new("seaLevel", "float", 0, "colormap.vsh"),
        new("atlasHeight", "float", 0, "colormap.vsh"),
        new("seasonTemperature", "float", 0, "colormap.vsh"),

        // underwatereffects.fsh. frameSize is not here: bilateralblur and the blur
        // passes declare a frameSize of their own and write it outside Use().
        new("cameraUnderwater", "float", 0, "underwatereffects.fsh"),
        new("waterMurkColor", "vec4", 0, "underwatereffects.fsh"),

        // The large members last, so the scalars above share a few cache lines.
        new("toShadowMapSpaceMatrixNear", "mat4", 0, "shadowcoords.vsh"),
        new("toShadowMapSpaceMatrixFar", "mat4", 0, "shadowcoords.vsh"),
        new("fogSpheres", "float", 3 * 8, "fogandlight.vsh"),
        new("colorMapRects", "vec4", 40, "colormap.vsh"),
        new("pointLights", "vec3", MaxDynamicLights, "fogandlight.vsh"),
        new("pointLightColors", "vec3", MaxDynamicLights, "fogandlight.vsh"),
    };

    private static readonly Dictionary<string, (UniformMember Member, string Owner)> ByName = new(StringComparer.Ordinal);
    private static readonly List<UniformMember> MemberList = new();

    /// <summary>Size of the shared block in bytes.</summary>
    public static int BlockSize { get; }

    /// <summary>Every member, in layout order.</summary>
    public static IReadOnlyList<UniformMember> Members => MemberList;

    static FrameGlobals()
    {
        int offset = 0;
        foreach (Entry entry in Entries)
        {
            if (!GlslType.TryParse(entry.TypeName, out GlslType type))
            {
                throw new InvalidOperationException("frame global '" + entry.Name + "' has unknown type " + entry.TypeName);
            }

            offset = Align(offset, type.Alignment);
            var member = new UniformMember
            {
                Name = entry.Name,
                Type = type,
                ArrayLength = entry.Capacity,
                Offset = offset,
                Initializer = entry.Initializer,
            };
            member.Size = type.Size * member.ElementCount;
            offset += member.Size;

            MemberList.Add(member);
            ByName.Add(entry.Name, (member, entry.Owner));
        }
        BlockSize = offset;
    }

    /// <summary>
    /// Whether a program's declaration of <paramref name="name" /> reads the shared
    /// block: the program includes the name's owner, and the declaration agrees
    /// with the shared member - the same type, and an array no longer than the
    /// member (or not an array where the member is not one).
    /// </summary>
    public static bool TryPlace(
        string name, GlslType type, int arrayLength, IReadOnlySet<string>? includes, out UniformMember member)
    {
        member = null!;
        if (includes == null || !ByName.TryGetValue(name, out (UniformMember Member, string Owner) found)) return false;
        if (!includes.Contains(found.Owner)) return false;
        if (!found.Member.Type.Equals(type)) return false;

        bool fits = found.Member.ArrayLength == 0
            ? arrayLength == 0
            : arrayLength > 0 && arrayLength <= found.Member.ArrayLength;
        if (!fits) return false;

        member = found.Member;
        return true;
    }

    /// <summary>The member called <paramref name="name" />, whatever its owner.</summary>
    public static bool TryGetMember(string name, out UniformMember member)
    {
        bool known = ByName.TryGetValue(name, out (UniformMember Member, string Owner) found);
        member = known ? found.Member : null!;
        return known;
    }

    /// <summary>The include whose block in <c>Use()</c> writes <paramref name="name" />.</summary>
    public static string? OwnerOf(string name) => ByName.TryGetValue(name, out (UniformMember Member, string Owner) found) ? found.Owner : null;

    /// <summary>A shadow of the shared block, pre-filled with the declared defaults.</summary>
    public static byte[] CreateShadow()
    {
        var buffer = new byte[BlockSize];
        foreach (UniformMember member in MemberList)
        {
            if (member.Initializer != null) ProgramInterfaceLayout.WriteInitializer(buffer, member);
        }
        return buffer;
    }

    private static int Align(int value, int alignment) =>
        alignment <= 1 ? value : (value + alignment - 1) / alignment * alignment;
}
