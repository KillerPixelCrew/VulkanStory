// Source: Optimum.Tests/KometCompatibilityGuardTests.cs
namespace Optimum.Tests
{
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Intrinsics;
using System.Text.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Xunit;

[Collection("OptimumConfig")]
public sealed class KometCompatibilityGuardTests : IDisposable
{
    public KometCompatibilityGuardTests()
    {
        OptimumKometGuard.ResetForTests();
    }

    public void Dispose()
    {
        OptimumKometGuard.ResetForTests();
    }

    [Fact]
    public void DefaultState_HasNoKometDetected_AndGuardEnabled()
    {
        Assert.False(OptimumConfig.KometDetected);
        Assert.True(OptimumConfig.KometGuardEnabled);
        Assert.False(OptimumKometGuard.IsDetected);
        Assert.Equal("none detected", OptimumKometGuard.GetStatusLine());
        Assert.Contains("none detected", OptimumDiagnostics.GetConflictingModsSummary());
    }

    [Fact]
    public void DetectViaModLoader_SetsDetectedAndLogsAdvisory()
    {
        var logs = new List<string>();
        var loader = new FakeModLoader(hasKomet: true, kometVersion: "2.0.0");

        bool detected = OptimumKometGuard.Detect(loader, msg => logs.Add(msg));

        Assert.True(detected);
        Assert.True(OptimumConfig.KometDetected);
        Assert.True(OptimumKometGuard.IsDetected);
        Assert.Equal("ModLoader", OptimumKometGuard.DetectionSource);
        Assert.Equal("2.0.0", OptimumKometGuard.DetectedVersion);
        Assert.Single(logs);
        Assert.Contains("Komet mod detected", logs[0]);
        Assert.Contains("glMultiDrawElementsIndirect", logs[0]);

        string status = OptimumKometGuard.GetStatusLine();
        Assert.Contains("Komet v2.0.0", status);
        Assert.Contains("ACTIVE (safely yielding MDI/SIMD)", status);
    }

    [Fact]
    public void DetectViaModLoader_IgnoresWhenKometNotPresent()
    {
        var logs = new List<string>();
        var loader = new FakeModLoader(hasKomet: false);

        bool detected = OptimumKometGuard.Detect(loader, msg => logs.Add(msg));

        Assert.False(detected);
        Assert.False(OptimumConfig.KometDetected);
        Assert.Empty(logs);
        Assert.Equal("none detected", OptimumKometGuard.GetStatusLine());
    }

    [Fact]
    public void EffectiveIndirectDraw_SafelyYieldsWhenKometDetected()
    {
        bool origSupported = OptimumConfig.IndirectDrawSupported;
        bool origEnabled = OptimumConfig.IndirectDrawEnabled;

        try
        {
            OptimumConfig.IndirectDrawSupported = true;
            OptimumConfig.IndirectDrawEnabled = true;

            // Without Komet: MDI is active
            OptimumConfig.KometDetected = false;
            OptimumConfig.KometGuardEnabled = true;
            Assert.True(OptimumConfig.EffectiveIndirectDraw);

            // With Komet detected and guard enabled: yields MDI to avoid conflict with Harmony prefix
            OptimumConfig.KometDetected = true;
            Assert.False(OptimumConfig.EffectiveIndirectDraw);

            // User explicitly disables guard: force MDI despite Komet
            OptimumConfig.KometGuardEnabled = false;
            Assert.True(OptimumConfig.EffectiveIndirectDraw);
        }
        finally
        {
            OptimumConfig.IndirectDrawSupported = origSupported;
            OptimumConfig.IndirectDrawEnabled = origEnabled;
        }
    }

    [Fact]
    public void EffectiveSimdCulling_SafelyYieldsWhenKometDetected()
    {
        bool origEnabled = OptimumConfig.SimdCullingEnabled;

        try
        {
            OptimumConfig.SimdCullingEnabled = true;

            // Without Komet: SIMD active if CPU supports it
            OptimumConfig.KometDetected = false;
            OptimumConfig.KometGuardEnabled = true;
            Assert.Equal(Vector128.IsHardwareAccelerated, OptimumConfig.EffectiveSimdCulling);

            // With Komet detected and guard enabled: yields SIMD culling to avoid racing against FastCuller
            OptimumConfig.KometDetected = true;
            Assert.False(OptimumConfig.EffectiveSimdCulling);

            // User explicitly disables guard: force SIMD despite Komet
            OptimumConfig.KometGuardEnabled = false;
            Assert.Equal(Vector128.IsHardwareAccelerated, OptimumConfig.EffectiveSimdCulling);
        }
        finally
        {
            OptimumConfig.SimdCullingEnabled = origEnabled;
        }
    }

