// Source: Optimum.Tests/platform-client-program-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// Vulkan-native plan, Phase 1A step 1: ClientProgram.Start constructs the platform the
/// backend hands it (VulkanClientPlatform : ClientPlatformWindows), initializes graphics
/// through an injected virtual once the window is open, and on failure swaps in a plain
/// ClientPlatformWindows. Each of these fails silently when wrong: a lambda breaks the Cecil
/// transplant, a fallback that forgets ScreenManager.Platform renders through the dead
/// Vulkan platform, a member missing from Program.cs ships nothing.
/// </summary>
public class PlatformClientProgramCoverageTests
{
    private static string StartRegion()
    {
        string program = ReadLib("Vintagestory.Client/ClientProgram.cs");
        int start = program.IndexOf("private unsafe void Start(ClientProgramArgs args, string[] rawArgs)", StringComparison.Ordinal);
        int end = program.IndexOf("private GameWindowNative AttemptToOpenWindow(", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "Start must precede AttemptToOpenWindow");
        return program.Substring(start, end - start);
    }

    [Fact]
    public void TheStartRegionHasNoLambda()
    {
        string region = StartRegion();

        Assert.Contains("private void ConfigureClientPlatform(ClientPlatformWindows clientPlatformWindows)", region);
        Assert.Contains("private void WireClientPlatform(ClientPlatformWindows clientPlatformWindows)", region);
        Assert.Contains("private void OptimumStartSinglePlayerServer(StartServerArgs serverargs)", region);
        Assert.DoesNotContain("delegate", region);
        foreach (string line in region.Split('\n'))
        {
            // Vanilla's window-mode switch expression arms (`3 => 3,`) are not lambdas.
            if (Regex.IsMatch(line, @"^\s*(\d+|_)\s*=>\s*\d+,?\s*$")) continue;
            Assert.DoesNotContain("=>", line);
        }
    }

    [Fact]
    public void StartCreatesTheBackendPlatformAndFallsBackToTheBase()
    {
        string region = StartRegion();

        int probe = region.IndexOf("OptimumRenderBootstrap.ShouldTryVulkan(", StringComparison.Ordinal);
        int create = region.IndexOf("OptimumRenderBootstrap.CreatePlatform(logger) as ClientPlatformWindows;", StringComparison.Ordinal);
        int fallback = region.IndexOf("clientPlatformWindows = new ClientPlatformWindows(logger);", StringComparison.Ordinal);
        int configure = region.IndexOf("ConfigureClientPlatform(clientPlatformWindows);", StringComparison.Ordinal);
        int screenManager = region.IndexOf("screenManager = new ScreenManager(clientPlatformWindows);", StringComparison.Ordinal);
        int window = region.IndexOf("AttemptToOpenWindow(gameWindowSettings, val2, num3, num4, 3);", StringComparison.Ordinal);
        int initialize = region.IndexOf("clientPlatformWindows.InitializeGraphics(", StringComparison.Ordinal);

        Assert.True(probe >= 0 && create > probe, "the probe runs before the platform is created");
        Assert.True(fallback > create, "a null platform falls back to the base constructor");
        Assert.True(configure > fallback && screenManager > configure);
        Assert.True(window > screenManager && initialize > window, "graphics initialize after the window opens");
        Assert.DoesNotContain("OptimumRenderBootstrap.Install", region);
        Assert.DoesNotContain("OptimumRenderBootstrap.Shutdown", region);
    }

    [Fact]
    public void TheFallbackReassignsScreenManagerPlatform()
    {
        string region = StartRegion();

        int reason = region.IndexOf("\"[Optimum] Vulkan unavailable, reopening for OpenGL: \" + optimumInstallReason", StringComparison.Ordinal);
        Assert.True(reason >= 0, "the fallback log line keeps its shape");
        int reopen = region.IndexOf("AttemptToOpenWindow(gameWindowSettings, val2, num3, num4, 3);", reason, StringComparison.Ordinal);
        int rebuild = region.IndexOf("clientPlatformWindows = new ClientPlatformWindows(logger);", reason, StringComparison.Ordinal);
        int configure = region.IndexOf("ConfigureClientPlatform(clientPlatformWindows);", reason, StringComparison.Ordinal);
        int assign = region.IndexOf("ScreenManager.Platform = clientPlatformWindows;", reason, StringComparison.Ordinal);
        int start = region.IndexOf("screenManager.Start(args, rawArgs);", StringComparison.Ordinal);

        Assert.True(reopen > reason, "the window is reopened for OpenGL");
        Assert.True(rebuild > reopen && configure > rebuild && assign > configure,
            "the fallback builds, wires and publishes a base platform");
        Assert.True(start > assign, "the swap happens before screenManager.Start");
    }

