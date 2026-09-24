// Source: Optimum.Tests/platform-substitution-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using Xunit;

/// <summary>
/// Vulkan-native plan, Phase 0: VulkanClientPlatform subclasses ClientPlatformWindows, so the
/// patcher must unseal the class and virtualize the members it overrides, and the donor the
/// renderer compiles against must declare the same shape. A missing entry on either side
/// only shows up at runtime (TypeLoadException, or an override that is silently bypassed).
/// </summary>
public class PlatformSubstitutionCoverageTests
{
    private const string PlatformType = "\"Vintagestory.Client.NoObf.ClientPlatformWindows\"";

    private static readonly (string Name, int ParamCount, string Declaration)[] VirtualizedMembers =
    {
        ("SetupDefaultFrameBuffers", 0, "public virtual List<FrameBufferRef> SetupDefaultFrameBuffers()"),
        ("DisposeFrameBuffers", 1, "public virtual void DisposeFrameBuffers(List<FrameBufferRef> buffers)"),
        ("RenderFullscreenTriangle", 1, "public virtual void RenderFullscreenTriangle(MeshRef modelRef)"),
        ("GetGraphicsCardRenderer", 0, "public virtual string GetGraphicsCardRenderer()"),
    };

    [Fact]
    public void PatcherUnsealsThePlatformClass()
    {
        string unseal = ListBody(PatcherSource.Read(), "var typesToUnseal = new List<string>");

        Assert.Contains(PlatformType + ",", unseal);
    }

    [Fact]
    public void PatcherVirtualizesEveryOverriddenPlatformMember()
    {
        string patcher = PatcherSource.Read();
        string virtualize = ListBody(patcher, "var methodsToVirtualize = new List<MethodTarget>");

        foreach (var member in VirtualizedMembers)
            Assert.Contains($"new({PlatformType}, \"{member.Name}\", {member.ParamCount})", virtualize);

        Assert.Contains("typesToUnseal: manifest.TypesToUnseal", patcher);
        Assert.Contains("methodsToVirtualize: manifest.MethodsToVirtualize", patcher);
    }

    [Fact]
    public void PatcherVerifiesVirtualDispatchBeforeWritingTheOutput()
    {
        string ilPatcher = Read("Optimum.Patcher/ILPatcher.cs");

        int transplant = ilPatcher.IndexOf("TransplantBody(vanillaMethod, compiledMethod", StringComparison.Ordinal);
        int hooks = ilPatcher.IndexOf("// Phase 3: IL hooks", StringComparison.Ordinal);
        int virtualize = ilPatcher.IndexOf("PlatformSubstitution.VirtualizeMethods(", StringComparison.Ordinal);
        int verify = ilPatcher.IndexOf("PlatformSubstitution.VerifyVirtualDispatch(", StringComparison.Ordinal);
        int write = ilPatcher.IndexOf("AssemblyWriter.Write(vanillaAsm", StringComparison.Ordinal);

        Assert.True(transplant >= 0 && hooks > transplant);
        Assert.True(virtualize > hooks, "flags must be applied after every transplant and hook");
        Assert.True(verify > virtualize && write > verify, "the dispatch verifier must run before the write");
    }

    [Fact]
    public void DonorDeclaresTheSubclassableShape()
    {
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs", optional: true)
            ?? PatchReader.ReadPatchedContent(PatchReader.FindRepositoryFile(
                "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch"));

        Assert.Contains("public class ClientPlatformWindows : ClientPlatformAbstract", platform);
        Assert.DoesNotContain("sealed class ClientPlatformWindows", platform);
        foreach (var member in VirtualizedMembers)
            Assert.Contains(member.Declaration, platform);
    }

    private static string ListBody(string source, string declaration)
    {
        int start = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"missing declaration: {declaration}");
        int end = source.IndexOf("};", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return source.Substring(start, end - start);
    }

    private static string? Read(string relativePath, bool optional = false)
    {
        try
        {
            return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
        }
        catch (FileNotFoundException) when (optional)
        {
            return null;
        }
    }

    private static string Read(string relativePath) => Read(relativePath, optional: false)!;
}
}