    [Fact]
    public void ChunkRenderSummary_ReflectsKometOverrideState()
    {
        OptimumConfig.KometDetected = false;
        string cleanSummary = OptimumDiagnostics.GetChunkRenderSummary();
        Assert.DoesNotContain("kometOverride", cleanSummary);

        OptimumConfig.KometDetected = true;
        string overriddenSummary = OptimumDiagnostics.GetChunkRenderSummary();
        Assert.Contains("kometOverride=true", overriddenSummary);
    }

    [Fact]
    public void DescribeToggles_IncludesKometGuard()
    {
        var toggles = OptimumConfig.DescribeToggles();
        var entry = toggles.FirstOrDefault(t => t.Name == nameof(OptimumConfigData.KometGuard));

        Assert.NotNull(entry.Name);
        Assert.Equal("True", entry.Value);
    }

    [Fact]
    public void ConfigSerialization_RoundtripsKometGuard()
    {
        var data = new OptimumConfigData { KometGuard = false };
        string json = JsonSerializer.Serialize(data);
        var parsed = JsonSerializer.Deserialize<OptimumConfigData>(json);

        Assert.NotNull(parsed);
        Assert.False(parsed.KometGuard);
    }

    [Fact]
    public void NotifyPlayerOnJoin_NotifiesOnlyOncePerSession()
    {
        var messages = new List<string>();
        OptimumConfig.KometDetected = true;
        OptimumConfig.KometGuardEnabled = true;

        // First join in session notifies
        OptimumKometGuard.NotifyPlayerOnJoin(msg => messages.Add(msg));
        Assert.Single(messages);
        Assert.Contains("Komet mod detected", messages[0]);

        // Second join in same session does not repeat
        OptimumKometGuard.NotifyPlayerOnJoin(msg => messages.Add(msg));
        Assert.Single(messages);

        // ResetSession simulates leaving and rejoining world
        OptimumKometGuard.ResetSession();
        OptimumKometGuard.NotifyPlayerOnJoin(msg => messages.Add(msg));
        Assert.Equal(2, messages.Count);
    }

    private sealed class FakeModLoader : IModLoader
    {
        private readonly bool _hasKomet;
        private readonly string _kometVersion;

        public FakeModLoader(bool hasKomet, string kometVersion = "1.0.0")
        {
            _hasKomet = hasKomet;
            _kometVersion = kometVersion;
        }

        public IEnumerable<Mod> Mods => _hasKomet
            ? new List<Mod> { new FakeMod("komet", "Komet", _kometVersion) }
            : new List<Mod>();

        public IEnumerable<ModSystem> Systems => Enumerable.Empty<ModSystem>();

        public Mod GetMod(string modID)
        {
            if (_hasKomet && string.Equals(modID, "komet", StringComparison.OrdinalIgnoreCase))
            {
                return new FakeMod("komet", "Komet", _kometVersion);
            }
            return null;
        }

        public bool IsModEnabled(string modID)
        {
            return _hasKomet && string.Equals(modID, "komet", StringComparison.OrdinalIgnoreCase);
        }

        public ModSystem GetModSystem(string fullName) => null;
        public T GetModSystem<T>(bool withInheritance = true) where T : ModSystem => null;
        public bool IsModSystemEnabled(string fullName) => false;
    }

    private sealed class FakeMod : Mod
    {
        public FakeMod(string modId, string name, string version)
        {
            var info = new ModInfo
            {
                ModID = modId,
                Name = name,
                Version = version,
            };
            typeof(Mod).GetProperty(nameof(Info), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(this, info);
        }
    }
}
}

// Source: Optimum.Tests/ModCompatibilityGuardTests.cs
namespace Optimum.Tests
{
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Intrinsics;
using System.Text.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Xunit;

[Collection("OptimumConfig")]
public sealed class ModCompatibilityGuardTests : IDisposable
{
    public ModCompatibilityGuardTests()
    {
        OptimumCompatibilityGuard.ResetForTests();
    }

    public void Dispose()
    {
        OptimumCompatibilityGuard.ResetForTests();
    }

    [Fact]
    public void DefaultState_NoPerformanceModsDetected()
    {
        Assert.False(OptimumConfig.KometDetected);
        Assert.False(OptimumConfig.OptiTimeDetected);
        Assert.False(OptimumConfig.TungstenDetected);
        Assert.False(OptimumConfig.SynergyDetected);
        Assert.True(OptimumConfig.KometGuardEnabled);

        Assert.Equal("none detected", OptimumCompatibilityGuard.GetKometStatusLine());
        Assert.Equal("none detected", OptimumCompatibilityGuard.GetOptiTimeStatusLine());
        Assert.Equal("none detected", OptimumCompatibilityGuard.GetTungstenStatusLine());
        Assert.Equal("none detected", OptimumCompatibilityGuard.GetSynergyStatusLine());

        string summary = OptimumCompatibilityGuard.GetPerformanceModsSummary();
        Assert.Equal("Optimum performance mods: none detected", summary);
    }

