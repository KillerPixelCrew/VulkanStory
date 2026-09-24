// Source: Optimum.Tests/OptimumOutfitAnimatorCacheCoverageTests.cs
namespace Optimum.Tests
{
using System.IO;
using Xunit;

/// <summary>
/// OptimumOutfitAnimatorCache is a brand-new type (not member-injected into an existing vanilla
/// type), so its own static field initializer (Cache = new()) runs correctly when Cecil clones
/// the whole type via InjectTypes - see OptimumOutfitShapeCacheCoverageTests for the same
/// reasoning and CecilInjectedFieldInitializerTests for the four real crashes this class of bug
/// caused historically. This guards the same registration for the animator cache, plus the new
/// optimumAnimatorCacheKey field injected into EntityDressedHumanoid (an existing type, so this
/// one field-only injects correctly since it has no initializer to lose - defaults to null,
/// which is exactly what a fresh instance field would be anyway).
/// </summary>
public class OptimumOutfitAnimatorCacheCoverageTests
{
    [Fact]
    public void CacheTypeIsRegisteredForWholeTypeInjectionNotMemberInjection()
    {
        string patcher = Read("Optimum.Patcher/mod-patcher.cs");

        Assert.Contains("\"Vintagestory.GameContent.OptimumOutfitAnimatorCache\",", patcher);
    }

    [Fact]
    public void OnTesselationThreeArgOverloadIsRegisteredAsAMethodTransplantTarget()
    {
        string patcher = Read("Optimum.Patcher/mod-patcher.cs");

        Assert.Contains(
            "new(\"Vintagestory.GameContent.EntityDressedHumanoid\", \"OnTesselation\", 3, Optional: true),",
            patcher);
    }

    [Fact]
    public void AnimatorCacheKeyFieldIsRegisteredForMemberInjection()
    {
        string patcher = Read("Optimum.Patcher/mod-patcher.cs");

        Assert.Contains("\"optimumAnimatorCacheKey\",", patcher);
    }

    [Fact]
    public void NewSourceFileIsCopiedIntoBothRuntimeDonorScripts()
    {
        string bashScript = Read("scripts/prepare-runtime-donors.sh");
        string ps1Script = Read("scripts/prepare-runtime-donors.ps1");

        Assert.Contains("sources/VSSurvivalMod/Lore/Village/OptimumOutfitAnimatorCache.cs", bashScript);
        Assert.Contains("VSSurvivalMod/Vintagestory/GameContent/OptimumOutfitAnimatorCache.cs", bashScript);
        Assert.Contains("sources/VSSurvivalMod/Lore/Village/OptimumOutfitAnimatorCache.cs", ps1Script);
        Assert.Contains("VSSurvivalMod/Vintagestory/GameContent/OptimumOutfitAnimatorCache.cs", ps1Script);
    }

    [Fact]
    public void SourcesCopyAndWorkingTreeFileMatch()
    {
        string sourcesCopy = Read("sources/VSSurvivalMod/Lore/Village/OptimumOutfitAnimatorCache.cs");
        string workingCopy = Read("VSSurvivalMod/Lore/Village/OptimumOutfitAnimatorCache.cs");

        Assert.Equal(sourcesCopy, workingCopy);
    }

    [Fact]
    public void PatchFallsBackToVanillaLoadAnimatorOnCacheMissAndStoresResult()
    {
        // The whole safety argument mirrors OptimumOutfitShapeCache: a miss must fall through to
        // vanilla's own (correct, if expensive) AnimManager.LoadAnimator, and the result gets
        // stored for next time - never silently skip building an animator.
        string patch = Read("patches/runtime/VSSurvivalMod/Vintagestory/GameContent/EntityDressedHumanoid.cs.patch");

        Assert.Contains("OptimumOutfitAnimatorCache.TryApply(", patch);
        Assert.Contains("AnimManager.LoadAnimator(World.Api, this, entityShape, AnimManager.Animator?.Animations, requirePosesOnServer, willDisableElements, \"head\");", patch);
        Assert.Contains("OptimumOutfitAnimatorCache.Store(optimumAnimatorCacheKey, entityShape, AnimManager.Animator);", patch);
    }

