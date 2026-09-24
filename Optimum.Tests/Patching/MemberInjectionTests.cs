// Source: Optimum.Tests/cecil-injected-field-initializer-tests.cs
namespace Optimum.Tests
{
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// Cecil field injection (Optimum.Patcher/mod-patcher.cs's Members manifests,
/// api-patcher.cs's equivalents) adds a field to an already-compiled type
/// without re-running its constructor, so a C# field initializer on an
/// injected field is silently never executed - the field is null at runtime
/// no matter what the source says. This has caused four real crashes so far:
/// EventManager.singleDelayedCallbackBlockKeys (fixed with a lazy ??= at each
/// use site, see EventManager.cs), MechanicalPowerMod.optimumTickNetworks
/// (same fix; NRE'd every server tick and tripped the DieAboveErrorCount
/// safety shutdown on a real singleplayer load),
/// WeatherSystemClient.optimumWindSpeed/optimumSurfaceWindSpeed (readonly
/// Vec3d fields; NRE'd on the first render frame after joining, every single
/// time - fixed by dropping the fields entirely and using local variables
/// instead, matching the VSEssentials/Systems/Weather/WeatherSystemClient.cs
/// fork source, which never needed injected reference-type fields for this),
/// and AStar.optimumNodePool (readonly PathNode[]; flooded server-main.log
/// with "Exception thrown during pathfinding" on every single pathfind call
/// from every entity, silently caught by the vanilla try/catch around
/// pathfinding so it never crashed outright - just made every mob pathless.
/// Fixed with a lazy ??= at each OptimumRentNode overload, matching the
/// MechanicalPowerMod pattern; a persistent pool can't become a local like
/// the weather fix, it has to survive across FindPathOrEscapePath calls).
/// WeatherSimulationSound.lastSetWindVolumeLeafy/Leafless/lastSetRainVolumeLeafy/Leafless
/// never actually shipped broken - the runtime patch for it didn't exist at
/// all until this class already had four known instances of the pattern, so
/// it was written Cecil-safe from the start (no initializer, relies on the
/// CLR's guaranteed float zero-default instead of the -1f sentinel the
/// source-tree fork uses). Kept as a guard so nobody "fixes" it back to -1f
/// by copying the source-tree version verbatim.
/// TreeGen.vineScratchPos/positionStack (readonly BlockPos / readonly
/// BlockPos[32]) is the sixth, and predates this session's other fixes - it
/// was already shipping broken. NRE'd in growBranch every time worldgen grew
/// a tree branch past depth 0, caught per-chunk by
/// ServerSystemSupplyChunks's pass error handler ("[Worldgen] An error was
/// thrown in pass Vegetation...") so it never crashed the server outright,
/// just silently truncated tree/vine generation. Most visible during world
/// shutdown, when many queued-but-incomplete chunks get force-processed at
/// once ("Incomplete chunks stored and wiped"), flooding server-worldgen.log
/// right as the player quits. Fixed the same way as AStar's node pool: lazy
/// ??=/null-check at each use site (positionStack is indexed by recursion
/// depth as a per-depth scratch-object pool, so it stays a field; vineScratchPos
/// is fully written and consumed within one PlaceBlockEtc call, so reuse
/// across calls is safe).
/// ClientMain.tesselationWorkers (readonly OptimumTesselationWorkerRegistry) is
/// the seventh, and is the same class of bug hitting the *other* patcher
/// entry point: Optimum.Patcher/PatchManifest.cs's membersToInject, consumed by
/// MemberInjector.InjectStaticMembers via ILPatcher.PatchWithInjection - the
/// main VintagestoryLib donor transplant, not the runtime mod-patcher.cs path
/// the six instances above went through, but the same InjectStaticMembers
/// function underneath. Shipped broken from 2026-08-10's first
/// IsTesselationThread fix (`51da961`) until this test: every real call
/// (VSSurvivalMod's MealMeshCache.GetOrCreateMealInContainerMeshRef, which
/// calls `capi.IsTesselationThread(...)` on every meal-container mesh build -
/// VSSurvivalMod ships as a normally-compiled DLL, not Cecil-transplanted, so
/// this call site was live immediately) NRE'd on `tesselationWorkers.Contains`.
/// Fixed the same way as MechanicalPowerMod/AStar: lazy `??=` at the one
/// writer (RegisterTesselationThread), null-safe reads at the other two
/// accessors (IsTesselationThread, GetTesselationWorkerSlot).
/// Guards against an eighth.
/// </summary>
public class CecilInjectedFieldInitializerTests
{
    [Fact]
    public void MechanicalPowerModTickNetworksIsLazilyInitialized()
    {
        string patch = Read("patches/runtime/VSSurvivalMod/Vintagestory/GameContent/Mechanics/MechanicalPowerMod.cs.patch");

        Assert.DoesNotMatch(
            new Regex(@"optimumTickNetworks\s*=\s*new List<MechanicalNetwork>\(\);"),
            patch);
        Assert.Contains("optimumTickNetworks ??= new List<MechanicalNetwork>();", patch);
    }