    [Fact]
    public void ShutdownGraphicsRunsInTheFinallyBeforeTheWindowIsDisposed()
    {
        string region = StartRegion();

        int run = region.IndexOf("((GameWindow)gameWindowNative).Run();", StringComparison.Ordinal);
        Assert.True(run >= 0);
        int finallyBlock = region.IndexOf("finally", run, StringComparison.Ordinal);
        int shutdown = region.IndexOf("clientPlatformWindows.ShutdownGraphics();", run, StringComparison.Ordinal);
        int dispose = region.IndexOf("((NativeWindow)gameWindowNative).Dispose();", run, StringComparison.Ordinal);

        Assert.True(finallyBlock > run && shutdown > finallyBlock && dispose > shutdown);
    }

    /// <summary>
    /// Step-1 review finding: a throw after InitializeGraphics succeeded but before the Run
    /// block (window setup, screenManager.Start, the platform's Start) skipped the Run
    /// finally, so the device stayed alive and the Vulkan crash marker survived a clean
    /// failure. Everything between the bring-up and Run now sits in a try whose catch shuts
    /// graphics down and rethrows.
    /// </summary>
    [Fact]
    public void AThrowBetweenGraphicsBringUpAndRunStillShutsGraphicsDown()
    {
        string region = StartRegion();

        int initialize = region.IndexOf("clientPlatformWindows.InitializeGraphics(", StringComparison.Ordinal);
        int run = region.IndexOf("((GameWindow)gameWindowNative).Run();", StringComparison.Ordinal);
        Assert.True(initialize >= 0 && run > initialize);
        string between = region.Substring(initialize, run - initialize);

        var guard = Regex.Match(between, @"try\s*\{\s*if \(\(int\)val == 0 && !RuntimeEnv\.IsWaylandSession\)");
        Assert.True(guard.Success, "the window setup after the bring-up is not inside a try");
        var handler = Regex.Match(between, @"catch \(Exception\)\s*\{\s*clientPlatformWindows\.ShutdownGraphics\(\);\s*throw;\s*\}");
        Assert.True(handler.Success, "no catch shuts graphics down and rethrows before Run");

        int screenStart = between.IndexOf("screenManager.Start(args, rawArgs);", StringComparison.Ordinal);
        int platformStart = between.IndexOf("clientPlatformWindows.Start();", StringComparison.Ordinal);
        int audio = between.IndexOf("clientPlatformWindows.StartAudio();", StringComparison.Ordinal);
        Assert.True(audio > guard.Index && screenStart > audio && platformStart > screenStart
            && handler.Index > platformStart, "screenManager.Start and the platform Start are guarded");
    }