// Source: Optimum.Tests/platform-substitution-patcher-tests.cs
namespace Optimum.Tests
{
using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Xunit;

/// <summary>
/// Synthetic-module tests for the patcher's platform substitution step (typesToUnseal,
/// methodsToVirtualize and the call-versus-callvirt dispatch verifier). Same fixture pattern
/// as member-injector-tests.cs: in-memory Cecil modules named VintagestoryLib.
/// </summary>
public sealed class PlatformSubstitutionPatcherTests
{
    private const string PlatformName = "Vintagestory.Client.NoObf.ClientPlatformWindows";
    private const string CallerName = "Vintagestory.Client.ClientProgram";

    private static readonly List<MethodTarget> Virtualize = new()
    {
        new(PlatformName, "SetupDefaultFrameBuffers", 0),
    };

    [Fact]
    public void UnsealClearsOnlySealed()
    {
        using AssemblyDefinition assembly = CreateModule();
        TypeDefinition platform = AddPlatform(assembly.MainModule, MethodAttributes.Public | MethodAttributes.HideBySig);
        TypeAttributes before = platform.Attributes;
        Assert.True(platform.IsSealed);

        int unsealed = PlatformSubstitution.UnsealTypes(assembly.MainModule, new[] { PlatformName });

        Assert.Equal(1, unsealed);
        Assert.False(platform.IsSealed);
        Assert.Equal(before & ~TypeAttributes.Sealed, platform.Attributes);
    }

    [Fact]
    public void VirtualizeSetsVirtualNewSlotHideBySigAndKeepsVisibility()
    {
        using AssemblyDefinition assembly = CreateModule();
        TypeDefinition platform = AddPlatform(assembly.MainModule, MethodAttributes.Public);
        MethodDefinition protectedMethod = AddVoidMethod(platform, "RenderFullscreenTriangle", MethodAttributes.Family);

        PlatformSubstitution.VirtualizeMethods(assembly.MainModule, new List<MethodTarget>
        {
            new(PlatformName, "SetupDefaultFrameBuffers", 0),
            new(PlatformName, "RenderFullscreenTriangle", 0),
        });

        MethodDefinition setup = Find(platform, "SetupDefaultFrameBuffers");
        Assert.Equal(
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
            setup.Attributes);
        Assert.True(setup.IsPublic);
        Assert.Equal(
            MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
            protectedMethod.Attributes);
    }

    [Fact]
    public void PrivateEntryFailsThePatch()
    {
        using AssemblyDefinition assembly = CreateModule();
        AddPlatform(assembly.MainModule, MethodAttributes.Private | MethodAttributes.HideBySig);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => PlatformSubstitution.VirtualizeMethods(assembly.MainModule, Virtualize));

        Assert.Contains("private", error.Message);
        Assert.Contains("SetupDefaultFrameBuffers", error.Message);
    }

    [Fact]
    public void CallToVirtualizedMethodFailsTheVerifierNamingTheCaller()
    {
        using AssemblyDefinition assembly = CreateModule();
        TypeDefinition platform = AddPlatform(assembly.MainModule, MethodAttributes.Public | MethodAttributes.HideBySig);
        AddCaller(assembly.MainModule, platform, OpCodes.Call);
        Apply(assembly.MainModule);

        List<string> errors = Verify(assembly.MainModule, out int virtualSites);

        string error = Assert.Single(errors);
        Assert.Contains(CallerName + "::Start", error);
        Assert.Contains("SetupDefaultFrameBuffers", error);
        Assert.Contains(" call ", error);
        Assert.Equal(0, virtualSites);
    }

    [Fact]
    public void CallvirtToVirtualizedMethodPassesTheVerifier()
    {
        using AssemblyDefinition assembly = CreateModule();
        TypeDefinition platform = AddPlatform(assembly.MainModule, MethodAttributes.Public | MethodAttributes.HideBySig);
        AddCaller(assembly.MainModule, platform, OpCodes.Callvirt);
        Apply(assembly.MainModule);

        List<string> errors = Verify(assembly.MainModule, out int virtualSites);

        Assert.Empty(errors);
        Assert.Equal(1, virtualSites);
    }

