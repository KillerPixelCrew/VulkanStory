using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VulkanStory.Render.Vulkan.Shaders;

/// <summary>GL user clipping at the last vertex-processing stage, before depth conversion.</summary>
internal static class GlClipShader
{
    internal const string Planes = "vulkanstoryClipPlanes";
    internal const string Mask = "vulkanstoryClipMask";
    internal const string Owner = "vulkanstory_clip";

    private static string Code(string source) => Regex.Replace(source, @"/\*[\s\S]*?\*/|//[^\r\n]*", "");
    private static string OutputCode(string source) => Regex.Replace(
        Regex.Replace(Code(source), @"\bin\s+gl_PerVertex\s*\{[^}]*\}[^;]*;", ""),
        @"\.\s*gl_ClipDistance\b", "");
    internal static bool HasDistances(string source) => Regex.IsMatch(OutputCode(source), @"\bgl_ClipDistance\b");
    private static bool HasVertex(string source) => Regex.IsMatch(Code(source), @"\bgl_ClipVertex\b");

    internal static string Fields()
    {
        var text = new StringBuilder();
        foreach (string name in new[] { Planes, Mask })
        {
            if (!FrameGlobals.TryGetMember(name, out UniformMember member)) throw new InvalidOperationException(name);
            text.Append(CultureInfo.InvariantCulture, $"    layout(offset = {member.Offset}) {member.Type.Name} {name}");
            if (member.ArrayLength > 0) text.Append('[').Append(member.ArrayLength).Append(']');
            text.Append(";\n");
        }
        return text.ToString();
    }

    internal static string Declarations(string source)
    {
        // Dynamic indexing of an implicitly-sized built-in is illegal GLSL.
        // Preserve an explicit source size; otherwise provide the supported size
        // before either original writes or our enable-mask loop can index it.
        bool explicitlySized = Regex.IsMatch(OutputCode(source), @"\bfloat\s+gl_ClipDistance\s*\[\s*[^\]\s][^\]]*\]");
        string result = explicitlySized ? "" : "out float gl_ClipDistance[8];\n";
        if (HasVertex(source)) result += "vec4 _optimum_clip_vertex;\n#define gl_ClipVertex _optimum_clip_vertex\n";
        return result;
    }

    internal static string Apply(string source, string frame = "", bool nativeDepth = false)
    {
        string mask = frame + Mask;
        // A shader that provides distances owns their equations. Only the GL enable
        // bits are emulated; inactive distances must never reach Vulkan clipping.
        if (HasDistances(source))
            return $"for (int _optimum_clip_i = 0; _optimum_clip_i < gl_ClipDistance.length(); ++_optimum_clip_i) " +
                $"if (({mask} & (1 << _optimum_clip_i)) == 0) gl_ClipDistance[_optimum_clip_i] = 1.0;";

        // Legacy gl_ClipVertex is eye-space. Without it the compatibility default
        // is the GL clip position; never compare an eye-space plane to Vulkan Z.
        string position = HasVertex(source) ? "_optimum_clip_vertex" : nativeDepth
            ? "vec4(gl_Position.xy, 2.0 * gl_Position.z - gl_Position.w, gl_Position.w)" : "gl_Position";
        return "for (int _optimum_clip_i = 0; _optimum_clip_i < 8; ++_optimum_clip_i) " +
            $"gl_ClipDistance[_optimum_clip_i] = ({mask} & (1 << _optimum_clip_i)) != 0 " +
            $"? dot({position}, {frame}{Planes}[_optimum_clip_i]) : 1.0;";
    }
}