    [Fact]
    public void DetectKomet_SetsDetectedAndLogsAdvisory()
    {
        var logs = new List<string>();
        var loader = new MultiModLoader(("komet", "Komet", "2.0.0"));

        OptimumCompatibilityGuard.DetectFromModLoader(loader, msg => logs.Add(msg));

        Assert.True(OptimumConfig.KometDetected);
        Assert.Equal("2.0.0", OptimumCompatibilityGuard.KometDetectedVersion);
        Assert.Single(logs);
        Assert.Contains("Komet mod detected", logs[0]);
        Assert.Contains("glMultiDrawElementsIndirect", logs[0]);

        string status = OptimumCompatibilityGuard.GetKometStatusLine();
        Assert.Contains("Komet v2.0.0", status);
        Assert.Contains("ACTIVE (safely yielding MDI/SIMD)", status);
    }

    [Fact]
    public void DetectOptiTime_SetsDetectedAndLogsRedundancyAdvisory()
    {
        var logs = new List<string>();
        var loader = new MultiModLoader(("optitime", "OptiTime", "1.4.2"));

        OptimumCompatibilityGuard.DetectFromModLoader(loader, msg => logs.Add(msg));

        Assert.True(OptimumConfig.OptiTimeDetected);
        Assert.Equal("1.4.2", OptimumCompatibilityGuard.OptiTimeDetectedVersion);
        Assert.Single(logs);
        Assert.Contains("OptiTime mod detected", logs[0]);
        Assert.Contains("redundant", logs[0]);

        string status = OptimumCompatibilityGuard.GetOptiTimeStatusLine();
        Assert.Contains("OptiTime v1.4.2", status);
        Assert.Contains("REDUNDANT", status);
    }

    [Fact]
    public void DetectTungsten_SetsDetectedAndMarksCompatible()
    {
        var logs = new List<string>();
        var loader = new MultiModLoader(("tungsten", "Tungsten", "1.3.7"));

        OptimumCompatibilityGuard.DetectFromModLoader(loader, msg => logs.Add(msg));

        Assert.True(OptimumConfig.TungstenDetected);
        Assert.Equal("1.3.7", OptimumCompatibilityGuard.TungstenDetectedVersion);
        Assert.Empty(logs);

        string status = OptimumCompatibilityGuard.GetTungstenStatusLine();
        Assert.Contains("Tungsten v1.3.7", status);
        Assert.Contains("COMPATIBLE", status);
    }

    [Fact]
    public void DetectSynergy_SetsDetectedAndMarksCompatible()
    {
        var logs = new List<string>();
        var loader = new MultiModLoader(("synergy", "Synergy", "1.1.25"));

        OptimumCompatibilityGuard.DetectFromModLoader(loader, msg => logs.Add(msg));

        Assert.True(OptimumConfig.SynergyDetected);
        Assert.Equal("1.1.25", OptimumCompatibilityGuard.SynergyDetectedVersion);
        Assert.Empty(logs);

        string status = OptimumCompatibilityGuard.GetSynergyStatusLine();
        Assert.Contains("Synergy v1.1.25", status);
        Assert.Contains("COMPATIBLE", status);
    }

    [Fact]
    public void DetectAllFourMods_SimultaneouslyPopulatesAllStates()
    {
        var logs = new List<string>();
        var loader = new MultiModLoader(
            ("komet", "Komet", "2.0.0"),
            ("optitime", "OptiTime", "1.4.0"),
            ("tungsten", "Tungsten", "1.3.7"),
            ("synergy", "Synergy", "1.1.25")
        );

        OptimumCompatibilityGuard.DetectFromModLoader(loader, msg => logs.Add(msg));

        Assert.True(OptimumConfig.KometDetected);
        Assert.True(OptimumConfig.OptiTimeDetected);
        Assert.True(OptimumConfig.TungstenDetected);
        Assert.True(OptimumConfig.SynergyDetected);

        // Komet warning and OptiTime redundancy advisory are both logged
        Assert.Equal(2, logs.Count);

        string report = OptimumCompatibilityGuard.GetPerformanceModsReport();
        Assert.Contains("Komet:    Komet v2.0.0 (ACTIVE (safely yielding MDI/SIMD))", report);
        Assert.Contains("OptiTime: OptiTime v1.4.0 (REDUNDANT - natively integrated in Optimum)", report);
        Assert.Contains("Tungsten: Tungsten v1.3.7 (COMPATIBLE - server-side optimizations active)", report);
        Assert.Contains("Synergy:  Synergy v1.1.25 (COMPATIBLE - client-server synchronization active)", report);

        string summary = OptimumCompatibilityGuard.GetPerformanceModsSummary();
        Assert.Contains("Komet v2.0.0", summary);
        Assert.Contains("OptiTime v1.4.0", summary);
        Assert.Contains("Tungsten v1.3.7", summary);
        Assert.Contains("Synergy v1.1.25", summary);
    }

