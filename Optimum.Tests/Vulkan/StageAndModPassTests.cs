// Source: Optimum.Tests/mod-pass-api-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Vintagestory.API.Client;
using Xunit;

/// <summary>
/// Vulkan-native plan, Phase 5: the mod pass and motion-writer API. The contract types are data
/// holders in Optimum.Api.Contracts (no lib types); registration validates against the contract,
/// copies the declaration, stores it per mod and drops it when the client leaves the world; only the
/// Vulkan platform reads it, from the end of the stage bracket, with the mod-hosted pass flags. The
/// GPU side (slot, attachment states, validation) is ModPassHostingTests in Optimum.Render.Vulkan.Tests.
/// </summary>
[Collection("OptimumModPasses")]
public class ModPassApiCoverageTests
{
    private const string ModId = "coverage-mod";

    public class ClientApiStub : DispatchProxy
    {
        public readonly List<Action> Handlers = new();
        private IClientEventAPI? events;
        private ClientApiStub? owner;

        public static (ICoreClientAPI Api, ClientApiStub Stub) Create()
        {
            ICoreClientAPI api = Create<ICoreClientAPI, ClientApiStub>();
            var stub = (ClientApiStub)(object)api;
            IClientEventAPI events = Create<IClientEventAPI, ClientApiStub>();
            ((ClientApiStub)(object)events).owner = stub;
            stub.events = events;
            return (api, stub);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
            case "get_Event": return events;
            case "add_LeaveWorld": (owner ?? this).Handlers.Add((Action)args![0]!); return null;
            case "remove_LeaveWorld": (owner ?? this).Handlers.Remove((Action)args![0]!); return null;
            }
            Type type = targetMethod.ReturnType;
            return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
        }
    }

    private static OptimumPassDecl ValidPass(string name = "tint") => new()
    {
        Name = name,
        Slot = EnumOptimumPass.AfterOIT,
        Reads = new[] { EnumOptimumAttachment.PrimaryGlow },
        Writes = new[] { EnumOptimumAttachment.PrimaryColor, EnumOptimumAttachment.PrimaryDepth },
        Draw = static _ => { },
        MotionWriter = new OptimumMotionWriterDecl { Name = name, Mode = EnumOptimumMotionWrite.WithColor },
    };

    private static string Read(string path) => File.ReadAllText(PatchReader.FindRepositoryFile(path));

    [Fact]
    public void TheSlotsMirrorTheRenderStages()
    {
        foreach (EnumRenderStage stage in Enum.GetValues<EnumRenderStage>())
            Assert.Equal(stage.ToString(), ((EnumOptimumPass)(int)stage).ToString());
        Assert.Equal(Enum.GetValues<EnumRenderStage>().Length, Enum.GetValues<EnumOptimumPass>().Length);
    }

    [Fact]
    public void TheContractIsDataHoldersWithNoLibTypes()
    {
        Assembly contracts = typeof(OptimumPassDecl).Assembly;
        Assert.Equal("Optimum.Api.Contracts", contracts.GetName().Name);
        foreach (AssemblyName reference in contracts.GetReferencedAssemblies())
            Assert.NotEqual("VintagestoryLib", reference.Name);

        foreach (FieldInfo field in typeof(OptimumPassDecl).GetFields(BindingFlags.Public | BindingFlags.Instance))
            Assert.False(field.IsInitOnly, field.Name + " is a plain settable field");
        Assert.Empty(typeof(OptimumPassDecl).GetProperties(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(typeof(OptimumMotionWriterDecl).GetProperties(BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void ContractFileIsWiredIntoContractsAndRemovedFromTheFork()
    {
        Assert.Contains(@"..\sources\VintagestoryApi\Client\Render\OptimumModPasses.cs",
            Read("optimum-api-contracts/optimum-api-contracts.csproj"));
        Assert.Contains(@"<Compile Remove=""Client\Render\OptimumModPasses.cs"" />", Read("VintagestoryApi/VintagestoryAPI.csproj"));
        Assert.Contains(@"<Compile Remove=""Client\Render\OptimumModPasses.cs"" />", Read("sources/VintagestoryApi/VintagestoryAPI.csproj"));
        Assert.True(File.Exists(PatchReader.FindRepositoryFile("sources/VintagestoryApi/Client/Render/OptimumModPasses.cs")));
    }

    [Theory]
    [InlineData("motion-write", "PrimaryMotion directly")]
    [InlineData("feedback", "feedback loop")]
    [InlineData("two-targets", "more than one target")]
    [InlineData("default-in-world", "only AfterBlit, Ortho and Done")]
    [InlineData("primary-after-blit", "after the scene was blitted")]
    [InlineData("motion-in-ortho", "motion window exists only in Opaque and AfterOIT")]
    [InlineData("shadow", "shadow stages")]
    [InlineData("read-only-write", "which is read only")]
    [InlineData("no-draw", "no draw callback")]
    [InlineData("nothing", "writes nothing")]
    public void RegistrationEnforcesTheContract(string breach, string expected)
    {
        OptimumPassDecl decl = ValidPass();
        switch (breach)
        {
        case "motion-write": decl.Writes = new[] { EnumOptimumAttachment.PrimaryColor, EnumOptimumAttachment.PrimaryMotion }; break;
        case "feedback": decl.Reads = new[] { EnumOptimumAttachment.PrimaryColor }; break;
        case "two-targets": decl.Writes = new[] { EnumOptimumAttachment.PrimaryColor, EnumOptimumAttachment.TransparentAccumulation }; decl.MotionWriter = null; break;
        case "default-in-world": decl.Writes = new[] { EnumOptimumAttachment.DefaultColor }; decl.Reads = Array.Empty<EnumOptimumAttachment>(); decl.MotionWriter = null; break;
        case "primary-after-blit": decl.Slot = EnumOptimumPass.AfterBlit; decl.MotionWriter = null; break;
        case "motion-in-ortho": decl.Slot = EnumOptimumPass.AfterPostProcessing; break;
        case "shadow": decl.Slot = EnumOptimumPass.ShadowNear; break;
        case "read-only-write": decl.Writes = new[] { EnumOptimumAttachment.GodRays }; decl.MotionWriter = null; break;
        case "no-draw": decl.Draw = null; break;
        case "nothing": decl.Writes = Array.Empty<EnumOptimumAttachment>(); decl.MotionWriter = null; break;
        }

        (ICoreClientAPI api, ClientApiStub _) = ClientApiStub.Create();
        Assert.False(OptimumModPasses.Register(api, ModId, decl, out string reason));
        Assert.Contains(expected, reason);
        Assert.Empty(OptimumModPasses.ForSlot(decl.Slot));
    }

    [Fact]
    public void AValidDeclarationIsCopiedStoredPerModAndReplacedByName()
    {
        (ICoreClientAPI api, ClientApiStub stub) = ClientApiStub.Create();
        try
        {
            OptimumPassDecl decl = ValidPass();
            Assert.True(OptimumPassContract.Validate(decl, out string? reason), reason);
            Assert.Equal(EnumOptimumTarget.Primary, OptimumPassContract.TargetOf(decl));
            Assert.True(OptimumModPasses.Register(api, ModId, decl, out reason), reason);
            Assert.True(OptimumModPasses.Register(api, "other-mod", ValidPass("other"), out reason), reason);

            OptimumPassRegistration[] slot = OptimumModPasses.ForSlot(EnumOptimumPass.AfterOIT);
            Assert.Equal(2, slot.Length);
            Assert.Equal(ModId, slot[0].ModId);
            Assert.NotSame(decl, slot[0].Decl);
            decl.Writes[0] = EnumOptimumAttachment.PrimaryGlow;
            Assert.Equal(EnumOptimumAttachment.PrimaryColor, OptimumModPasses.ForSlot(EnumOptimumPass.AfterOIT)[0].Decl.Writes[0]);
            Assert.Same(slot, OptimumModPasses.ForSlot(EnumOptimumPass.AfterOIT));

            long version = OptimumModPasses.Version;
            Assert.True(OptimumModPasses.Register(api, ModId, ValidPass(), out reason), reason);
            Assert.Equal(2, OptimumModPasses.PassCount);
            Assert.True(OptimumModPasses.Version > version);
            // One LeaveWorld subscription per mod, not per registration.
            Assert.Equal(2, stub.Handlers.Count);
        }
        finally
        {
            OptimumModPasses.UnregisterMod(ModId);
            OptimumModPasses.UnregisterMod("other-mod");
        }
        Assert.Equal(0, OptimumModPasses.PassCount);
    }

    [Fact]
    public void LeavingTheWorldClearsWhatTheModRegistered()
    {
        (ICoreClientAPI api, ClientApiStub stub) = ClientApiStub.Create();
        var writer = new OptimumMotionWriterDecl { Name = "renderer" };
        Assert.True(OptimumModPasses.Register(api, ModId, ValidPass(), out _));
        Assert.True(OptimumModPasses.RegisterMotionWriter(api, ModId, writer, out _));
        Assert.True(OptimumModPasses.IsRegisteredWriter(writer));
        Assert.Single(stub.Handlers);

        foreach (Action handler in stub.Handlers.ToArray()) handler();

        Assert.Empty(OptimumModPasses.ForSlot(EnumOptimumPass.AfterOIT));
        Assert.False(OptimumModPasses.IsRegisteredWriter(writer));
        Assert.Empty(stub.Handlers);
        Assert.DoesNotContain(ModId, OptimumModPasses.RegisteredMods());
    }

    [Fact]
    public void MotionWritersOpenOnlyThroughAnInstalledHookAndOnlyWhenRegistered()
    {
        (ICoreClientAPI api, ClientApiStub _) = ClientApiStub.Create();
        var writer = new OptimumMotionWriterDecl { Name = "renderer", Mode = EnumOptimumMotionWrite.MotionOnly };
        var calls = new List<string>();
        try
        {
            Assert.True(OptimumModPasses.RegisterMotionWriter(api, ModId, writer, out _));
            Assert.False(OptimumModPasses.BeginMotionWriter(writer), "no hook: OpenGL ignores writers");
            OptimumModPasses.EndMotionWriter();

            OptimumModPasses.MotionBeginHook = w => { calls.Add("begin " + w.Name); return true; };
            OptimumModPasses.MotionEndHook = () => calls.Add("end");
            Assert.False(OptimumModPasses.BeginMotionWriter(new OptimumMotionWriterDecl { Name = "stranger" }));
            Assert.True(OptimumModPasses.BeginMotionWriter(writer));
            OptimumModPasses.EndMotionWriter();
            Assert.Equal(new[] { "begin renderer", "end" }, calls);
        }
        finally
        {
            OptimumModPasses.MotionBeginHook = null;
            OptimumModPasses.MotionEndHook = null;
            OptimumModPasses.UnregisterMod(ModId);
        }
    }

    [Fact]
    public void TheModSystemEntryPointsFallBackToTheAssemblyName()
    {
        var system = new CoverageModSystem();
        Assert.Equal(typeof(CoverageModSystem).Assembly.GetName().Name, OptimumModRenderExtensions.OptimumModId(system));
    }

    private sealed class CoverageModSystem : Vintagestory.API.Common.ModSystem
    {
    }

    [Fact]
    public void OnlyTheVulkanPlatformHostsModPassesFromTheStageBracket()
    {
        string stages = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Stages.cs");
        int run = stages.IndexOf("RunModPasses(stage);", StringComparison.Ordinal);
        Assert.True(run > 0, "EndRenderStage runs the slot's mod passes");
        Assert.True(run < stages.IndexOf("InRenderStage = false;", StringComparison.Ordinal), "mod passes run inside the stage");
        Assert.True(run < stages.IndexOf("RenderStageListener?.OnEndRenderStage(stage);", StringComparison.Ordinal),
            "mod passes run before the stage's pass ends");

        string host = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.ModPasses.cs");
        Assert.Contains("internal const PassFlags ModPassFlags = PassFlags.OpenSampling | PassFlags.AllowSplit;", host);
        Assert.Contains("Flags = ModPassFlags,", host);
        Assert.Contains("BeginMotionOnlyWrite() : BeginMotionWrite()", host);
        Assert.Contains("if (motion) EndMotionWrite();", host);
        Assert.Contains("statedPass = plan.Declaration;", host);
        Assert.Contains("statedPass = null;", host);
        Assert.Contains("out string? refusal, declared);",
            Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeStated.cs"));

        string platform = VulkanPlatformSource.Read();
        Assert.Contains("InstallModPassHooks();", platform);
        Assert.Contains("RemoveModPassHooks();", platform);

        string gl = VulkanPlatformSource.ReadClientPlatformWindows();
        Assert.DoesNotContain("OptimumModPasses", gl);
        Assert.DoesNotContain("OptimumPassDecl", gl);
    }

    [Fact]
    public void TheModderDocumentationAndTheFixtureShip()
    {
        string doc = Read("docs/vulkan-mod-support.md");
        foreach (string term in new[] { "OptimumPassDecl", "EnumOptimumPass", "OptimumMotionWriterDecl", "TAAMOTIONLOCATION",
                     "BeginMotionWriter", "routes to OpenGL", "rewriter", "shaderincludes", "LeaveWorld" })
            Assert.Contains(term, doc);
        Assert.Contains("!docs/vulkan-mod-support.md", Read(".gitignore"));
        Assert.Contains("RegisterOptimumPass", Read("Optimum.Render.Vulkan.Tests/Fixtures/ModPassFixture/ModPassFixtureSystem.cs"));
    }
}
}

// Source: Optimum.Tests/render-stage-hooks-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// Vulkan-native plan, Phase 2 (contract C3): ClientMain.TriggerRenderStage brackets the stage's
/// renderers with ClientPlatformAbstract.BeginRenderStage/EndRenderStage, the method and both
/// virtuals reach the shipped DLL through Cecil, the OpenGL path keeps the neutral bodies and
/// VulkanClientPlatform overrides them (self-checked) and forwards to IRenderStageListener.
/// </summary>
public class RenderStageHooksCoverageTests
{
    private const string ClientMainPath = "Vintagestory.Client.NoObf/ClientMain.cs";
    private const string AbstractPath = "Vintagestory.Client.NoObf/ClientPlatformAbstract.cs";
    private const string TriggerSignature = "public void TriggerRenderStage(EnumRenderStage stage, float dt)";

    [Fact]
    public void TriggerRenderStageBracketsTheEvent()
    {
        string body = Body(ReadLib(ClientMainPath), TriggerSignature);

        int begin = body.IndexOf("stagePlatform.BeginRenderStage(stage);", StringComparison.Ordinal);
        int trigger = body.IndexOf("eventManager?.TriggerRenderStage(stage, dt);", StringComparison.Ordinal);
        int end = body.IndexOf("stagePlatform.EndRenderStage(stage);", StringComparison.Ordinal);
        int glCheck = body.IndexOf("Platform.CheckGlError(", StringComparison.Ordinal);
        Assert.True(begin >= 0, "BeginRenderStage missing:\n" + body);
        Assert.True(trigger > begin, "the event does not follow BeginRenderStage:\n" + body);
        Assert.True(end > trigger, "EndRenderStage does not follow the event:\n" + body);
        Assert.True(glCheck > end, "the GL error check moved inside the bracket:\n" + body);
        Assert.Single(Regex.Matches(body, @"BeginRenderStage\("));
        Assert.Single(Regex.Matches(body, @"EndRenderStage\("));
    }

    [Fact]
    public void TriggerRenderStageIsCecilSafe()
    {
        string body = StripComments(Body(ReadLib(ClientMainPath), TriggerSignature));
        Assert.DoesNotContain("=>", body);
        Assert.DoesNotContain("delegate", body);
        Assert.DoesNotContain("static ", body);
        Assert.False(Regex.IsMatch(body, @"\.(All|Any|Where|Select|First|Count)\s*\("), "LINQ in a transplanted method:\n" + body);
    }

    [Fact]
    public void ThePatcherShipsTheMethodAndTheVirtuals()
    {
        string patcher = PatcherSource.Read();
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ClientMain\", \"TriggerRenderStage\", 2),", patcher);

        string injected = Block(patcher, "[\"Vintagestory.Client.NoObf.ClientPlatformAbstract\"] = new()", "},");
        Assert.Contains("\"BeginRenderStage\",", injected);
        Assert.Contains("\"EndRenderStage\",", injected);
    }

    [Fact]
    public void TheAbstractPlatformDeclaresNeutralVirtualsAndOpenGlDoesNotOverrideThem()
    {
        string platform = ReadLib(AbstractPath);
        foreach (string signature in new[]
        {
            "public virtual void BeginRenderStage(EnumRenderStage stage)",
            "public virtual void EndRenderStage(EnumRenderStage stage)",
        })
        {
            Assert.Equal("{ }", Regex.Replace(Body(platform, signature), @"\s+", " ").Trim());
        }

        string windows = VulkanPlatformSource.ReadClientPlatformWindows();
        Assert.DoesNotContain("BeginRenderStage", windows);
        Assert.DoesNotContain("EndRenderStage", windows);
    }

    [Fact]
    public void TheVulkanPlatformOverridesSelfChecksAndForwards()
    {
        string selfCheck = Block(Read(VulkanPlatformSource.MainFile), "internal static readonly ExpectedVirtual[] ExpectedVirtuals", "};");
        Assert.Contains("new(true, \"BeginRenderStage\", new[] { \"EnumRenderStage\" }),", selfCheck);
        Assert.Contains("new(true, \"EndRenderStage\", new[] { \"EnumRenderStage\" }),", selfCheck);

        string stages = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Stages.cs");
        string begin = Body(stages, "public override void BeginRenderStage(EnumRenderStage stage)");
        Assert.Contains("CurrentRenderStage = stage;", begin);
        Assert.Contains("RenderStageListener?.OnBeginRenderStage(stage);", begin);
        Assert.Contains("RenderStageListener?.OnEndRenderStage(stage);",
            Body(stages, "public override void EndRenderStage(EnumRenderStage stage)"));
        Assert.Contains("internal IRenderStageListener? RenderStageListener;", stages);

        string listener = Read("Optimum.Render.Vulkan/Graph/IRenderStageListener.cs");
        Assert.Contains("namespace Optimum.Render.Vulkan.Graph;", listener);
        Assert.Contains("internal interface IRenderStageListener", listener);
        Assert.Contains("void OnBeginRenderStage(EnumRenderStage stage);", listener);
        Assert.Contains("void OnEndRenderStage(EnumRenderStage stage);", listener);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));

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

    private static string Block(string source, string header, string terminator)
    {
        int start = source.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing: " + header);
        int end = source.IndexOf(terminator, start, StringComparison.Ordinal);
        Assert.True(end > start);
        return source.Substring(start, end - start);
    }
}
}