    [Fact]
    public void WeatherSystemClientDoesNotInjectUninitializedReferenceFields()
    {
        string patch = Read("patches/runtime/VSEssentials/Vintagestory/GameContent/WeatherSystemClient.cs.patch");

        // optimumWindSpeed/optimumSurfaceWindSpeed used to be readonly Vec3d
        // fields with a `= new Vec3d()` initializer that Cecil field
        // injection never runs, so they were null on every first use. Assert
        // they don't come back as fields at all - the safe fix here is local
        // variables (Vec3d is only touched inside OnRenderFrame, which is
        // itself transplanted whole, so locals work fine).
        Assert.DoesNotContain("optimumWindSpeed", patch);
        Assert.DoesNotContain("optimumSurfaceWindSpeed", patch);

        // optimumWindFrameCounter is an int (default-zeroed by the CLR, no
        // constructor call needed), so it's safe as an injected field.
        Assert.Contains("private int optimumWindFrameCounter;", patch);
    }

    [Fact]
    public void AStarNodePoolIsLazilyInitialized()
    {
        string patch = Read("patches/runtime/VSEssentials/Vintagestory/Essentials/AStar.cs.patch");

        Assert.DoesNotMatch(
            new Regex(@"readonly PathNode\[\]\s*optimumNodePool\s*=\s*new PathNode\[4096\];"),
            patch);
        Assert.Contains("optimumNodePool ??= new PathNode[4096];", patch);
    }

    [Fact]
    public void WeatherSimulationSoundVolumeDeadzoneFieldsHaveNoInitializer()
    {
        // The source-tree fork (patches/VSEssentials/Systems/Weather/WeatherSimulationSound.cs.patch)
        // sentinels these at -1f so the very first SetVolume call always
        // fires. That initializer is float literal IL in the constructor,
        // not metadata - safe there because that class is a normally
        // compiled C# type. The runtime patch injects these same fields via
        // Cecil member injection instead, which never runs the constructor,
        // so any non-default initializer would silently vanish. Defaulting
        // to 0f (the CLR's guaranteed zero-init, no initializer needed) is
        // functionally equivalent here: it only skips the very first
        // SetVolume call if the true starting volume happens to land within
        // the 0.01 deadzone of zero, which is inaudible anyway.
        string patch = Read("patches/runtime/VSEssentials/Vintagestory/GameContent/WeatherSimulationSound.cs.patch");

        Assert.DoesNotMatch(
            new Regex(@"lastSet(Wind|Rain)Volume(Leafy|Leafless)\s*=\s*-1f;"),
            patch);
        Assert.Contains("private float lastSetWindVolumeLeafy;", patch);
        Assert.Contains("private float lastSetWindVolumeLeafless;", patch);
        Assert.Contains("private float lastSetRainVolumeLeafy;", patch);
        Assert.Contains("private float lastSetRainVolumeLeafless;", patch);
    }