    /// <summary>
    /// Step-1 review finding: the wiring vanilla does after LogAndTestHardwareInfosStage1,
    /// the install and message-box checks and the signal handlers had moved before all of
    /// them on every path. Only the renderer probe and the platform construction may move
    /// earlier; the rest keeps vanilla's order (<c>_ref/.../ClientProgram.cs</c>).
    /// </summary>
    [Fact]
    public void StartKeepsVanillasOrderAroundThePlatformWiring()
    {
        string region = StartRegion();

        string[] anchors =
        {
            "OptimumRenderBootstrap.ShouldTryVulkan(",
            "clientPlatformWindows = new ClientPlatformWindows(logger);",
            "ConfigureClientPlatform(clientPlatformWindows);",
            "clientPlatformWindows.LogAndTestHardwareInfosStage1();",
            "screenManager = new ScreenManager(clientPlatformWindows);",
            "if (!Directory.Exists(GamePaths.AssetsPath))",
            "if (!CleanInstallCheck.IsCleanInstall())",
            "Signals[1] = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnExit);",
            "WireClientPlatform(clientPlatformWindows);",
            "WindowState val = ",
            "AttemptToOpenWindow(gameWindowSettings, val2, num3, num4, 3);",
        };
        int previous = -1;
        foreach (string anchor in anchors)
        {
            int at = region.IndexOf(anchor, previous + 1, StringComparison.Ordinal);
            Assert.True(at > previous, "out of vanilla order or missing: " + anchor);
            previous = at;
        }

        string configure = MethodBody(region, "private void ConfigureClientPlatform(ClientPlatformWindows clientPlatformWindows)");
        Assert.Contains("clientPlatformWindows.ShaderUniforms.SepiaLevel = ClientSettings.SepiaLevel;", configure);
        Assert.Contains("CrashReporter.SetLogger((Logger)clientPlatformWindows.Logger);", configure);
        Assert.DoesNotContain("SetServerExitInterface", configure);
        Assert.DoesNotContain("platform = clientPlatformWindows;", configure);

        string wire = MethodBody(region, "private void WireClientPlatform(ClientPlatformWindows clientPlatformWindows)");
        int exit = wire.IndexOf("clientPlatformWindows.SetServerExitInterface(clientPlatformWindows.ServerExitState);", StringComparison.Ordinal);
        int reporter = wire.IndexOf("clientPlatformWindows.crashreporter = crashreporter;", StringComparison.Ordinal);
        int assign = wire.IndexOf("platform = clientPlatformWindows;", StringComparison.Ordinal);
        int server = wire.IndexOf("clientPlatformWindows.OnStartSinglePlayerServer = OptimumStartSinglePlayerServer;", StringComparison.Ordinal);
        Assert.True(exit >= 0 && reporter > exit && assign > reporter && server > assign);

        // The OpenGL fallback applies both halves to the platform it builds.
        int reason = region.IndexOf("\"[Optimum] Vulkan unavailable, reopening for OpenGL: \" + optimumInstallReason", StringComparison.Ordinal);
        int fallbackConfigure = region.IndexOf("ConfigureClientPlatform(clientPlatformWindows);", reason, StringComparison.Ordinal);
        int fallbackWire = region.IndexOf("WireClientPlatform(clientPlatformWindows);", reason, StringComparison.Ordinal);
        int fallbackAssign = region.IndexOf("ScreenManager.Platform = clientPlatformWindows;", reason, StringComparison.Ordinal);
        Assert.True(reason >= 0 && fallbackConfigure > reason && fallbackWire > fallbackConfigure && fallbackAssign > fallbackWire);

        // Where the vanilla reference is available, its two wiring blocks sit on the
        // same sides of the same anchors.
        string? vanilla = TryRead("_ref/VintagestoryLib/Vintagestory.Client/ClientProgram.cs");
        if (vanilla != null)
        {
            int stage1 = vanilla.IndexOf("clientPlatformWindows.LogAndTestHardwareInfosStage1();", StringComparison.Ordinal);
            int logger = vanilla.IndexOf("CrashReporter.SetLogger((Logger)clientPlatformWindows.Logger);", StringComparison.Ordinal);
            int signals = vanilla.IndexOf("Signals[1] = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnExit);", StringComparison.Ordinal);
            int vanillaExit = vanilla.IndexOf("clientPlatformWindows.SetServerExitInterface(clientPlatformWindows.ServerExitState);", StringComparison.Ordinal);
            Assert.True(logger >= 0 && stage1 > logger && signals > stage1 && vanillaExit > signals);
        }
    }

    [Fact]
    public void TheRendererLogLinesKeepTheirShape()
    {
        string region = StartRegion();

        Assert.Contains("Console.WriteLine(\"[Optimum] Vulkan renderer: \" +", region);
        Assert.Contains("Console.WriteLine(\"[Optimum] OpenGL renderer: \" + optimumRendererReason);", region);
        Assert.Contains("Console.WriteLine(\"[Optimum] OpenGL renderer: selected by config\");", region);
        Assert.Contains("Console.WriteLine(\"[Optimum] Vulkan unavailable, reopening for OpenGL: \" + optimumInstallReason);", region);
    }

    [Fact]
    public void CreatePlatformReturnsObjectAndInstallIsGone()
    {
        string bootstrap = Read("sources/VintagestoryApi/Client/optimum-render-bootstrap.cs");

        Assert.Contains("public static object CreatePlatform(object logger)", bootstrap);
        Assert.Contains("\"Optimum.Render.Vulkan.Platform.VulkanClientPlatform\"", bootstrap);
        Assert.DoesNotContain("public static bool Install(", bootstrap);
    }

    [Fact]
    public void TheAbstractPlatformDeclaresTheGraphicsVirtuals()
    {
        string platform = ReadLib("Vintagestory.Client.NoObf/ClientPlatformAbstract.cs");

        Assert.Contains("public virtual bool InitializeGraphics(IntPtr windowHandle, int width, int height, out string reason)", platform);
        Assert.Contains("public virtual void ShutdownGraphics()", platform);
    }