    [Fact]
    public void PatchGatesOnEnabledFlagBeforeDuplicatingVanillaOnTesselationLogic()
    {
        // The duplicated overlay/behavior/willDisableElements block is only safe under the
        // assumption documented on OptimumConfig.EntityOutfitAnimatorCacheEnabled (trader/
        // villager entity types have none of those); when the flag is off, this must delegate
        // straight to base.OnTesselation instead of running the duplicated copy for no reason.
        string patch = Read("patches/runtime/VSSurvivalMod/Vintagestory/GameContent/EntityDressedHumanoid.cs.patch");

        Assert.Contains("if (!OptimumOutfitAnimatorCache.Enabled)", patch);
        Assert.Contains("base.OnTesselation(ref entityShape, shapePathForLogging, ref shapeIsCloned);", patch);
    }

    [Fact]
    public void CacheConfigDefaultsOff()
    {
        string config = Read("sources/VintagestoryApi/Config/OptimumConfig.cs");

        Assert.Contains("public static bool EntityOutfitAnimatorCacheEnabled = false;", config);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
}

// Source: Optimum.Tests/OptimumOutfitShapeCacheCoverageTests.cs
namespace Optimum.Tests
{
using System.IO;
using Xunit;

/// <summary>
/// OptimumOutfitShapeCache is a brand-new type (not member-injected into an existing vanilla
/// type), so its own static field initializer (Cache = new()) runs correctly when Cecil clones
/// the whole type via InjectTypes - unlike per-field injection into an already-compiled type
/// (see CecilInjectedFieldInitializerTests), which never re-runs a constructor. This class
/// guards the registration that makes that safe: the type must stay in mod-patcher.cs's Types
/// list (whole-type clone, own .cctor included), never move to the Members dictionary
/// (field-only injection into EntityDressedHumanoid, which would silently drop the initializer
/// exactly like the four real crashes CecilInjectedFieldInitializerTests documents).
/// </summary>
public class OptimumOutfitShapeCacheCoverageTests
{
    [Fact]
    public void CacheTypeIsRegisteredForWholeTypeInjectionNotMemberInjection()
    {
        string patcher = Read("Optimum.Patcher/mod-patcher.cs");

        Assert.Contains("\"Vintagestory.GameContent.OptimumOutfitShapeCache\",", patcher);
    }

    [Fact]
    public void OnTesselationIsRegisteredAsAMethodTransplantTarget()
    {
        string patcher = Read("Optimum.Patcher/mod-patcher.cs");

        Assert.Contains(
            "new(\"Vintagestory.GameContent.EntityDressedHumanoid\", \"OnTesselation\", 2),",
            patcher);
    }

    [Fact]
    public void NewSourceFileIsCopiedIntoBothRuntimeDonorScripts()
    {
        // A whole-new-type overlay (no vanilla counterpart to diff against) has to be copied
        // into the decompiled tree directly - see the equivalent step for
        // OptimumStatusModSystem/CrucibleInFirepitRenderer a few lines above each of these.
        string bashScript = Read("scripts/prepare-runtime-donors.sh");
        string ps1Script = Read("scripts/prepare-runtime-donors.ps1");

        Assert.Contains("sources/VSSurvivalMod/Lore/Village/OptimumOutfitShapeCache.cs", bashScript);
        Assert.Contains("VSSurvivalMod/Vintagestory/GameContent/OptimumOutfitShapeCache.cs", bashScript);
        Assert.Contains("sources/VSSurvivalMod/Lore/Village/OptimumOutfitShapeCache.cs", ps1Script);
        Assert.Contains("VSSurvivalMod/Vintagestory/GameContent/OptimumOutfitShapeCache.cs", ps1Script);
    }

    [Fact]
    public void SourcesCopyAndWorkingTreeFileMatch()
    {
        // sources/ is the tracked file the runtime-donor pipeline actually copies from; the
        // gitignored working-tree copy under VSSurvivalMod/ must stay in sync with it, the same
        // way every other "new type" overlay in this repo does.
        string sourcesCopy = Read("sources/VSSurvivalMod/Lore/Village/OptimumOutfitShapeCache.cs");
        string workingCopy = Read("VSSurvivalMod/Lore/Village/OptimumOutfitShapeCache.cs");

        Assert.Equal(sourcesCopy, workingCopy);
    }

    [Fact]
    public void EntityDressedHumanoidPatchStoresAnIndependentCloneNotTheLiveShape()
    {
        // The whole safety argument for this cache hinges on Store() never being handed (and
        // TryGet() never handing out) the shape instance actually being mutated elsewhere -
        // OptimumOutfitShapeCache.Store() clones internally, so the call site here just needs
        // to pass its own local, not attempt to clone before calling (that would be redundant,
        // not wrong, but drifting from the class's own contract is a sign something regressed).
        string patch = Read("patches/runtime/VSSurvivalMod/Vintagestory/GameContent/EntityDressedHumanoid.cs.patch");

        Assert.Contains("OptimumOutfitShapeCache.Store(optimumOutfitCacheKey, entityShape, fastSmallDictionary);", patch);
        Assert.Contains("OptimumOutfitShapeCache.TryGet(optimumOutfitCacheKey, out optimumCachedShape, out optimumCachedTextures)", patch);
    }

