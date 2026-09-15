using System;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The shared frame block on a real device: programs assembled from the same
/// include read one copy of a frame value, a program that does not include it keeps
/// its own, and a program's own uniforms stay its own.
/// </summary>
public class FrameGlobalsDeviceTests(ITestOutputHelper output)
{
    private const int Size = 4;

    private const string FullscreenVertex = """
        #version 330 core
        out vec2 texCoord;
        void main()
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0.0, 1.0);
            texCoord = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);
        }
        """;

    private const string ZNearFragment = """
        #version 330 core
        uniform float zNear;
        uniform float tint;
        in vec2 texCoord;
        out vec4 outColor;
        void main() { outColor = vec4(zNear, tint, 0.0, 1.0); }
        """;

    [SkippableFact]
    public void ProgramsThatIncludeTheOwnerShareOneCopyAndOthersKeepTheirOwn()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            int first = Link(seam, "shared-a", includeFog: true);
            int second = Link(seam, "shared-b", includeFog: true);
            int isolated = Link(seam, "isolated", includeFog: false);

            int firstZNear = seam.GetUniformLocation(first, "zNear");
            int secondZNear = seam.GetUniformLocation(second, "zNear");
            int isolatedZNear = seam.GetUniformLocation(isolated, "zNear");
            Assert.True(ShaderProgramResources.IsFrameLocation(firstZNear));
            Assert.Equal(firstZNear, secondZNear);
            Assert.False(ShaderProgramResources.IsFrameLocation(isolatedZNear));
            Assert.True(isolatedZNear >= 0);
            Assert.False(ShaderProgramResources.IsFrameLocation(seam.GetUniformLocation(first, "tint")));

            // Written once, through the first program.
            seam.UseProgram(first);
            seam.SetUniform(first, firstZNear, 0.25f);
            Assert.True(FrameGlobals.TryGetMember("zNear", out UniformMember member));
            Assert.Equal(0.25f, BitConverter.ToSingle(seam.FrameGlobalsForTests, member.Offset));

            seam.SetUniform(second, seam.GetUniformLocation(second, "tint"), 0.75f);
            seam.SetUniform(isolated, seam.GetUniformLocation(isolated, "tint"), 0.75f);

            int secondTarget = Target(seam, out int secondColour);
            int isolatedTarget = Target(seam, out int isolatedColour);
            seam.SetViewport(0, 0, Size, Size);
            seam.SetCullFace(false);
            seam.SetDepthTest(false);
            seam.SetBlend(false, EnumBlendMode.Standard);

            Draw(seam, second, secondTarget);
            Draw(seam, isolated, isolatedTarget);

            seam.BeginFrame();
            byte[] shared = seam.ReadBackLevel0ForTests(secondColour);
            byte[] own = seam.ReadBackLevel0ForTests(isolatedColour);
            seam.Present();

            output.WriteLine($"second program: R={shared[0]} G={shared[1]}; isolated program: R={own[0]} G={own[1]}");
            // The second program never wrote zNear, yet reads the first's value.
            Assert.InRange(shared[0], 63, 65);
            Assert.InRange(shared[1], 190, 192);
            // The isolated program reads its own zNear, never written: zero.
            Assert.Equal(0, own[0]);
            Assert.InRange(own[1], 190, 192);

            GpuTest.AssertClean(seam);
        }
    }

    private static int Link(VulkanDevice seam, string name, bool includeFog)
    {
        var vertex = new Shader(EnumShaderType.VertexShader, FullscreenVertex, name + ".vsh");
        var fragment = new Shader(EnumShaderType.FragmentShader, ZNearFragment, name + ".fsh");
        Assert.True(seam.CompileShader(vertex));
        Assert.True(seam.CompileShader(fragment));

        var program = new ShaderProgram { PassName = name, VertexShader = vertex, FragmentShader = fragment };
        if (includeFog) program.includes.Add("fogandlight.fsh");

        int id = seam.LinkProgram(program);
        Assert.True(id > 0, seam.GetError() ?? "link failed");
        return id;
    }

    private static int Target(VulkanDevice seam, out int colour)
    {
        int target = seam.CreateFramebuffer(Size, Size);
        colour = seam.CreateTexture2DRaw(Size, Size, 0x8058, IntPtr.Zero, 4);
        seam.AttachTexture(target, EnumFramebufferAttachment.ColorAttachment0, colour, 0);
        return target;
    }

    private static void Draw(VulkanDevice seam, int program, int target)
    {
        seam.BeginFrame();
        seam.BindFramebuffer(target);
        seam.SetDrawBuffers(target, 1);
        seam.ClearColor(0, 0, 0, 0, 1);
        seam.UseProgram(program);
        seam.DrawFullscreenTriangle();
        seam.Present();
    }
}