    [Fact]
    public void ThePatcherListsEveryNewMember()
    {
        string patcher = PatcherSource.Read();

        string abstractMembers = Block(patcher, "[\"Vintagestory.Client.NoObf.ClientPlatformAbstract\"] = new()");
        Assert.Contains("\"InitializeGraphics\",", abstractMembers);
        Assert.Contains("\"ShutdownGraphics\",", abstractMembers);

        string programMembers = Block(patcher, "[\"Vintagestory.Client.ClientProgram\"] = new()");
        Assert.Contains("\"ConfigureClientPlatform\",", programMembers);
        Assert.Contains("\"WireClientPlatform\",", programMembers);
        Assert.Contains("\"OptimumStartSinglePlayerServer\",", programMembers);

        Assert.Contains("new(\"Vintagestory.Client.ClientProgram\", \"Start\", 2)", patcher);
    }

    [Fact]
    public void VulkanClientPlatformDerivesFromClientPlatformWindows()
    {
        string platform = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.cs");

        Assert.Contains("namespace Optimum.Render.Vulkan.Platform;", platform);
        Assert.Contains("public partial class VulkanClientPlatform : ClientPlatformWindows", platform);
        Assert.Contains("public VulkanClientPlatform(Logger logger) : base(logger)", platform);
        Assert.Contains("public override bool InitializeGraphics(IntPtr windowHandle, int width, int height, out string reason)", platform);
        Assert.Contains("public override void ShutdownGraphics()", platform);
        Assert.Contains("\"OPTIMUM_VULKAN_FORCE_INSTALL_FAILURE\"", platform);
        // The main file holds bring-up and teardown only; the graphics overrides (Phase 1A
        // step 4) live in the VulkanClientPlatform.*.cs partial files.
        Assert.Equal(2, Regex.Matches(platform, @"^\s*(public|protected|internal)\s+override\s", RegexOptions.Multiline).Count);
    }

