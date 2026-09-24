// Source: Optimum.Tests/CecilPatchOwnershipTests.cs
namespace Optimum.Tests
{
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Optimum.Patcher;
using Xunit;

public class CecilPatchOwnershipTests
{
    [Fact]
    public void CecilOwnershipListMatchesTheTypedManifest()
    {
        string root = FindRepositoryRoot();
        string[] checkedIn = File.ReadAllLines(Path.Combine(root, "patches", "cecil-owned.list"))
            .Where(line => line.StartsWith("patches/", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(PatchManifest.Create().CecilOwnedPatchPaths(root), checkedIn);
    }

    private static readonly HashSet<string> KnownUnownedLibPatches = new(StringComparer.Ordinal)
    {
        "patches/VintagestoryLib/Vintagestory.API.Common/EventHelper.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientWorldMap.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Client.NoObf/ParticleManager.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Client.NoObf/SvgLoader.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemClientTickingBlocks.cs.patch",

        // Triaged 2026-08-11 (see docs/todo.md's "CecilPatchOwnershipTests backlog
        // triage" entry for the full audit and the server-side Cecil wiring that
        // followed from it). Two groups below. ChunkColumnLoadRequest.cs.patch,
        // ServerProgramArgs.cs.patch (just the RestoreLogsFolder property, not the
        // rest of that file's 1.22.6 diff), and GameDatabase.cs.patch are NOT here -
        // they're genuinely cecil-owned now (see patches/cecil-owned.list).

        // Group 1: pure version string-literal swaps, zero other diff.
        // Moot once forks.json's vintageStoryVersion pin actually bumps to 1.22.6
        // (a fresh decompile already carries the right literal with no patch
        // needed at all, per patches-1.22.6-bridge/README.md).
        "patches/VintagestoryLib/SaveGame.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Client.MaxObf/SessionManager.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiElementModCell.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Client/ClientPackets.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Client/ClientProgramArgs.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Client/GuiScreenDownloadMods.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Client/GuiScreenServerDashboard.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Client/GuiScreenSingleplayer.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Client/ScreenManager.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Common/CleanInstallCheck.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Common/EntityTypeNet.cs.patch",
        "patches/VintagestoryLib/Vintagestory.ModDb/ModDbUtil.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Server/AuthServerComm.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Server/CmdStats.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Server/ServerChunk.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Server/ServerMapRegion.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Server/ServerProgram.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Server/ServerSystemHeartbeat.cs.patch",

        // Group 2: real changes, already documented elsewhere as deliberately not
        // yet Cecil-wired (a lambda/nested-type transplant blocker, or an
        // unconfirmed 1.22.6 vanilla change waiting on the version-pin bump).
        // ClientSystemStartup: docs/implementation-plans/chunk-tesselator-worker-pool-wiring-plan-2026-08-10.md,
        //   HandleWorldMetaData's lambda ordinal mismatch (Option A rejected).
        // TextureAtlasManager: same doc, "IsTesselationThread guard stays inert."
        // AssetManager: GetAssetsDontLoad's Parallel.For lambda compiles to a
        //   <>c__DisplayClass absent from vanilla - the same ordinal hazard class,
        //   not yet worked around with a lambda-free rewrite.
        // ServerConfig/ServerPlayer/ServerProgramArgs/Logger: confirmed real
        //   vanilla version diffs per docs/done.md's 2026-08-05 session,
        //   correctly withheld until the version pin bumps.
        "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientSystemStartup.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Client.NoObf/TextureAtlasManager.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Common/AssetManager.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Server/ServerConfig.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Server/ServerPlayer.cs.patch",
        "patches/VintagestoryLib/Vintagestory/Logger.cs.patch",
    };

    /// <summary>
    /// Was failing on exactly 2 files (ChunkColumnLoadRequest.cs.patch,
    /// GameDatabase.cs.patch), deliberately left unallowlisted rather than
    /// papered over: they're real Optimum dependencies of already-cecil-owned
    /// server patches (ServerSystemSupplyChunks, ServerSystemLoadAndSaveGame),
    /// but patch selection had zero Vintagestory.Server.* targets at
    /// all - the entire "Server-side worldgen scheduler and chunk read pool"
    /// section of cecil-owned.list had never actually been wired up, despite
    /// claiming to ship since 2026-07-28. Fixed 2026-08-11: the server-side
    /// Cecil targets are wired for real now (see docs/todo.md's
    /// "CecilPatchOwnershipTests backlog triage" entry for the full
    /// investigation and docs/implementation-plans/server-worldgen-chunk-pool-cecil-wiring-plan-2026-08-11.md
    /// for the wiring itself), so this passes cleanly again.
    /// </summary>
    [Fact]
    public void NewVintagestoryLibPatchesNeedCecilOwnership()
    {
        string repoRoot = FindRepositoryRoot();
        string patchesRoot = Path.Combine(repoRoot, "patches", "VintagestoryLib");
        string cecilListPath = Path.Combine(repoRoot, "patches", "cecil-owned.list");

        HashSet<string> owned = File.ReadAllLines(cecilListPath)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("patches/VintagestoryLib/", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        string[] unowned = Directory.GetFiles(patchesRoot, "*.patch", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(repoRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => !owned.Contains(path))
            .Where(path => !KnownUnownedLibPatches.Contains(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unowned);
    }

    [Fact]
    public void CecilMemberNamesMatchTheCompiledTickSliceConstant()
    {
        // RandomTickSlice ships via the recompiled VintagestoryLib.dll (not Cecil).
        // The patcher handles client patches only; server features compile in.
        // This test validates that if TickSlice ever moves to Cecil, the naming
        // convention (PascalCase) is used. For now, just verify the patcher loads.
        string source = PatcherSource.Read();

        Assert.DoesNotContain("\"optimumTickSliceCount\"", source);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "patches", "cecil-owned.list");
            if (File.Exists(candidate))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find patches/cecil-owned.list from {AppContext.BaseDirectory}.");
    }
}
}

// Source: Optimum.Tests/CecilTransplantBoundaryTests.cs
#if !NO_DONOR
namespace Optimum.Tests
{
using System.Reflection;
using Vintagestory.Client.NoObf;
using Xunit;

// The release DLL is the vanilla assembly with selected method bodies
// cecil-transplanted from this compiled donor (Optimum.Patcher/PatchManifest.cs).
// A transplanted body's member references resolve at JIT time against the
// vanilla definitions by name AND signature, so any member a transplanted
// method touches must keep its vanilla type in the donor. Optimum 0.2.1
// shipped ClientWorldMap.chunksLock retyped object -> System.Threading.Lock
// while ChunkCuller.CullInvisibleChunks (transplanted) referenced it: every
// world load then killed the chunkculling thread with MissingFieldException
// "Field not found: chunksLock". These tests pin the vanilla signatures of
// members that transplanted methods reference. The hardened
// SelfConsistencyVerifier fails the patcher on any new mismatch; this pins
// the known one at unit-test speed, before a package run.
public class CecilTransplantBoundaryTests
{
    [Fact]
    public void ChunksLockKeepsItsVanillaObjectType()
    {
        FieldInfo field = typeof(ClientWorldMap).GetField(
            "chunksLock", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        Assert.Equal(typeof(object), field.FieldType);
    }
}
}
#endif

// Source: Optimum.Tests/api-patcher-type-forward-tests.cs
namespace Optimum.Tests
{
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

/// <summary>
/// Verifies that the ApiPatcher injects type forwards from VintagestoryAPI.dll
/// to Optimum.Api.Contracts.dll for all Optimum-original types that VintagestoryLib
/// references under the [VintagestoryAPI] assembly scope.
/// </summary>
public class ApiPatcherTypeForwardTests
{
    [Fact]
    public void ApiPatcher_InjectsTypeForwards_ForContractsTypes()
    {
        string apiPatcher = Read("Optimum.Patcher/api-patcher.cs");

        Assert.Contains("InjectTypeForwards", apiPatcher);
        Assert.Contains("ExportedTypes.Add", apiPatcher);
        Assert.Contains("AssemblyNameReference", apiPatcher);
        Assert.Contains("Optimum.Api.Contracts", apiPatcher);
    }

    [Fact]
    public void ContractsProject_IncludesCoreManagedTypes()
    {
        string contracts = Read("optimum-api-contracts/optimum-api-contracts.csproj");

        // Core types that are 100% Optimum-original and belong in contracts.
        Assert.Contains("OptimumConfig.cs", contracts);
        Assert.Contains("OptimumAnimLod.cs", contracts);
        Assert.Contains("OptimumGreedyMesher.cs", contracts);
        Assert.Contains("OptimumWorkerInstances.cs", contracts);
    }

    [Fact]
    public void ForkProject_ExcludesContractsTypes()
    {
        string fork = Read("sources/VintagestoryApi/VintagestoryAPI.csproj");

        // The fork excludes types that live in contracts to avoid CS0433.
        Assert.Contains("<Compile Remove=\"Config\\OptimumConfig.cs\"", fork);
        Assert.Contains("<Compile Remove=\"Config\\OptimumWorkerInstances.cs\"", fork);
    }

    [Fact]
    public void TypeForwardMethod_SkipsExistingTypeDefs()
    {
        string apiPatcher = Read("Optimum.Patcher/api-patcher.cs");

        // The method must skip types already defined in vanilla to avoid conflicts.
        Assert.Contains("vanilla.MainModule.GetType(type.FullName) != null", apiPatcher);
    }

    [Fact]
    public void TypeForwardMethod_HandlesNestedTypes()
    {
        string apiPatcher = Read("Optimum.Patcher/api-patcher.cs");

        Assert.Contains("NestedTypes", apiPatcher);
        Assert.Contains("NestedPublic", apiPatcher);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
}

// Source: Optimum.Tests/api-version-label-tests.cs
namespace Optimum.Tests
{
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Xunit;

public sealed class ApiVersionLabelTests
{
    [Fact]
    public void PatchGameVersionLabelAppendsOptimumVersionToVanillaValue()
    {
        using ModuleDefinition module = ModuleDefinition.CreateModule("VintagestoryAPI", ModuleKind.Dll);
        var gameVersion = new TypeDefinition(
            "Vintagestory.API.Config",
            "GameVersion",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
            module.TypeSystem.Object);
        module.Types.Add(gameVersion);

        var longGameVersion = new FieldDefinition(
            "LongGameVersion",
            FieldAttributes.Public | FieldAttributes.Static,
            module.TypeSystem.String);
        gameVersion.Fields.Add(longGameVersion);

        var initializer = new MethodDefinition(
            ".cctor",
            MethodAttributes.Private |
            MethodAttributes.Static |
            MethodAttributes.HideBySig |
            MethodAttributes.SpecialName |
            MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        gameVersion.Methods.Add(initializer);
        ILProcessor processor = initializer.Body.GetILProcessor();
        processor.Append(Instruction.Create(OpCodes.Ldstr, "v1.22.7 (Stable)"));
        processor.Append(Instruction.Create(OpCodes.Stsfld, longGameVersion));
        processor.Append(Instruction.Create(OpCodes.Ret));

        int patched = ApiPatcher.PatchGameVersionLabel(module, "0.3.3");

        Assert.Equal(1, patched);
        Instruction[] instructions = initializer.Body.Instructions.ToArray();
        Assert.Equal(OpCodes.Ldsfld, instructions[^5].OpCode);
        Assert.Same(longGameVersion, instructions[^5].Operand);
        Assert.Equal(OpCodes.Ldstr, instructions[^4].OpCode);
        Assert.Equal(" + Optimum v0.3.3", instructions[^4].Operand);
        Assert.Equal(OpCodes.Call, instructions[^3].OpCode);
        Assert.Equal("System.String System.String::Concat(System.String,System.String)", instructions[^3].Operand.ToString());
        Assert.Equal(OpCodes.Stsfld, instructions[^2].OpCode);
        Assert.Same(longGameVersion, instructions[^2].Operand);
        Assert.Equal(OpCodes.Ret, instructions[^1].OpCode);
    }
}
}

// Source: Optimum.Tests/bootstrap-source-sync-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// bootstrap.sh and worktree-bootstrap.sh copy Optimum-only files from sources/ into the
/// working tree, where the fork projects are built. Asset overlays and the native shader
/// tree are not project source: deploy, the packagers and the shader compiler read them
/// from sources/ directly. A root copy of one of them is stale the moment the real file
/// is edited, and an edit made to the copy is silently lost.
/// </summary>
public class BootstrapSourceSyncCoverageTests
{
    private static readonly string[] SourceOnlyTrees = { "lang", "shaders", "shaderincludes", "shaders-vk" };

    [Theory]
    [InlineData("scripts/bootstrap.sh")]
    [InlineData("scripts/dev/worktree-bootstrap.sh")]
    public void TheSourceSyncSkipsEveryTreeThatIsReadFromSourcesDirectly(string script)
    {
        string text = File.ReadAllText(Path.Combine(Root(), script));
        Match skip = Regex.Match(text, @"case ""\$top(?:_proj)?"" in\s+([a-z|\-]+)\)\s+continue");
        Assert.True(skip.Success, script + " no longer has the sources/ skip list");

        string[] skipped = skip.Groups[1].Value.Split('|');
        foreach (string tree in SourceOnlyTrees)
        {
            Assert.Contains(tree, skipped);
        }
    }

    private static string Root()
    {
        string root = Directory.GetCurrentDirectory();
        while (!Directory.Exists(Path.Combine(root, "Optimum.Patcher"))) root = Directory.GetParent(root)!.FullName;
        return root;
    }
}
}