    [Fact]
    public void TreeGenScratchFieldsHaveNoInitializer()
    {
        string patch = Read("patches/runtime/VSEssentials/Vintagestory/ServerMods/TreeGen.cs.patch");

        Assert.DoesNotMatch(
            new Regex(@"readonly BlockPos\s*vineScratchPos\s*=\s*new BlockPos\(0\);"),
            patch);
        Assert.DoesNotMatch(
            new Regex(@"readonly BlockPos\[\]\s*positionStack\s*=\s*new BlockPos\[32\];"),
            patch);
        Assert.Contains("positionStack ??= new BlockPos[32];", patch);
        Assert.Contains("vineScratchPos ??= new BlockPos(0);", patch);
    }

    [Fact]
    public void ClientMainTesselationWorkersIsLazilyInitialized()
    {
        string patch = Read("patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs.patch");

        Assert.DoesNotMatch(
            new Regex(@"readonly OptimumTesselationWorkerRegistry\s*tesselationWorkers\s*=\s*new OptimumTesselationWorkerRegistry\(\);"),
            patch);
        Assert.Contains("(tesselationWorkers ??= new OptimumTesselationWorkerRegistry()).Register(threadId);", patch);

        // IsTesselationThread/GetTesselationWorkerSlot both read the field before
        // it's necessarily been created (any thread can ask before the tesselation
        // worker thread has registered itself) - both must null-check.
        Assert.Contains("workers != null && workers.Contains(threadId);", patch);
        Assert.Contains("workers != null ? workers.GetSlot(threadId) : 0;", patch);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
}

// Source: Optimum.Tests/cecil-transplant-lambda-tests.cs
namespace Optimum.Tests
{
using System.IO;
using System.Linq;
using Xunit;

/// <summary>
/// A method transplanted whole via Cecil member injection (mod-patcher.cs's
/// Methods list, Program.cs's targets) that contains a lambda compiled into
/// a *cached static delegate* (a compiler-generated `<>c` class, typical of
/// LINQ calls like `.All(x => ...)`) references that class's method by
/// MethodReference - but injection only clones the named method, not its
/// dependent nested closure type. SelfConsistencyVerifier correctly detects
/// this and refuses to write output ("N self-reference error(s), output not
/// written"), but ModPatcher.Patch then throws on total &lt;= 0, which is an
/// *unhandled* exception in Optimum.exe - it kills the launcher mid-sequence,
/// so every DLL not yet patched that run (VintagestoryLib, VintagestoryAPI in
/// this incident) stays fully vanilla with zero warning to the user beyond
/// "Game Version: v1.22.7 (Stable)" missing its "+ Optimum" suffix.
///
/// WeatherSimulationSound::updateSounds hit this from
/// `rainSoundsLeafless.All(s => s.IsReady)` / `rainSoundsLeafy.All(...)`,
/// added when the volume-deadzone patch first got a patches/runtime
/// counterpart. Fixed by replacing both with explicit loops (docs/il-patcher-plan.md
/// documents this exact constraint: "methods [containing lambdas] cannot be
/// transplanted" - this was a known rule, just not checked before adding this
/// particular method to the Methods list).
///
/// Instance-capturing lambdas (e.g. the TyronThreadPool.QueueTask(() => ...)
/// a few lines below in the same method) are not necessarily unsafe - that
/// one transplants fine, verified by actually running Optimum.Patcher --mod
/// against the real vanilla DLL, not just by building the donor project.
/// The .All(...) pattern specifically (LINQ predicate cached in a shared
/// `<>c` class) is the one confirmed to break.
/// </summary>
public class CecilTransplantLambdaTests
{
    /// <summary>
    /// ClientMain::Start retains vanilla's `rand = new ThreadLocal&lt;Random&gt;(() =&gt;
    /// new Random(Environment.TickCount));` - a lambda capturing nothing, so the
    /// compiler caches it as a static delegate field on ClientMain/&lt;&gt;c
    /// (&lt;&gt;9__&lt;memberOrdinal&gt;_0). That ordinal is the member's declaration
    /// index within the type, which differs between the vanilla assembly and this
    /// decompiled-and-rebuilt donor (confirmed empirically: donor ordinal 345,
    /// vanilla's slot 345 holds an unrelated Func&lt;ClientPlayer,ClientPlayer&gt; from
    /// a different property), so InjectMissingFieldsForMethod throws a field
    /// signature mismatch the moment Start is added as a transplant target - this
    /// was the blocker preventing Start (and therefore the tesselation worker
    /// registration and pool wiring it does) from shipping at all. Fixed by
    /// replacing the lambda with an instance method group (CreateRandom) -
    /// verified via `make patch-il` with Start temporarily added to targets
    /// (PATCHED cleanly, 0 verifier errors) and an ilspycmd IL dump confirming
    /// ClientMain/&lt;&gt;c no longer has a &lt;Start&gt;b__ member.
    /// </summary>
    [Fact]
    public void ClientMainStartHasNoThreadLocalRandomLambda()
    {
        string patch = Read("patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs.patch");

        string addedLines = string.Join('\n', patch
            .Split('\n')
            .Where(line => line.StartsWith('+') && !line.StartsWith("+++")));

        Assert.DoesNotContain("() => new Random(Environment.TickCount)", addedLines);
        Assert.Contains("rand = new ThreadLocal<Random>(CreateRandom);", addedLines);
        Assert.Contains("private Random CreateRandom()", addedLines);
    }