    [Fact]
    public void TheRendererCompilesAgainstTheDonorWithoutShippingIt()
    {
        string renderer = Regex.Replace(Read("Optimum.Render.Vulkan/Optimum.Render.Vulkan.csproj"), @"\s+", " ");
        // Compile-only: no copy, and no NuGet or transitive project flow that
        // CopyLocalLockFileAssemblies would copy into the shared deploy output.
        Assert.Contains("<ProjectReference Include=\"..\\build\\VintagestoryLib\\VintagestoryLib.csproj\"> <Private>false</Private> <ExcludeAssets>all</ExcludeAssets> </ProjectReference>", renderer);
        Assert.Contains("<DisableTransitiveProjectReferences>true</DisableTransitiveProjectReferences>", renderer);

        string makefile = Read("Makefile");
        Assert.DoesNotContain("$(MOD_OUT)/VintagestoryLib", makefile);
        foreach (string script in new[] { "scripts/package-linux.sh", "scripts/package-macos.sh" })
            Assert.DoesNotContain("$MOD_OUT/VintagestoryLib", Read(script));
    }

    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing: " + signature);
        int open = source.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
        }
        throw new InvalidOperationException("unbalanced body: " + signature);
    }

    private static string? TryRead(string relativePath)
    {
        try
        {
            return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static string Block(string source, string header)
    {
        int start = source.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing: " + header);
        int end = source.IndexOf("},", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return source.Substring(start, end - start);
    }

    private static string ReadLib(string relativePath)
    {
        string build = "build/VintagestoryLib/" + relativePath;
        try
        {
            return File.ReadAllText(PatchReader.FindRepositoryFile(build));
        }
        catch (FileNotFoundException)
        {
            return PatchReader.ReadPatchedContent(PatchReader.FindRepositoryFile(
                "patches/VintagestoryLib/" + relativePath + ".patch"));
        }
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
}
}

// Source: Optimum.Tests/platform-program-ubo-virtuals-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// Vulkan-native plan, Phase 1A step 3: shader program, uniform and UBO calls are
/// ClientPlatformAbstract virtuals. ShaderProgramBase and UBO are back to the vanilla
/// shape with ScreenManager.Platform calls where the GL lines were; ClientPlatformWindows
/// holds the device branch and the GL lines as overrides. A direct device or GL call left
/// in either class would bypass whichever platform the client installed.
/// </summary>
public class PlatformProgramUboVirtualsCoverageTests
{
    private const string AbstractPath = "Vintagestory.Client.NoObf/ClientPlatformAbstract.cs";
    private const string WindowsPath = "Vintagestory.Client.NoObf/ClientPlatformWindows.cs";
    private const string ProgramPath = "Vintagestory.Client.NoObf/ShaderProgramBase.cs";
    private const string UboPath = "Vintagestory.Client.NoObf/UBO.cs";

    /// <summary>The member, its parameter list, the device call and the GL call its override must hold.</summary>
    private static readonly (string Name, string Parameters, string Device, string Gl)[] Members =
    {
        ("UseShaderProgram", "int programId", "", "GL.UseProgram(programId);"),
        ("DisposeShaderProgram", "ShaderProgramBase program", "optimumDevice.DeleteProgram(program.ProgramId);", "GL.DeleteProgram(program.ProgramId);"),
        ("BindSampler", "int unit, int samplerId", "stated.BindSampler(unit, samplerId);", "GL.BindSampler(unit, samplerId);"),
        ("SetUniform", "int programId, int location, float value", "optimumDevice.SetUniform(programId, location, value);", "GL.Uniform1(location, value);"),
        ("SetUniform", "int programId, int location, int value", "optimumDevice.SetUniform(programId, location, value);", "GL.Uniform1(location, value);"),
        ("SetUniform", "int programId, int location, float x, float y", "optimumDevice.SetUniform(programId, location, x, y);", "GL.Uniform2(location, x, y);"),
        ("SetUniform", "int programId, int location, float x, float y, float z", "optimumDevice.SetUniform(programId, location, x, y, z);", "GL.Uniform3(location, x, y, z);"),
        ("SetUniform", "int programId, int location, float x, float y, float z, float w", "optimumDevice.SetUniform(programId, location, x, y, z, w);", "GL.Uniform4(location, x, y, z, w);"),
        ("SetUniform", "int programId, int location, int x, int y, int z", "optimumDevice.SetUniform(programId, location, x, y, z);", "GL.Uniform3(location, x, y, z);"),
        ("SetUniformArray1", "int programId, int location, int count, float[] values", "optimumDevice.SetUniformArray1(programId, location, count, values);", "GL.Uniform1(location, count, values);"),
        ("SetUniformArray2", "int programId, int location, int count, float[] values", "optimumDevice.SetUniformArray2(programId, location, count, values);", "GL.Uniform2(location, count, values);"),
        ("SetUniformArray3", "int programId, int location, int count, float[] values", "optimumDevice.SetUniformArray3(programId, location, count, values);", "GL.Uniform3(location, count, values);"),
        ("SetUniformArray4", "int programId, int location, int count, float[] values", "optimumDevice.SetUniformArray4(programId, location, count, values);", "GL.Uniform4(location, count, values);"),
        ("SetUniformMatrix", "int programId, int location, float[] matrix", "optimumDevice.SetUniformMatrix(programId, location, matrix);", "GL.UniformMatrix4(location, 1, false, matrix);"),
        ("SetUniformMatrix", "int programId, int location, ref Matrix4 matrix", "optimumDevice.SetUniformMatrix(programId, location, optimumMatrix);", "GL.UniformMatrix4(location, false, ref matrix);"),
        ("SetUniformMatrices", "int programId, int location, int count, float[] matrices", "optimumDevice.SetUniformMatrices(programId, location, count, matrices);", "GL.UniformMatrix4(location, count, false, matrices);"),
        ("SetUniformMatrices4x3", "int programId, int location, int count, float[] matrices", "optimumDevice.SetUniformMatrices4x3(programId, location, count, matrices);", "GL.UniformMatrix4x3(location, count, false, matrices);"),
        ("BindProgramTexture2D", "ShaderProgramBase program, string samplerName, int textureId, int textureNumber", "stated.BindTexture(textureNumber, textureId);", "GL.BindTexture((TextureTarget)3553, textureId);"),
        ("BindProgramTextureCube", "ShaderProgramBase program, string samplerName, int textureId, int textureNumber", "stated.BindTexture(textureNumber, textureId);", "GL.BindTexture((TextureTarget)34067, textureId);"),
        ("BindUBO", "UBO ubo", "optimumDevice.BindUniformBuffer(ubo.Handle);", "GL.BindBufferBase((BufferRangeTarget)35345, ubo.BindingPoint, ubo.Handle);"),
        ("UnbindUBO", "UBO ubo", "optimumDevice.UnbindUniformBuffer(ubo.Handle);", "GL.BindBuffer((BufferTarget)35345, 0);"),
        ("UpdateUBO", "UBO ubo, IntPtr data, int offset, int size, bool reallocate", "optimumDevice.UpdateUniformBuffer(ubo.Handle, data, offset, size);", "GL.BufferSubData((BufferTarget)35345, (IntPtr)offset, size, data);"),
        ("DeleteUBO", "UBO ubo", "optimumDevice.DeleteUniformBuffer(ubo.Handle);", "GL.DeleteBuffers(1, ref ubo.Handle);"),
    };

    [Theory]
    [InlineData(ProgramPath)]
    [InlineData(UboPath)]
    public void TheClassHasNoDeviceBranchAndNoDirectGlCall(string path)
    {
        string code = StripComments(ReadLib(path));

        Assert.DoesNotContain("OptimumRender.Device", code);
        Assert.DoesNotContain("IOptimumGraphicsDevice", code);
        Assert.DoesNotContain("optimumDevice", code);
        Assert.DoesNotMatch(new Regex(@"\bGL\."), code);
    }

    [Fact]
    public void ShaderProgramBaseRoutesEveryMovedOperationThroughThePlatform()
    {
        string program = StripComments(ReadLib(ProgramPath));

        var expected = new (string Method, string Call)[]
        {
            ("public void Uniform(string uniformName, float value)", "ScreenManager.Platform.SetUniform(ProgramId, uniformLocations[uniformName], value);"),
            ("public void Uniform(string uniformName, int count, float[] value)", "ScreenManager.Platform.SetUniformArray1(ProgramId, uniformLocations[uniformName], count, value);"),
            ("public void Uniform(string uniformName, int value)", "ScreenManager.Platform.SetUniform(ProgramId, uniformLocations[uniformName], value);"),
            ("public void Uniform(string uniformName, Vec2f value)", "ScreenManager.Platform.SetUniform(ProgramId, uniformLocations[uniformName], value.X, value.Y);"),
            ("public void Uniform(string uniformName, Vec2i value)", "ScreenManager.Platform.SetUniform(ProgramId, uniformLocations[uniformName], (float)value.X, (float)value.Y);"),
            ("public void Uniform(string uniformName, float valueX, float valueY)", "ScreenManager.Platform.SetUniform(ProgramId, uniformLocations[uniformName], valueX, valueY);"),
            ("public void Uniform(string uniformName, Vec3f value)", "ScreenManager.Platform.SetUniform(ProgramId, uniformLocations[uniformName], value.X, value.Y, value.Z);"),
            ("public void Uniform(string uniformName, float valueX, float valueY, float valueZ)", "ScreenManager.Platform.SetUniform(ProgramId, uniformLocations[uniformName], valueX, valueY, valueZ);"),
            ("public void Uniform(string uniformName, float valueX, float valueY, float valueZ, float valueW)", "ScreenManager.Platform.SetUniform(ProgramId, uniformLocations[uniformName], valueX, valueY, valueZ, valueW);"),
            ("public void Uniform(string uniformName, Vec3i value)", "ScreenManager.Platform.SetUniform(ProgramId, uniformLocations[uniformName], value.X, value.Y, value.Z);"),
            ("public void Uniforms2(string uniformName, int count, float[] values)", "ScreenManager.Platform.SetUniformArray2(ProgramId, uniformLocations[uniformName], count, values);"),
            ("public void Uniforms3(string uniformName, int count, float[] values)", "ScreenManager.Platform.SetUniformArray3(ProgramId, uniformLocations[uniformName], count, values);"),
            ("public void Uniform(string uniformName, Vec4f value)", "ScreenManager.Platform.SetUniform(ProgramId, uniformLocations[uniformName], value.X, value.Y, value.Z, value.W);"),
            ("public void Uniforms4(string uniformName, int count, float[] values)", "ScreenManager.Platform.SetUniformArray4(ProgramId, uniformLocations[uniformName], count, values);"),
            ("public void UniformMatrix(string uniformName, float[] matrix)", "ScreenManager.Platform.SetUniformMatrix(ProgramId, uniformLocations[uniformName], matrix);"),
            ("public void UniformMatrix(string uniformName, ref Matrix4 matrix)", "ScreenManager.Platform.SetUniformMatrix(ProgramId, uniformLocations[uniformName], ref matrix);"),
            ("public void BindTexture2D(string samplerName, int textureId, int textureNumber)", "ScreenManager.Platform.BindProgramTexture2D(this, samplerName, textureId, textureNumber);"),
            ("public void BindTextureCube(string samplerName, int textureId, int textureNumber)", "ScreenManager.Platform.BindProgramTextureCube(this, samplerName, textureId, textureNumber);"),
            ("public void UniformMatrices4x3(string uniformName, int count, float[] matrix)", "ScreenManager.Platform.SetUniformMatrices4x3(ProgramId, uniformLocations[uniformName], count, matrix);"),
            ("public void UniformMatrices(string uniformName, int count, float[] matrix)", "ScreenManager.Platform.SetUniformMatrices(ProgramId, uniformLocations[uniformName], count, matrix);"),
            ("public void Use()", "ScreenManager.Platform.UseShaderProgram(ProgramId);"),
            ("public void Stop()", "ScreenManager.Platform.UseShaderProgram(0);"),
            ("public void Stop()", "ScreenManager.Platform.BindSampler(i, 0);"),
            ("public void Dispose()", "ScreenManager.Platform.DisposeShaderProgram(this);"),
        };

        foreach ((string method, string call) in expected)
        {
            Assert.True(Body(program, method).Contains(call, StringComparison.Ordinal), method + " does not call " + call);
        }

        // The TAA hooks stay in the program, ahead of the platform call.
        Assert.Contains("if (OptimumEntityMotion.Enabled) OptimumEntityMotion.NoteWarpUniform(uniformName, value);", program);
        Assert.Contains("if (OptimumEntityMotion.Enabled && uniformName == \"modelMatrix\") OptimumEntityMotion.NoteModelMatrix(matrix);", program);
    }

    [Fact]
    public void UboRoutesEveryMovedOperationThroughThePlatform()
    {
        string ubo = StripComments(ReadLib(UboPath));

        Assert.Contains("ScreenManager.Platform.BindUBO(this);", Body(ubo, "public override void Bind()"));
        Assert.Contains("ScreenManager.Platform.UnbindUBO(this);", Body(ubo, "public override void Unbind()"));
        Assert.Contains("ScreenManager.Platform.DeleteUBO(this);", Body(ubo, "public override void Dispose()"));
        // Update<T>(data) replaced the whole buffer on GL (glBufferData); the ranged ones write into it.
        Assert.Contains("ScreenManager.Platform.UpdateUBO(this, (IntPtr)gCHandleProvider.Pointer, 0, base.Size, true);",
            Body(ubo, "public override void Update<T>(T data)"));
        Assert.Contains("ScreenManager.Platform.UpdateUBO(this, (IntPtr)gCHandleProvider.Pointer, offset, size, false);",
            Body(ubo, "public override void Update<T>(T data, int offset, int size)"));
        Assert.Contains("ScreenManager.Platform.UpdateUBO(this, (IntPtr)num, offset, size, false);",
            Body(ubo, "public override void Update(object data, int offset, int size)"));
    }

    [Fact]
    public void TheAbstractPlatformDeclaresEveryOperationWithAnEmptyBody()
    {
        string platform = ReadLib(AbstractPath);

        foreach ((string name, string parameters, _, _) in Members)
        {
            string signature = "public virtual void " + name + "(" + parameters.Replace("ref Matrix4", "ref OpenTK.Mathematics.Matrix4") + ")";
            string inner = Regex.Replace(Body(platform, signature), @"\s+", " ").Trim();
            Assert.True(inner == "{ }", signature + " is not empty: " + inner);
        }
    }

    /// <summary>
    /// Phase 1A step 4: ClientPlatformWindows overrides every operation with the GL lines only,
    /// and VulkanClientPlatform overrides the same operation with the device call that used
    /// to be the branch in front of them.
    /// </summary>
    [Fact]
    public void ClientPlatformWindowsOverridesEveryOperationWithTheGlLinesAndVulkanClientPlatformWithTheDeviceCall()
    {
        string platform = ReadLib(WindowsPath);
        string vulkan = VulkanPlatformSource.Read();

        foreach ((string name, string parameters, string device, string gl) in Members)
        {
            string signature = "public override void " + name + "(" + parameters + ")";
            Assert.Single(Regex.Matches(platform, Regex.Escape(signature)));
            string body = Body(platform, signature);
            Assert.True(body.Contains(gl, StringComparison.Ordinal), signature + " does not issue " + gl);
            Assert.False(body.Contains("optimumDevice", StringComparison.Ordinal), signature + " still has a device branch");

            Assert.Single(Regex.Matches(vulkan, Regex.Escape(signature)));
            string deviceCall = device.Replace("optimumDevice.", "device.");
            Assert.True(Body(vulkan, signature).Contains(deviceCall, StringComparison.Ordinal),
                "VulkanClientPlatform." + name + " does not call " + deviceCall);
        }

        // The whole-buffer update keeps glBufferData on GL.
        Assert.Contains("GL.BufferData((BufferTarget)35345, size, data, (BufferUsageHint)35048);",
            Body(platform, "public override void UpdateUBO(UBO ubo, IntPtr data, int offset, int size, bool reallocate)"));
        // The current program is the client's (ShaderProgramBase.CurrentShaderProgram): nothing to record.
        Assert.DoesNotContain("device.", Body(vulkan, "public override void UseShaderProgram(int programId)"));
        // A unit with no custom sampler has any override cleared in the stated state.
        Assert.Contains("stated.BindSampler(textureNumber, 0);",
            Body(vulkan, "public override void BindProgramTexture2D(ShaderProgramBase program, string samplerName, int textureId, int textureNumber)"));
    }

    [Fact]
    public void ThePatcherInjectsTheVirtualsAndTheOverridesAndKeepsTheBodyTargets()
    {
        string patcher = PatcherSource.Read();

        string abstractMembers = Block(patcher, "[\"Vintagestory.Client.NoObf.ClientPlatformAbstract\"] = new()");
        string windowsMembers = Block(patcher, "[\"Vintagestory.Client.NoObf.ClientPlatformWindows\"] = new()");
        var names = new HashSet<string>();
        foreach ((string name, _, _, _) in Members) names.Add(name);
        foreach (string name in names)
        {
            Assert.Contains("\"" + name + "\",", abstractMembers);
            Assert.Contains("\"" + name + "\",", windowsMembers);
        }

        foreach (string target in new[]
        {
            "new(\"Vintagestory.Client.NoObf.ShaderProgramBase\", \"Use\", 0)",
            "new(\"Vintagestory.Client.NoObf.ShaderProgramBase\", \"Stop\", 0)",
            "new(\"Vintagestory.Client.NoObf.ShaderProgramBase\", \"Dispose\", 0)",
            "new(\"Vintagestory.Client.NoObf.ShaderProgramBase\", \"BindTexture2D\", 3)",
            "new(\"Vintagestory.Client.NoObf.ShaderProgramBase\", \"BindTextureCube\", 3)",
            "new(\"Vintagestory.Client.NoObf.ShaderProgramBase\", \"UniformMatrices\", 3)",
            "new(\"Vintagestory.Client.NoObf.ShaderProgramBase\", \"UniformMatrices4x3\", 3)",
            "new(\"Vintagestory.Client.NoObf.UBO\", \"Bind\", 0)",
            "new(\"Vintagestory.Client.NoObf.UBO\", \"Unbind\", 0)",
            "new(\"Vintagestory.Client.NoObf.UBO\", \"Dispose\", 0)",
        })
        {
            Assert.Contains(target, patcher);
        }
    }

    /// <summary>
    /// Outside the platform calls and the TAA hooks, ShaderProgramBase is vanilla again:
    /// every line the patch adds is one of those, a comment or blank.
    /// </summary>
    [Fact]
    public void ShaderProgramBaseDiffersFromVanillaOnlyByPlatformCallsAndTaaHooks()
    {
        string patch = Read("patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderProgramBase.cs.patch");
        var offenders = new List<string>();
        foreach (string raw in patch.Split('\n'))
        {
            if (!raw.StartsWith("+", StringComparison.Ordinal) || raw.StartsWith("+++", StringComparison.Ordinal)) continue;
            string line = raw.Substring(1).Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.StartsWith("ScreenManager.Platform.", StringComparison.Ordinal)) continue;
            if (line.StartsWith("if (OptimumEntityMotion.Enabled", StringComparison.Ordinal)) continue;
            offenders.Add(line);
        }
        Assert.True(offenders.Count == 0, "non-routing additions:\n" + string.Join("\n", offenders));
    }

    private static string StripComments(string source) =>
        Regex.Replace(source, @"//[^\n]*", string.Empty);

    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing: " + signature);
        int open = source.IndexOf('{', start + signature.Length);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
        }
        throw new InvalidOperationException("unbalanced body: " + signature);
    }

    private static string Block(string source, string header)
    {
        int start = source.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing: " + header);
        int end = source.IndexOf("},", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return source.Substring(start, end - start);
    }

    private static string ReadLib(string relativePath)
    {
        try
        {
            return File.ReadAllText(PatchReader.FindRepositoryFile("build/VintagestoryLib/" + relativePath));
        }
        catch (FileNotFoundException)
        {
            return PatchReader.ReadPatchedContent(PatchReader.FindRepositoryFile(
                "patches/VintagestoryLib/" + relativePath + ".patch"));
        }
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
}
}
