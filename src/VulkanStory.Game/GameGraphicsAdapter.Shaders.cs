using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Vintagestory.Client.NoObf;
using OpenTK.Mathematics;

namespace VulkanStory.Game;

/// <summary>Borrowed game-side policy and logging callbacks for shader linking; no game singleton crosses into the renderer.</summary>
internal sealed record GameShaderCallbacks(Func<bool> HandheldShadowTier,
    Action<string, string?> LinkError, Action<string> ProgramLoaded);

internal sealed partial class GameGraphicsAdapter
{
    private readonly GameShaderDefinitions shaderDefinitions = new();
    private readonly ConditionalWeakTable<Shader, object> injectedHandheldShadowStages = new();
    internal int StatedProgram { get; private set; }

    /// <summary>Applies the owned handheld-shadow define and submits the original stage source to backend compilation.</summary>
    /// <param name="shader">Original stage object.</param>
    /// <returns>Whether stage acceptance succeeded.</returns>
    internal bool CompileShader(Shader shader)
    {
        var renderer = RequireDevice();
        if (shaderCallbacks.HandheldShadowTier())
        {
            if (!Regex.IsMatch(shader.PrefixCode ?? "", @"^[ \t]*#define[ \t]+HANDHELDSHADOWS\b", RegexOptions.Multiline))
            {
                shader.PrefixCode = (shader.PrefixCode ?? "") + "\n#define HANDHELDSHADOWS 1\n";
                injectedHandheldShadowStages.GetValue(shader, _ => new object());
            }
        }
        else if (injectedHandheldShadowStages.Remove(shader))
        {
            // Remove only a define this adapter inserted; retain custom shader prefixes.
            shader.PrefixCode = Regex.Replace(shader.PrefixCode ?? "",
                @"^[ \t]*#define[ \t]+HANDHELDSHADOWS[ \t]+1[ \t]*(?:\r?\n|$)", "", RegexOptions.Multiline);
        }
        return renderer.CompileShader(shaderDefinitions.Stage(shader));
    }

    /// <summary>Links original shader sources through the retained backend and publishes the backend program identifier on success.</summary>
    /// <param name="program">Original program receiving backend ID and link status.</param>
    /// <returns>True after successful backend linking; false after a reported link refusal.</returns>
    internal bool CreateShaderProgram(ShaderProgram program)
    {
        var renderer = RequireDevice();
        int id = renderer.LinkProgram(shaderDefinitions.Program(program));
        if (id == 0)
        {
            shaderCallbacks.LinkError(program.PassName, renderer.GetError());
            return false;
        }
        program.ProgramId = id;
        SceneShaderLinked(program);
        shaderCallbacks.ProgramLoaded(program.PassName);
        return true;
    }

    internal int GetUniformLocation(ShaderProgram program, string name) =>
        RequireDevice().GetUniformLocation(program.ProgramId, name);

    internal void Uniform(int program, int location, float value) => RequireDevice().SetUniform(program, location, value);
    internal void Uniform(int program, int location, int value) => RequireDevice().SetUniform(program, location, value);
    internal void Uniform(int program, int location, float x, float y) => RequireDevice().SetUniform(program, location, x, y);
    internal void Uniform(int program, int location, float x, float y, float z) => RequireDevice().SetUniform(program, location, x, y, z);
    internal void Uniform(int program, int location, float x, float y, float z, float w) => RequireDevice().SetUniform(program, location, x, y, z, w);
    internal void Uniform(int program, int location, int x, int y, int z) => RequireDevice().SetUniform(program, location, x, y, z);

    internal void UniformArray(int program, int location, int count, float[] values, int components)
    {
        var renderer = RequireDevice();
        switch (components)
        {
            case 1: renderer.SetUniformArray1(program, location, count, values); break;
            case 2: renderer.SetUniformArray2(program, location, count, values); break;
            case 3: renderer.SetUniformArray3(program, location, count, values); break;
            case 4: renderer.SetUniformArray4(program, location, count, values); break;
            default: throw new ArgumentOutOfRangeException(nameof(components));
        }
    }
    internal void UniformMatrices(int program, int location, int count, float[] values) =>
        RequireDevice().SetUniformMatrices(program, location, count, values);
    internal void UniformMatrices4x3(int program, int location, int count, float[] values) =>
        RequireDevice().SetUniformMatrices4x3(program, location, count, values);
    internal void UniformMatrix(int program, int location, ref Matrix4 matrix)
    {
        var renderer = RequireDevice();
        // Retained row-field flattening from the original renderer platform adapter.
        float[] values = [matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44];
        renderer.SetUniformMatrix(program, location, values);
    }

    internal void UseShaderProgram(int id)
    {
        RequireDevice();
        StatedProgram = id;
    }
    internal void DetachShader(int program, int shader) { RequireDevice(); }
    internal void DeleteShader(int shader) { RequireDevice(); } // SPIR-V stage modules belong to the linked program.
    internal void DeleteSampler(int sampler) => RequireDevice().DeleteSampler(sampler);
    /// <summary>Deletes the backend program and releases its previous-animation buffer association.</summary>
    /// <param name="program">Original published backend program identifier.</param>
    internal void DeleteProgram(int program)
    {
        ReleasePreviousAnimation(program);
        RequireDevice().DeleteProgram(program);
        worldSamplerNames.Remove(program);
        foreach (var key in programTextures.Keys.Where(key => key.Program == program).ToArray())
            programTextures.Remove(key);
    }
}
