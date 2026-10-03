using System;
using System.Globalization;
using System.IO;

namespace VulkanStory.Render.Vulkan.Core;

// Included with the shader program types when their game-facing adapter is compiled.
internal static partial class RenderTrace
{
    /// <summary>
    /// Dumps one named uniform out of a program's shadow buffer, which is what
    /// separates "the CPU wrote nonsense" from "the CPU was right and the GPU
    /// read it from the wrong place".
    /// </summary>
    public static void Uniforms(Shaders.ProgramInterfaceLayout layout, byte[] shadow, string name)
    {
        if (Path == null) return;
        if (!layout.MembersByName.TryGetValue(name, out Shaders.UniformMember? member)) return;

        int floats = Math.Min(member.Size / sizeof(float), 16);
        var text = new System.Text.StringBuilder();
        text.Append("  ").Append(name).Append(" @").Append(member.Offset).Append(" =");
        for (int i = 0; i < floats; i++)
        {
            text.Append(' ').Append(
                BitConverter.ToSingle(shadow, member.Offset + i * sizeof(float))
                    .ToString("0.###", CultureInfo.InvariantCulture));
        }
        Write(text.ToString());
    }

    public static void UniformInt(Shaders.ProgramInterfaceLayout layout, byte[] shadow, string name)
    {
        if (Path == null) return;
        if (!layout.MembersByName.TryGetValue(name, out Shaders.UniformMember? member)) return;

        Write("  " + name + " @" + member.Offset + " = " + BitConverter.ToInt32(shadow, member.Offset));
    }

    /// <summary>
    /// Writes each stage's rewritten GLSL beside the trace file, so the source
    /// the driver actually compiled can be read rather than reconstructed.
    /// </summary>
    public static void DumpProgramSources(string passName, Shaders.TranslatedProgram translated)
    {
        if (Path == null) return;

        string directory = System.IO.Path.GetDirectoryName(Path) ?? ".";
        // The hardcoded minimal-GUI program has no pass name at all.
        string safeName = string.IsNullOrWhiteSpace(passName)
            ? "unnamed"
            : string.Join("_", passName.Split(System.IO.Path.GetInvalidFileNameChars()));

        foreach (var stage in translated.RewrittenSource)
        {
            string file = System.IO.Path.Combine(directory, "shader-" + safeName + "-" + stage.Key + ".glsl");
            try
            {
                File.WriteAllText(file, stage.Value);
            }
            catch (IOException)
            {
                // Losing a debug dump must not disturb the run.
            }
        }
    }
}