    [Fact]
    public void WeatherSimulationSoundUpdateSoundsHasNoAllPredicateLambda()
    {
        string patch = Read("patches/runtime/VSEssentials/Vintagestory/GameContent/WeatherSimulationSound.cs.patch");

        // Only the added (+) lines matter here - a unified diff's context and
        // removed (-) lines legitimately still show vanilla's original
        // .All(s => s.IsReady) text being replaced.
        string addedLines = string.Join('\n', patch
            .Split('\n')
            .Where(line => line.StartsWith('+') && !line.StartsWith("+++")));

        Assert.DoesNotContain(".All(", addedLines);
        Assert.DoesNotContain("=> s.IsReady", addedLines);
    }

    /// <summary>
    /// Worker-pool wiring plan's Option B decision gate: TerrainChunkTesselator must
    /// stay a real, assignable field, not become a Cecil-injected property returning
    /// ChunkTesselatorManager.PrimaryTesselator. A property would require transplanting
    /// two collateral vanilla readers (BlockTextureAtlasManager::RuntimeCreateNewAtlas,
    /// ClientSystemStartup::HandleWorldMetaData) and leave a permanently-null public
    /// field on the property-converted metadata for any mod compiled against vanilla
    /// VintagestoryLib. See docs/implementation-plans/chunk-tesselator-worker-pool-wiring-plan-2026-08-10.md.
    /// </summary>
    [Fact]
    public void ClientMainTerrainChunkTesselatorStaysAField()
    {
        string patch = Read("patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs.patch");

        string addedLines = string.Join('\n', patch
            .Split('\n')
            .Where(line => line.StartsWith('+') && !line.StartsWith("+++")));

        Assert.Contains("TerrainChunkTesselator = terrainChunkTesselatorManager.PrimaryTesselator;", addedLines);
        Assert.DoesNotContain("TerrainChunkTesselator =>", addedLines);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
}

// Source: Optimum.Tests/member-injector-tests.cs
namespace Optimum.Tests
{
using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Xunit;

public sealed class MemberInjectorTests
{
    [Fact]
    public void InjectedMethodMapsArgumentsAndLocalsIntoItsOwnBody()
    {
        using AssemblyDefinition vanilla = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("VintagestoryLib", new Version(1, 0)),
            "VintagestoryLib", ModuleKind.Dll);
        using AssemblyDefinition compiled = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("VintagestoryLib", new Version(1, 0)),
            "VintagestoryLib", ModuleKind.Dll);