    [Fact]
    public void LdftnInANestedTypeFailsTheVerifier()
    {
        using AssemblyDefinition assembly = CreateModule();
        TypeDefinition platform = AddPlatform(assembly.MainModule, MethodAttributes.Public | MethodAttributes.HideBySig);
        TypeDefinition caller = AddCaller(assembly.MainModule, platform, OpCodes.Callvirt);
        TypeDefinition closure = new("", "<>c", TypeAttributes.NestedPrivate | TypeAttributes.Class, assembly.MainModule.TypeSystem.Object);
        caller.NestedTypes.Add(closure);
        MethodDefinition lambda = new("<Start>b__0", MethodAttributes.Assembly | MethodAttributes.HideBySig, assembly.MainModule.TypeSystem.Void);
        closure.Methods.Add(lambda);
        ILProcessor il = lambda.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldftn, Find(platform, "SetupDefaultFrameBuffers")));
        il.Append(il.Create(OpCodes.Pop));
        il.Append(il.Create(OpCodes.Ret));
        Apply(assembly.MainModule);

        List<string> errors = Verify(assembly.MainModule, out _);

        string error = Assert.Single(errors);
        Assert.Contains(CallerName + "/<>c::<Start>b__0", error);
        Assert.Contains("ldftn", error);
    }

    [Fact]
    public void BaseCallFromASubclassPassesTheVerifier()
    {
        using AssemblyDefinition assembly = CreateModule();
        TypeDefinition platform = AddPlatform(assembly.MainModule, MethodAttributes.Public | MethodAttributes.HideBySig);
        TypeDefinition derived = new("Fixture", "DerivedPlatform", TypeAttributes.Public | TypeAttributes.Class, platform);
        assembly.MainModule.Types.Add(derived);
        MethodDefinition overrideMethod = new(
            "SetupDefaultFrameBuffers",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            assembly.MainModule.TypeSystem.Void);
        derived.Methods.Add(overrideMethod);
        ILProcessor il = overrideMethod.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, Find(platform, "SetupDefaultFrameBuffers")));
        il.Append(il.Create(OpCodes.Ret));
        Apply(assembly.MainModule);

        Assert.Empty(Verify(assembly.MainModule, out _));
    }

    [Fact]
    public void VerifierFailsWhenTheFlagsWereNotApplied()
    {
        using AssemblyDefinition assembly = CreateModule();
        AddPlatform(assembly.MainModule, MethodAttributes.Public | MethodAttributes.HideBySig);

        List<string> errors = Verify(assembly.MainModule, out _);

        Assert.Contains(errors, error => error.Contains("still carries TypeAttributes.Sealed"));
        Assert.Contains(errors, error => error.Contains("is not virtual"));
    }

    [Fact]
    public void PatchWithInjectionKeepsFlagsOnTransplantedMethodsAndRefusesAStrayCall()
    {
        string directory = Path.Combine(Path.GetTempPath(), "optimum-platform-substitution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "vanilla"));
        Directory.CreateDirectory(Path.Combine(directory, "compiled"));
        try
        {
            string compiledPath = Path.Combine(directory, "compiled", "VintagestoryLib.dll");
            using (AssemblyDefinition compiled = CreateModule())
            {
                TypeDefinition platform = AddPlatform(
                    compiled.MainModule,
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
                    sealedType: false);
                AddCaller(compiled.MainModule, platform, OpCodes.Callvirt);
                compiled.Write(compiledPath);
            }

            var targets = new List<MethodTarget>
            {
                new(PlatformName, "SetupDefaultFrameBuffers", 0),
                new(CallerName, "Start", 0),
            };

            // 1. Both the virtualized method and its caller are transplanted: flags survive, output written.
            string vanillaPath = WriteVanilla(directory, "vanilla-ok.dll", OpCodes.Call);
            string outputPath = Path.Combine(directory, "patched-ok.dll");
            int result = ILPatcher.PatchWithInjection(
                vanillaPath, compiledPath, outputPath, new List<string>(), new Dictionary<string, List<string>>(), targets,
                typesToUnseal: new List<string> { PlatformName }, methodsToVirtualize: Virtualize);

            Assert.True(result > 0);
            using (AssemblyDefinition patched = AssemblyDefinition.ReadAssembly(outputPath))
            {
                TypeDefinition platform = patched.MainModule.GetType(PlatformName);
                Assert.False(platform.IsSealed);
                MethodDefinition setup = Find(platform, "SetupDefaultFrameBuffers");
                Assert.True(setup.IsVirtual && setup.IsNewSlot && setup.IsHideBySig && setup.IsPublic);
                Assert.Contains(
                    Find(patched.MainModule.GetType(CallerName), "Start").Body.Instructions,
                    instruction => instruction.OpCode == OpCodes.Callvirt);
            }

            // 2. The caller is not transplanted and keeps its vanilla `call`: the patch is refused.
            string strayPath = WriteVanilla(directory, "vanilla-stray.dll", OpCodes.Call);
            string strayOutput = Path.Combine(directory, "patched-stray.dll");
            int refused = ILPatcher.PatchWithInjection(
                strayPath, compiledPath, strayOutput, new List<string>(), new Dictionary<string, List<string>>(),
                new List<MethodTarget> { targets[0] },
                typesToUnseal: new List<string> { PlatformName }, methodsToVirtualize: Virtualize);

            Assert.Equal(-1, refused);
            Assert.False(File.Exists(strayOutput));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string WriteVanilla(string directory, string fileName, OpCode callOpCode)
    {
        string path = Path.Combine(directory, "vanilla", fileName);
        using AssemblyDefinition vanilla = CreateModule();
        TypeDefinition platform = AddPlatform(vanilla.MainModule, MethodAttributes.Public | MethodAttributes.HideBySig);
        AddCaller(vanilla.MainModule, platform, callOpCode);
        vanilla.Write(path);
        return path;
    }

    private static void Apply(ModuleDefinition module)
    {
        PlatformSubstitution.UnsealTypes(module, new[] { PlatformName });
        PlatformSubstitution.VirtualizeMethods(module, Virtualize);
    }

    private static List<string> Verify(ModuleDefinition module, out int virtualSites) =>
        PlatformSubstitution.VerifyVirtualDispatch(module, new[] { PlatformName }, Virtualize, out virtualSites);

    private static AssemblyDefinition CreateModule() =>
        AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("VintagestoryLib", new Version(1, 0)),
            "VintagestoryLib",
            ModuleKind.Dll);

    private static TypeDefinition AddPlatform(ModuleDefinition module, MethodAttributes setupAttributes, bool sealedType = true)
    {
        TypeAttributes attributes = TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.BeforeFieldInit;
        if (sealedType) attributes |= TypeAttributes.Sealed;
        TypeDefinition platform = new("Vintagestory.Client.NoObf", "ClientPlatformWindows", attributes, module.TypeSystem.Object);
        module.Types.Add(platform);
        AddVoidMethod(platform, "SetupDefaultFrameBuffers", setupAttributes);
        return platform;
    }

    private static MethodDefinition AddVoidMethod(TypeDefinition type, string name, MethodAttributes attributes)
    {
        MethodDefinition method = new(name, attributes, type.Module.TypeSystem.Void);
        type.Methods.Add(method);
        method.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        return method;
    }

    private static TypeDefinition AddCaller(ModuleDefinition module, TypeDefinition platform, OpCode callOpCode)
    {
        TypeDefinition caller = new("Vintagestory.Client", "ClientProgram", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(caller);
        MethodDefinition start = new("Start", MethodAttributes.Public | MethodAttributes.HideBySig, module.TypeSystem.Void);
        start.Parameters.Clear();
        caller.Methods.Add(start);
        start.Body.Variables.Add(new VariableDefinition(platform));
        ILProcessor il = start.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldloc_0));
        il.Append(il.Create(callOpCode, Find(platform, "SetupDefaultFrameBuffers")));
        il.Append(il.Create(OpCodes.Ret));
        return caller;
    }

    private static MethodDefinition Find(TypeDefinition type, string name)
    {
        foreach (MethodDefinition method in type.Methods)
        {
            if (method.Name == name) return method;
        }
        throw new InvalidOperationException($"{type.FullName}::{name} not found");
    }
}
}
