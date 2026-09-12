using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

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
/// entry point: Optimum.Patcher/Program.cs's membersToInject, consumed by
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
/// ClientPlatformWindows.optimumUiTargetClearColor (float[4] of transparent
/// black, for the DLSS-FG step 3 UI target) was the eighth, on 2026-09-12 -
/// which is the point. Seven hand-written guards, one per past instance, did
/// not stop an eighth: they record history, they do not enforce the rule. It
/// NRE'd on the very first frame of the main menu with OPTIMUM_UI_TARGET=1,
/// having passed 1357 source tests and 907 GPU tests, because every one of
/// those constructs the real C# class, where the initializer does run - only
/// the Cecil-transplanted DLL sees the null. Fixed with a lazy allocation at
/// its one use site; the three sibling `= -1` int fields
/// (optimumSceneNoHudIndex, optimumUiTargetIndex, optimumMotionAttachmentIndex)
/// were silently 0 at runtime the whole time and got away with it only because
/// the framebuffer setup assigns them before anything reads - their
/// initializers are gone too, so the source no longer claims something the
/// runtime does not do.
///
/// <para>So the last test in this class is the general one:</para> every field
/// named in a patcher injection manifest is checked, and any initializer on one
/// fails. The per-instance tests above stay as the record of what each of these
/// cost - a server that shut itself down, every mob pathless, truncated
/// worldgen, a black main menu - because that is what stops someone "tidying"
/// the lazy initialisers back into declarations.
///
/// <para>Turning that general rule on found five more, cleared on 2026-09-13.</para>
/// GuiCompositeSettings.oButtonBounds/uButtonBounds looked like it disproved the
/// rule - both are dereferenced with no guard at all in ComposerHeader and
/// updateButtonBounds, yet the Optimum and Optimum-upscaling tabs work in the
/// shipped game. Reading the emitted IL of VintagestoryLib-patched.dll settled it:
/// the patched .ctor is the vanilla one and assigns the seven vanilla
/// *ButtonBounds and none of ours, so both fields really are null after
/// construction - and the only ldfld/stfld for either one in the entire patched
/// assembly is inside _AddOptimumTab, which already allocates them behind an
/// explicit null check. ComposerHeader and updateButtonBounds are neither
/// transplant targets nor injected members (ComposerHeader only takes an IL hook
/// that appends the call to _AddOptimumTab), so their Optimum-added lines are
/// donor-source-only and never reach the game. The tabs work by design, not by
/// accident, and the rule holds exactly as written - the initializer was simply a
/// claim the shipped DLL never honoured. The declarations are now bare and
/// updateButtonBounds allocates lazily, so the donor source stays correct if those
/// methods are ever promoted to transplant targets.
/// EntityBehaviorCollectEntities.OptimumCollectStrideInterval was live: a
/// <c>public static int = 3</c> that read 0, so <c>stride &gt; 1</c> was false on
/// every tick of every item-collecting entity and the spatial-query throttle the
/// field exists for had never once run. Note the two copies - the
/// patches/VSEssentials fork source is normally compiled and keeps its
/// initializer; only the patches/runtime Cecil copy is fixed, with 0 meaning
/// "unset" and the default applied at the use site.
/// EventManager.optimumCachedClimateInvocations/optimumCachedWindInvocations were
/// latent, not live: the rebuild branch is entered whenever the cached delegate
/// does not match, and the cached delegate starts null while <c>current</c> is
/// known non-null, so the first call through either trigger always assigns the
/// array before the loop reads it. What it did risk was the multi-threaded read -
/// climate queries run off worldgen threads - where a second thread could observe
/// the published delegate before the array store. Both readers now snapshot the
/// field into a local and rebuild when that local is null, which fixes the null
/// and the ordering together.
/// WeatherSimulationParticles.optimumLastHeightmapCenterX/Z was a real behaviour
/// difference: an <c>int.MinValue</c> "never sampled" sentinel that was 0, so a
/// player spawning within 4 blocks of x=0 or z=0 got the first low-res rain
/// heightmap refresh skipped entirely. Fixed by moving the sentinel into a
/// <c>bool optimumHeightmapSampled</c>, whose CLR default of false IS the state
/// wanted - the general lesson from all nine: an injected field may only use the
/// CLR zero/null default as its starting value, so pick the encoding that makes
/// that default correct.
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

    /// <summary>
    /// The settings-screen tab buttons. The initializer never ran - the patched
    /// .ctor is vanilla's and assigns only the seven vanilla *ButtonBounds - and the
    /// unguarded dereferences in ComposerHeader/updateButtonBounds never shipped,
    /// because neither method is a transplant target. _AddOptimumTab, which is
    /// injected and is the only code in the patched assembly that touches either
    /// field, allocates both behind a null check. Both facts are asserted here so a
    /// later "tidy the initializer back" or "transplant ComposerHeader too" is
    /// caught by a test rather than by an NRE on the settings screen.
    /// </summary>
    [Fact]
    public void GuiCompositeSettingsTabButtonBoundsHaveNoInitializer()
    {
        string patch = Read("patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch");

        Assert.DoesNotMatch(new Regex(@"ElementBounds\s+oButtonBounds\s*="), patch);
        Assert.DoesNotMatch(new Regex(@"ElementBounds\s+uButtonBounds\s*="), patch);
        Assert.Contains("private ElementBounds oButtonBounds;", patch);
        Assert.Contains("private ElementBounds uButtonBounds;", patch);

        // _AddOptimumTab is injected and is the one shipping writer of both.
        Assert.Contains("if (oButtonBounds == null)", patch);
        Assert.Contains("if (uButtonBounds == null)", patch);

        // updateButtonBounds runs first thing in ComposerHeader; the Optimum lines in
        // both are donor-source-only today, but they must not become a landmine for
        // whoever transplants those methods next.
        Assert.Contains("oButtonBounds ??= ElementBounds.Fixed(0.0, 0.0, 0.0, 40.0).WithFixedPadding(0.0, 3.0);", patch);
        Assert.Contains("uButtonBounds ??= ElementBounds.Fixed(0.0, 0.0, 0.0, 40.0).WithFixedPadding(0.0, 3.0);", patch);
    }

    /// <summary>
    /// The collect-stride throttle. Two copies, and only one of them is broken by
    /// the Cecil rule: the fork source under patches/VSEssentials is a normally
    /// compiled C# class whose constructor really does run, so its
    /// <c>= 3</c> is correct and must stay; the patches/runtime copy is
    /// Cecil-injected and must not carry one. Asserting both directions is the point
    /// - the obvious "fix" is to copy the fork spelling into the runtime patch.
    /// </summary>
    [Fact]
    public void EntityBehaviorCollectEntitiesStrideIntervalHasNoInitializerInTheRuntimeCopy()
    {
        string runtime = Read("patches/runtime/VSEssentials/Vintagestory/GameContent/EntityBehaviorCollectEntities.cs.patch");

        Assert.DoesNotMatch(new Regex(@"OptimumCollectStrideInterval\s*=\s*3\s*;"), runtime);
        Assert.Contains("public static int OptimumCollectStrideInterval;", runtime);
        // 0 means "unset", so the default lives at the use site in OnGameTick.
        Assert.Contains("int stride = OptimumCollectStrideInterval;", runtime);
        Assert.Matches(new Regex(@"if \(stride == 0\)\s*\n\+\s*\{\s*\n\+\s*stride = 3;"), runtime);

        // The fork source is compiled normally. Its initializer is not the bug.
        string fork = Read("patches/VSEssentials/Entity/Behavior/BehaviorCollectEntities.cs.patch");
        Assert.Contains("public static int OptimumCollectStrideInterval = 3;", fork);
    }

    /// <summary>
    /// The cached invocation lists. Latent rather than live (the rebuild branch
    /// always runs on the first call, because the cached delegate starts null and
    /// <c>current</c> is known non-null), but null all the same, and read from more
    /// than one thread. Both readers snapshot into a local and rebuild when the
    /// snapshot is null, which covers the null and the store ordering at once.
    /// </summary>
    [Fact]
    public void EventManagerCachedInvocationListsHaveNoInitializer()
    {
        string patch = Read("patches/VintagestoryLib/Vintagestory.Common/EventManager.cs.patch");

        Assert.DoesNotMatch(new Regex(@"optimumCached(Climate|Wind)Invocations\s*=\s*Array\.Empty<Delegate>\(\);"), patch);
        Assert.Contains("private Delegate[] optimumCachedClimateInvocations;", patch);
        Assert.Contains("private Delegate[] optimumCachedWindInvocations;", patch);

        Assert.Contains("if (invocationList == null || !ReferenceEquals(current, optimumCachedClimateDelegate))", patch);
        Assert.Contains("if (invocationList == null || !ReferenceEquals(current, optimumCachedWindDelegate))", patch);
    }

    /// <summary>
    /// The low-res rain heightmap refresh gate. An <c>int.MinValue</c> sentinel on
    /// an injected field is not a sentinel - it is 0 - so the first refresh was
    /// skipped for anyone spawning within 4 blocks of x=0 or z=0. The "never
    /// sampled" state moved to a bool, where the CLR default is the right one.
    /// </summary>
    [Fact]
    public void WeatherSimulationParticlesHeightmapGateUsesTheClrDefaultAsItsSentinel()
    {
        string patch = Read("patches/runtime/VSEssentials/Vintagestory/GameContent/WeatherSimulationParticles.cs.patch");

        Assert.DoesNotMatch(new Regex(@"optimumLastHeightmapCenter(X|Z)\s*=\s*int\.MinValue;"), patch);
        Assert.Contains("private int optimumLastHeightmapCenterX;", patch);
        Assert.Contains("private int optimumLastHeightmapCenterZ;", patch);
        Assert.Contains("private bool optimumHeightmapSampled;", patch);
        Assert.Contains("if (!optimumHeightmapSampled || Math.Abs(centerPos.X - optimumLastHeightmapCenterX) >= 4", patch);
        Assert.Contains("optimumHeightmapSampled = true;", patch);
    }

    /// <summary>
    /// The general rule, enforced rather than remembered: no field named in any
    /// patcher injection manifest may carry a field initializer.
    ///
    /// <para>Why a field and not a property or a method: Cecil injection copies the
    /// member's metadata and body into an already-compiled type. A field initializer
    /// is neither - the C# compiler lowers it into the constructor (or the static
    /// constructor), and the constructor is not what gets injected. So the
    /// initializer is dropped in silence and the field holds the CLR default: null
    /// for a reference type, 0 for a number. Nothing warns; the source keeps saying
    /// <c>= -1</c> or <c>= new float[4]</c> and the runtime keeps disagreeing.</para>
    ///
    /// <para>Allowed: <c>const</c> (the compiler inlines every use, so no field is
    /// read at runtime at all) and expression-bodied properties (<c>=&gt; x</c>, which
    /// are methods, not fields). Everything else with an <c>=</c> before the
    /// semicolon is the bug this class has now seen eight times.</para>
    ///
    /// <para>The fix at every site is the same: drop the initializer and either
    /// allocate lazily at each use (<c>x ??= new T()</c>, or an explicit null check
    /// where the Cecil rules forbid the operator), or assign it from a method that is
    /// itself transplanted and provably runs first.</para>
    /// </summary>
    [Fact]
    public void NoInjectedFieldAnywhereCarriesAnInitializer()
    {
        var injected = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var constructorTransplanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (string manifest in new[]
        {
            "Optimum.Patcher/Program.cs",
            "Optimum.Patcher/mod-patcher.cs",
            "Optimum.Patcher/api-patcher.cs",
        })
        {
            string? path = TryFind(manifest);
            if (path == null) continue;
            string text = File.ReadAllText(path);
            CollectInjectedMembers(text, injected);
            // The one exemption, and the reason the rule is about constructors rather
            // than about fields: when the type's own .ctor is a transplant target, the
            // donor's constructor body - initializers and all - replaces the vanilla
            // one, so an initializer on an injected field of that type really does run.
            // Vintagestory.Server.ChunkColumnLoadRequest and
            // Vintagestory.GameContent.ChunkMapLayer are both in this position today.
            foreach (Match constructor in Regex.Matches(text, @"new\(\s*""(?<type>[\w\.]+)""\s*,\s*""\.ctor"""))
            {
                constructorTransplanted.Add(constructor.Groups["type"].Value);
            }
        }

        // If the manifests stop parsing, this test would pass by finding nothing.
        Assert.True(injected.Count >= 3,
            "parsed " + injected.Count + " injected types; the manifest syntax changed and this test went blind");

        var offenders = new List<string>();
        int typesChecked = 0;
        foreach (KeyValuePair<string, HashSet<string>> entry in injected)
        {
            string typeName = entry.Key;
            if (constructorTransplanted.Contains(typeName)) continue;
            int dot = typeName.LastIndexOf('.');
            string shortName = dot >= 0 ? typeName.Substring(dot + 1) : typeName;
            foreach (string file in SourcesDeclaring(shortName))
            {
                typesChecked++;
                foreach (string offence in InitializedFields(File.ReadAllText(file), entry.Value))
                {
                    offenders.Add(Path.GetFileName(file) + ": " + offence);
                }
            }
        }

        Assert.True(typesChecked >= 3,
            "found source for " + typesChecked + " injected types; the layout changed and this test went blind");

        // No allowance list: the five instances that were in the tree when this rule
        // was written were cleared on 2026-09-13 (see the class doc), so any offender
        // at all is a new one.
        Assert.True(offenders.Count == 0,
            "these injected fields carry an initializer the Cecil transplant never runs, so they hold the CLR default"
                + " (null, or 0) at runtime no matter what the source says. Drop the initializer and allocate lazily at"
                + " each use, or assign from a transplanted method that provably runs first:"
                + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Pulls <c>["Namespace.Type"] = new() { "a", "b" }</c> and its
    /// <c>["Namespace.Type"] = [ "a", "b" ]</c> spelling out of a manifest. Both
    /// patcher entry points use one or the other.
    /// </summary>
    private static void CollectInjectedMembers(string manifest, Dictionary<string, HashSet<string>> into)
    {
        // Only the injection manifests, never every dictionary in the file: these
        // patchers also carry lists of transplant targets, virtualized methods and
        // retyped fields, and a retyped vanilla field legitimately keeps its
        // initializer because its constructor is the vanilla one and still runs.
        string scoped = string.Empty;
        foreach (Match anchor in Regex.Matches(manifest, @"(?:membersToInject\s*=\s*new\s+Dictionary<[^>]*>|Members:\s*new\(\))"))
        {
            scoped += BalancedBlockAfter(manifest, anchor.Index + anchor.Length);
        }

        foreach (Match entry in Regex.Matches(
            scoped, @"\[""(?<type>[\w\.]+)""\]\s*=\s*(?:new\(\)\s*)?[\{\[](?<body>[^\}\]]*)[\}\]]"))
        {
            string type = entry.Groups["type"].Value;
            if (!into.TryGetValue(type, out HashSet<string>? members))
            {
                members = new HashSet<string>(StringComparer.Ordinal);
                into[type] = members;
            }
            foreach (Match member in Regex.Matches(entry.Groups["body"].Value, @"""(?<name>[\w]+)"""))
            {
                members.Add(member.Groups["name"].Value);
            }
        }
    }

    /// <summary>
    /// The <c>{ ... }</c> block starting at or after <paramref name="from" />, brace
    /// matched so a nested initializer does not end it early.
    /// </summary>
    private static string BalancedBlockAfter(string text, int from)
    {
        int open = text.IndexOf('{', from);
        if (open < 0) return string.Empty;
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0) return text.Substring(open, i - open + 1);
            }
        }
        return text.Substring(open);
    }

    /// <summary>
    /// Every tracked file that could declare the type: the decompiled lib, and the
    /// patches for the forks (whose working trees are git-ignored, so the patch is
    /// the only copy a fresh clone has).
    /// </summary>
    private static IEnumerable<string> SourcesDeclaring(string shortTypeName)
    {
        string root = Path.GetDirectoryName(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"))!;
        root = Path.GetDirectoryName(root)!;
        foreach (string directory in new[] { "build", "patches" })
        {
            string search = Path.Combine(root, directory);
            if (!Directory.Exists(search)) continue;
            foreach (string file in Directory.EnumerateFiles(search, shortTypeName + ".cs*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".cs.patch", StringComparison.Ordinal))
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>
    /// Field declarations among <paramref name="members" /> that carry an
    /// initializer. Skips <c>const</c> (inlined at every use) and expression-bodied
    /// members (<c>=&gt;</c>), which are methods rather than fields. A patch file's
    /// leading '+' is tolerated so the fork patches are read the same way.
    /// </summary>
    private static IEnumerable<string> InitializedFields(string source, HashSet<string> members)
    {
        foreach (Match field in Regex.Matches(
            source,
            @"^\+?\s*(?:public|private|protected|internal)\s+(?<modifiers>(?:static\s+|readonly\s+|volatile\s+)*)"
                + @"(?!const\b)[\w\.\<\>\[\]\,\?]+(?:\s*\[\s*\])?\s+(?<name>\w+)\s*=\s*(?!>)[^;]+;",
            RegexOptions.Multiline))
        {
            string name = field.Groups["name"].Value;
            if (members.Contains(name))
            {
                yield return field.Value.TrimStart('+', ' ', '\t').Trim();
            }
        }
    }

    private static string? TryFind(string relativePath)
    {
        try
        {
            return PatchReader.FindRepositoryFile(relativePath);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