    [Fact]
    public void KometGuard_YieldsMdiAndSimdCullingSafely()
    {
        bool origSupported = OptimumConfig.IndirectDrawSupported;
        bool origEnabled = OptimumConfig.IndirectDrawEnabled;
        bool origSimd = OptimumConfig.SimdCullingEnabled;

        try
        {
            OptimumConfig.IndirectDrawSupported = true;
            OptimumConfig.IndirectDrawEnabled = true;
            OptimumConfig.SimdCullingEnabled = true;

            // Without Komet: MDI and SIMD are active
            OptimumConfig.KometDetected = false;
            OptimumConfig.KometGuardEnabled = true;
            Assert.True(OptimumConfig.EffectiveIndirectDraw);
            Assert.Equal(Vector128.IsHardwareAccelerated, OptimumConfig.EffectiveSimdCulling);

            // With Komet detected and guard enabled: safely yields
            OptimumConfig.KometDetected = true;
            Assert.False(OptimumConfig.EffectiveIndirectDraw);
            Assert.False(OptimumConfig.EffectiveSimdCulling);

            // With KometGuard disabled by user: forces active
            OptimumConfig.KometGuardEnabled = false;
            Assert.True(OptimumConfig.EffectiveIndirectDraw);
            Assert.Equal(Vector128.IsHardwareAccelerated, OptimumConfig.EffectiveSimdCulling);
        }
        finally
        {
            OptimumConfig.IndirectDrawSupported = origSupported;
            OptimumConfig.IndirectDrawEnabled = origEnabled;
            OptimumConfig.SimdCullingEnabled = origSimd;
        }
    }

    private sealed class MultiModLoader : IModLoader
    {
        private readonly List<Mod> _mods = new();

        public MultiModLoader(params (string id, string name, string version)[] entries)
        {
            foreach (var (id, name, version) in entries)
            {
                var mod = new TestMod(id, name, version);
                _mods.Add(mod);
            }
        }

        public IEnumerable<Mod> Mods => _mods;
        public IEnumerable<ModSystem> Systems => Enumerable.Empty<ModSystem>();

        public Mod GetMod(string modID)
        {
            return _mods.FirstOrDefault(m => string.Equals(m.Info?.ModID, modID, StringComparison.OrdinalIgnoreCase));
        }

        public bool IsModEnabled(string modID)
        {
            return _mods.Any(m => string.Equals(m.Info?.ModID, modID, StringComparison.OrdinalIgnoreCase));
        }

        public ModSystem GetModSystem(string fullName) => null;
        public T GetModSystem<T>(bool withInheritance = true) where T : ModSystem => null;
        public bool IsModSystemEnabled(string fullName) => false;
    }

    private sealed class TestMod : Mod
    {
        public TestMod(string modId, string name, string version)
        {
            var info = new ModInfo
            {
                ModID = modId,
                Name = name,
                Version = version,
            };
            typeof(Mod).GetProperty(nameof(Info), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(this, info);
        }
    }
}
}

// Source: Optimum.Tests/optimum-optitime-guard-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using Xunit;

public sealed class OptimumOptiTimeGuardCoverageTests
{
    [Fact]
    public void GuardMatchesDistributedOptiTimeFileForms()
    {
        string sourcePath = FindRepositoryFile("sources/VintagestoryLib/Optimum/OptimumOptiTimeGuard.cs");
        string source = File.ReadAllText(sourcePath);

        Assert.Contains("Contains(\"optitime\", StringComparison.OrdinalIgnoreCase)", source);
        Assert.Contains("extension.Equals(\".zip\", StringComparison.OrdinalIgnoreCase)", source);
        Assert.Contains("extension.Equals(\".dll\", StringComparison.OrdinalIgnoreCase)", source);
        Assert.Contains("extension.Equals(\".cs\", StringComparison.OrdinalIgnoreCase)", source);
        Assert.Contains(".disabled-by-optimum", source);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(relativePath);
    }
}
}