        TypeDefinition vanillaType = new("Fixture", "MethodHost",
            TypeAttributes.Public | TypeAttributes.Class, vanilla.MainModule.TypeSystem.Object);
        TypeDefinition sourceType = new("Fixture", "MethodHost",
            TypeAttributes.Public | TypeAttributes.Class, compiled.MainModule.TypeSystem.Object);
        vanilla.MainModule.Types.Add(vanillaType);
        compiled.MainModule.Types.Add(sourceType);

        MethodDefinition source = new("Identity", MethodAttributes.Public | MethodAttributes.Static,
            compiled.MainModule.TypeSystem.Int32);
        source.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None,
            compiled.MainModule.TypeSystem.Int32));
        var local = new VariableDefinition(compiled.MainModule.TypeSystem.Int32);
        source.Body.Variables.Add(local);
        source.Body.InitLocals = true;
        var il = source.Body.GetILProcessor();
        il.Append(Instruction.Create(OpCodes.Ldarg, source.Parameters[0]));
        il.Append(Instruction.Create(OpCodes.Stloc, local));
        il.Append(Instruction.Create(OpCodes.Ldloc, local));
        il.Append(Instruction.Create(OpCodes.Ret));
        sourceType.Methods.Add(source);

        MemberInjector.InjectStaticMembers(vanilla, compiled, sourceType.FullName,
            new List<string> { source.Name });

        MethodDefinition injected = Assert.Single(vanillaType.Methods);
        Assert.Same(injected.Parameters[0], injected.Body.Instructions[0].Operand);
        Assert.Same(injected.Body.Variables[0], injected.Body.Instructions[1].Operand);
        Assert.Same(injected.Body.Variables[0], injected.Body.Instructions[2].Operand);
        using var output = new System.IO.MemoryStream();
        vanilla.Write(output);
        Assert.True(output.Length > 0);
    }