    [Fact]
    public void CacheConfigDefaultsOff()
    {
        // New code on the entity-appearance path, only compile/unit-test verified so far - see
        // OptimumConfig.EntityOutfitShapeCacheEnabled's own doc comment for the full reasoning.
        string config = Read("sources/VintagestoryApi/Config/OptimumConfig.cs");

        Assert.Contains("public static bool EntityOutfitShapeCacheEnabled = false;", config);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
}

// Source: Optimum.Tests/OptimumOutfitTexturePrewarmerCoverageTests.cs
namespace Optimum.Tests
{
using System.IO;
using Xunit;

/// <summary>
/// OptimumOutfitTexturePrewarmerModSystem is a brand-new type, registered for whole-type Cecil
/// injection (own .cctor/static init runs correctly) rather than field injection into an
/// existing type - see OptimumOutfitShapeCacheCoverageTests and
/// CecilInjectedFieldInitializerTests for why that distinction matters. This guards the same
/// registration for the prewarmer.
/// </summary>
public class OptimumOutfitTexturePrewarmerCoverageTests
{
    [Fact]
    public void PrewarmerTypeIsRegisteredForWholeTypeInjection()
    {
        string patcher = Read("Optimum.Patcher/mod-patcher.cs");

        Assert.Contains("\"Vintagestory.GameContent.OptimumOutfitTexturePrewarmerModSystem\",", patcher);
    }

    [Fact]
    public void NewSourceFileIsCopiedIntoBothRuntimeDonorScripts()
    {
        string bashScript = Read("scripts/prepare-runtime-donors.sh");
        string ps1Script = Read("scripts/prepare-runtime-donors.ps1");

        Assert.Contains("sources/VSSurvivalMod/Lore/Village/OptimumOutfitTexturePrewarmer.cs", bashScript);
        Assert.Contains("VSSurvivalMod/Vintagestory/GameContent/OptimumOutfitTexturePrewarmer.cs", bashScript);
        Assert.Contains("sources/VSSurvivalMod/Lore/Village/OptimumOutfitTexturePrewarmer.cs", ps1Script);
        Assert.Contains("VSSurvivalMod/Vintagestory/GameContent/OptimumOutfitTexturePrewarmer.cs", ps1Script);
    }

    [Fact]
    public void SourcesCopyAndWorkingTreeFileMatch()
    {
        string sourcesCopy = Read("sources/VSSurvivalMod/Lore/Village/OptimumOutfitTexturePrewarmer.cs");
        string workingCopy = Read("VSSurvivalMod/Lore/Village/OptimumOutfitTexturePrewarmer.cs");

        Assert.Equal(sourcesCopy, workingCopy);
    }

    [Fact]
    public void PrewarmerHooksLevelFinalizeNotAnArbitraryThread()
    {
        // GetOrInsertTexture can touch GL (atlas allocation/upload), which requires the render
        // thread. capi.Event.LevelFinalize is a documented main-thread event (the same one
        // GuiManager.OnLevelFinalize already hooks elsewhere in this codebase) - guard against
        // this drifting to a background TyronThreadPool.QueueTask, which would crash or corrupt
        // the atlas on a GL-context-less thread.
        string source = Read("VSSurvivalMod/Lore/Village/OptimumOutfitTexturePrewarmer.cs");

        Assert.Contains("api.Event.LevelFinalize +=", source);
        Assert.DoesNotContain("TyronThreadPool", source);
    }

    [Fact]
    public void PrewarmFailuresArePerConfigNotFatal()
    {
        // One malformed outfit config must not abort the whole prewarm pass or crash the
        // loading screen - see the class's own doc comment for the reasoning.
        string source = Read("VSSurvivalMod/Lore/Village/OptimumOutfitTexturePrewarmer.cs");

        Assert.Contains("catch (System.Exception e)", source);
    }

    [Fact]
    public void PrewarmConfigDefaultsOff()
    {
        string config = Read("sources/VintagestoryApi/Config/OptimumConfig.cs");

        Assert.Contains("public static bool EntityOutfitTexturePrewarmEnabled = false;", config);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
}