    [Fact]
    public void InjectedPInvokeKeepsItsImportMap()
    {
        using AssemblyDefinition vanilla = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("vanilla", new Version(1, 0)), "vanilla", ModuleKind.Dll);
        using AssemblyDefinition compiled = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("compiled", new Version(1, 0)), "compiled", ModuleKind.Dll);
        TypeDefinition targetType = new("Fixture", "Native",
            TypeAttributes.Public | TypeAttributes.Class, vanilla.MainModule.TypeSystem.Object);
        TypeDefinition sourceType = new("Fixture", "Native",
            TypeAttributes.Public | TypeAttributes.Class, compiled.MainModule.TypeSystem.Object);
        vanilla.MainModule.Types.Add(targetType);
        compiled.MainModule.Types.Add(sourceType);
        var nativeModule = new ModuleReference("fixture.dll");
        compiled.MainModule.ModuleReferences.Add(nativeModule);
        var source = new MethodDefinition("NativeCall",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.PInvokeImpl,
            compiled.MainModule.TypeSystem.Void)
        {
            PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvCdecl,
                "native_call", nativeModule),
        };
        sourceType.Methods.Add(source);

        MemberInjector.InjectStaticMembers(vanilla, compiled, sourceType.FullName,
            new List<string> { source.Name });

        MethodDefinition injected = Assert.Single(targetType.Methods);
        Assert.Equal("native_call", injected.PInvokeInfo.EntryPoint);
        Assert.Equal("fixture.dll", injected.PInvokeInfo.Module.Name);
        Assert.Same(vanilla.MainModule.ModuleReferences.Single(), injected.PInvokeInfo.Module);
        using var output = new System.IO.MemoryStream();
        vanilla.Write(output);
        Assert.True(output.Length > 0);
    }

    [Fact]
    public void SameArityOverloadsRequireTheirParameterTypesToMatch()
    {
        using AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("overloads", new Version(1, 0)),
            "overloads",
            ModuleKind.Dll);
        TypeDefinition type = new(
            "Fixture.Overloads",
            "Overloads",
            TypeAttributes.Public | TypeAttributes.Class,
            assembly.MainModule.TypeSystem.Object);
        assembly.MainModule.Types.Add(type);

        MethodDefinition pathNodeEquals = new(
            "Equals",
            MethodAttributes.Public,
            assembly.MainModule.TypeSystem.Boolean);
        pathNodeEquals.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, type));
        type.Methods.Add(pathNodeEquals);

        MethodDefinition objectEquals = new(
            "Equals",
            MethodAttributes.Public,
            assembly.MainModule.TypeSystem.Boolean);
        objectEquals.Parameters.Add(new ParameterDefinition(
            "value",
            ParameterAttributes.None,
            assembly.MainModule.TypeSystem.Object));
        type.Methods.Add(objectEquals);

        Assert.True(MethodSignature.Matches(pathNodeEquals, pathNodeEquals));
        Assert.False(MethodSignature.Matches(pathNodeEquals, objectEquals));

        var sameSignatureOnAnotherType = new MethodReference(
            pathNodeEquals.Name,
            pathNodeEquals.ReturnType,
            pathNodeEquals.Module.TypeSystem.Object)
        {
            HasThis = pathNodeEquals.HasThis,
            ExplicitThis = pathNodeEquals.ExplicitThis,
            CallingConvention = pathNodeEquals.CallingConvention
        };
        foreach (var parameter in pathNodeEquals.Parameters)
        {
            sameSignatureOnAnotherType.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
        }
        Assert.False(MethodSignature.Matches(pathNodeEquals, sameSignatureOnAnotherType));

        Assert.Throws<InvalidOperationException>(() => MethodSignature.FindUnique(type, "Equals", 1));
        Assert.True(MethodSignature.Matches(
            pathNodeEquals,
            pathNodeEquals.DeclaringType.FullName,
            pathNodeEquals.Name,
            [pathNodeEquals.Parameters[0].ParameterType.FullName],
            pathNodeEquals.ReturnType.FullName,
            pathNodeEquals.HasThis,
            pathNodeEquals.ExplicitThis,
            pathNodeEquals.CallingConvention,
            pathNodeEquals.GenericParameters.Count));
        Assert.False(MethodSignature.Matches(
            pathNodeEquals,
            pathNodeEquals.DeclaringType.FullName,
            pathNodeEquals.Name,
            [pathNodeEquals.Parameters[0].ParameterType.FullName],
            "System.Object",
            pathNodeEquals.HasThis,
            pathNodeEquals.ExplicitThis,
            pathNodeEquals.CallingConvention,
            pathNodeEquals.GenericParameters.Count));
    }

    [Fact]
    public void MissingRequiredTypeFailsThePatch()
    {
        using AssemblyDefinition vanilla = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("vanilla", new Version(1, 0)), "vanilla", ModuleKind.Dll);
        using AssemblyDefinition compiled = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("compiled", new Version(1, 0)), "compiled", ModuleKind.Dll);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => MemberInjector.InjectTypes(vanilla, compiled, new List<string> { "Optimum.RequiredType" }));

        Assert.Contains("Optimum.RequiredType", exception.Message);
    }

    [Fact]
    public void InjectedHelperDependenciesBringTheirFieldsIntoTheTargetAssembly()
    {
        using AssemblyDefinition vanilla = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("VintagestoryLib", new Version(1, 0)),
            "VintagestoryLib",
            ModuleKind.Dll);
        using AssemblyDefinition compiled = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("VintagestoryLib", new Version(1, 0)),
            "VintagestoryLib",
            ModuleKind.Dll);

        TypeDefinition vanillaType = AddGearRenderer(vanilla.MainModule, includeOptimumMembers: false);
        TypeDefinition compiledType = AddGearRenderer(compiled.MainModule, includeOptimumMembers: true);

        int injected = MemberInjector.InjectStaticMembers(
            vanilla,
            compiled,
            "Vintagestory.GameContent.GearRenderer",
            new List<string> { "DisableOptimumGearRenderer" });

        Assert.Equal(3, injected);
        Assert.Contains(vanillaType.Fields, field => field.Name == "optimumGearRendererDisabled");
        Assert.Contains(vanillaType.Fields, field => field.Name == "optimumGearRendererFailureLogged");
        Assert.Contains(vanillaType.Methods, method => method.Name == "DisableOptimumGearRenderer");
        Assert.Empty(SelfConsistencyVerifier.VerifySelfReferences(vanilla.MainModule));
    }

    // Server worldgen/chunk-pool wiring plan, Gap A: field injection used to carry
    // over only FieldAttributes (visibility/static flags), silently dropping real
    // .NET attributes like [ThreadStatic] - turning a per-thread slot into one
    // shared-and-racing static with no error at patch time or at runtime.
    [Fact]
    public void InjectedFieldsPreserveThreadStaticAttribute()
    {
        using AssemblyDefinition vanilla = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("VintagestoryLib", new Version(1, 0)),
            "VintagestoryLib",
            ModuleKind.Dll);
        using AssemblyDefinition compiled = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("VintagestoryLib", new Version(1, 0)),
            "VintagestoryLib",
            ModuleKind.Dll);

        TypeDefinition vanillaType = new(
            "Vintagestory.Server", "ServerSystemSupplyChunks",
            TypeAttributes.Public | TypeAttributes.Class, vanilla.MainModule.TypeSystem.Object);
        vanilla.MainModule.Types.Add(vanillaType);

        TypeDefinition compiledType = new(
            "Vintagestory.Server", "ServerSystemSupplyChunks",
            TypeAttributes.Public | TypeAttributes.Class, compiled.MainModule.TypeSystem.Object);
        compiled.MainModule.Types.Add(compiledType);

        FieldDefinition srcField = new(
            "optimumWorkerIndex",
            FieldAttributes.Private | FieldAttributes.Static,
            compiled.MainModule.TypeSystem.Int32);
        MethodReference threadStaticCtor = compiled.MainModule.ImportReference(
            typeof(ThreadStaticAttribute).GetConstructor(Type.EmptyTypes));
        srcField.CustomAttributes.Add(new CustomAttribute(threadStaticCtor));
        compiledType.Fields.Add(srcField);

        MemberInjector.InjectStaticMembers(
            vanilla, compiled, "Vintagestory.Server.ServerSystemSupplyChunks",
            new List<string> { "optimumWorkerIndex" });

        FieldDefinition injected = Assert.Single(vanillaType.Fields, f => f.Name == "optimumWorkerIndex");
        CustomAttribute attr = Assert.Single(injected.CustomAttributes);
        Assert.Equal("System.ThreadStaticAttribute", attr.Constructor.DeclaringType.FullName);
    }

    private static TypeDefinition AddGearRenderer(ModuleDefinition module, bool includeOptimumMembers)
    {
        TypeDefinition type = new(
            "Vintagestory.GameContent",
            "GearRenderer",
            TypeAttributes.Public | TypeAttributes.Class,
            module.TypeSystem.Object);
        module.Types.Add(type);

        if (!includeOptimumMembers)
            return type;

        FieldDefinition disabled = new(
            "optimumGearRendererDisabled",
            FieldAttributes.Private,
            module.TypeSystem.Boolean);
        FieldDefinition failureLogged = new(
            "optimumGearRendererFailureLogged",
            FieldAttributes.Private,
            module.TypeSystem.Boolean);
        type.Fields.Add(disabled);
        type.Fields.Add(failureLogged);

        MethodDefinition helper = new(
            "DisableOptimumGearRenderer",
            MethodAttributes.Private | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        type.Methods.Add(helper);

        ILProcessor il = helper.Body.GetILProcessor();
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Stfld, disabled));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Stfld, failureLogged));
        il.Append(Instruction.Create(OpCodes.Ret));

        return type;
    }
}
}
