using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Vintagestory.API.Config;

/// <summary>
/// Issue #85: Unified compatibility posture, host detection, and defensive guards
/// for performance mods in the Vintage Story ecosystem:
/// 1. Komet (komet): Third-party client rendering mod (xToast). Overrides host .NET 10
///    MDI indirect draw and SIMD culling via cancelling Harmony prefixes. Yields safely
///    under KometGuard toggle to prevent graphics pipeline crashes.
/// 2. OptiTime (optitime): Client optimization mod (Zaldaryon). Optimum natively integrates
///    all OptiTime optimizations ahead-of-time in IL and source code.
/// 3. Tungsten (tungsten): Server-side performance optimizations (Zaldaryon). Fully compatible.
/// 4. Synergy (synergy): Coordinated client-server performance, network delta compression,
///    and simulation optimizations (Zaldaryon). Fully compatible.
/// </summary>
public static class OptimumCompatibilityGuard
{
    public const string KometModId = "komet";
    public const string OptiTimeModId = "optitime";
    public const string TungstenModId = "tungsten";
    public const string SynergyModId = "synergy";

    public const string KometAdvisoryWarning =
        "[Optimum] Advisory: Komet mod detected. Komet is a third-party guest mod engineered for vanilla Vintage Story. " +
        "Its Harmony prefixes override Optimum's native .NET 10 render pipeline (including glMultiDrawElementsIndirect " +
        "and SIMD frustum culling), which degrades performance and may cause instability. Running both simultaneously is not recommended.";

    public const string KometPlayerJoinNotification =
        "[Optimum] Advisory: Komet mod detected. Komet overrides Optimum's native GPU indirect draw and SIMD culling pipelines. " +
        "Type '.optimum status' for details.";

    public const string OptiTimeAdvisoryWarning =
        "[Optimum] Advisory: OptiTime mod detected. Optimum natively integrates all OptiTime optimizations into the host client engine. " +
        "Running the standalone OptiTime mod on Optimum is redundant.";

    public static string? KometDetectedVersion { get; private set; }
    public static string? KometDetectionSource { get; private set; }
    public static string? OptiTimeDetectedVersion { get; private set; }
    public static string? TungstenDetectedVersion { get; private set; }
    public static string? SynergyDetectedVersion { get; private set; }

    public static bool KometAdvisoryLogged { get; private set; }
    public static bool OptiTimeAdvisoryLogged { get; private set; }
    public static bool WorldJoinAdvisorySent { get; private set; }

    /// <summary>
    /// Detects all 4 performance mods via ModLoader.
    /// </summary>
    public static void DetectFromModLoader(IModLoader? modLoader, Action<string>? logger = null)
    {
        if (modLoader == null) return;

        try
        {
            // 1. Komet
            if (modLoader.IsModEnabled(KometModId))
            {
                OptimumConfig.KometDetected = true;
                KometDetectionSource = "ModLoader";
                var mod = modLoader.GetMod(KometModId);
                KometDetectedVersion = mod?.Info?.Version ?? "unknown";
                LogKometAdvisoryOnce(logger);
            }

            // 2. OptiTime
            if (modLoader.IsModEnabled(OptiTimeModId))
            {
                OptimumConfig.OptiTimeDetected = true;
                var mod = modLoader.GetMod(OptiTimeModId);
                OptiTimeDetectedVersion = mod?.Info?.Version ?? "unknown";
                LogOptiTimeAdvisoryOnce(logger);
            }

            // 3. Tungsten
            if (modLoader.IsModEnabled(TungstenModId))
            {
                OptimumConfig.TungstenDetected = true;
                var mod = modLoader.GetMod(TungstenModId);
                TungstenDetectedVersion = mod?.Info?.Version ?? "unknown";
            }

            // 4. Synergy
            if (modLoader.IsModEnabled(SynergyModId))
            {
                OptimumConfig.SynergyDetected = true;
                var mod = modLoader.GetMod(SynergyModId);
                SynergyDetectedVersion = mod?.Info?.Version ?? "unknown";
            }

            // Fallback scan across all mods in case of casing differences
            if (modLoader.Mods != null)
            {
                foreach (var mod in modLoader.Mods)
                {
                    if (mod?.Info == null) continue;
                    string modId = mod.Info.ModID ?? "";
                    string name = mod.Info.Name ?? "";

                    if (!OptimumConfig.KometDetected && (string.Equals(modId, KometModId, StringComparison.OrdinalIgnoreCase) ||
                        name.IndexOf("komet", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        OptimumConfig.KometDetected = true;
                        KometDetectionSource = "ModLoader";
                        KometDetectedVersion = mod.Info.Version ?? "unknown";
                        LogKometAdvisoryOnce(logger);
                    }

                    if (!OptimumConfig.OptiTimeDetected && (string.Equals(modId, OptiTimeModId, StringComparison.OrdinalIgnoreCase) ||
                        name.IndexOf("optitime", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        OptimumConfig.OptiTimeDetected = true;
                        OptiTimeDetectedVersion = mod.Info.Version ?? "unknown";
                        LogOptiTimeAdvisoryOnce(logger);
                    }

                    if (!OptimumConfig.TungstenDetected && (string.Equals(modId, TungstenModId, StringComparison.OrdinalIgnoreCase) ||
                        name.IndexOf("tungsten", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        OptimumConfig.TungstenDetected = true;
                        TungstenDetectedVersion = mod.Info.Version ?? "unknown";
                    }

                    if (!OptimumConfig.SynergyDetected && (string.Equals(modId, SynergyModId, StringComparison.OrdinalIgnoreCase) ||
                        name.IndexOf("synergy", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        OptimumConfig.SynergyDetected = true;
                        SynergyDetectedVersion = mod.Info.Version ?? "unknown";
                    }
                }
            }
        }
        catch (Exception)
        {
            // Defensive: detection must never crash game startup
        }
    }

    /// <summary>
    /// Scans loaded assemblies in the AppDomain as a fallback detector.
    /// </summary>
    public static void DetectFromAssemblies(Action<string>? logger = null)
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string? name = asm.GetName().Name;
                if (name == null) continue;

                if (!OptimumConfig.KometDetected &&
                    (string.Equals(name, KometModId, StringComparison.OrdinalIgnoreCase) || asm.GetType("Komet.KometModSystem") != null))
                {
                    OptimumConfig.KometDetected = true;
                    KometDetectionSource = "AppDomain";
                    KometDetectedVersion = asm.GetName().Version?.ToString() ?? "unknown";
                    LogKometAdvisoryOnce(logger);
                }

                if (!OptimumConfig.OptiTimeDetected &&
                    string.Equals(name, OptiTimeModId, StringComparison.OrdinalIgnoreCase))
                {
                    OptimumConfig.OptiTimeDetected = true;
                    OptiTimeDetectedVersion = asm.GetName().Version?.ToString() ?? "unknown";
                    LogOptiTimeAdvisoryOnce(logger);
                }

                if (!OptimumConfig.TungstenDetected &&
                    string.Equals(name, TungstenModId, StringComparison.OrdinalIgnoreCase))
                {
                    OptimumConfig.TungstenDetected = true;
                    TungstenDetectedVersion = asm.GetName().Version?.ToString() ?? "unknown";
                }

                if (!OptimumConfig.SynergyDetected &&
                    string.Equals(name, SynergyModId, StringComparison.OrdinalIgnoreCase))
                {
                    OptimumConfig.SynergyDetected = true;
                    SynergyDetectedVersion = asm.GetName().Version?.ToString() ?? "unknown";
                }
            }
        }
        catch (Exception)
        {
            // Defensive: scan must never crash startup
        }
    }

    /// <summary>
    /// Runs full detection pipeline against the game API.
    /// </summary>
    public static void RunDetection(ICoreAPI? api)
    {
        Action<string>? logger = api?.Logger != null ? msg => api.Logger.Warning(msg) : null;
        DetectFromModLoader(api?.ModLoader, logger);
        DetectFromAssemblies(logger);
    }

    /// <summary>
    /// Emits in-game chat advisory when player joins the world.
    /// </summary>
    public static void NotifyPlayerOnJoin(ICoreClientAPI? api)
    {
        if (api == null) return;
        NotifyPlayerOnJoin(api.ShowChatMessage);
    }

    /// <summary>
    /// Emits in-game chat advisory to the provided sink when player joins the world.
    /// </summary>
    public static void NotifyPlayerOnJoin(Action<string>? showChat)
    {
        if (showChat == null) return;
        if (OptimumConfig.KometDetected && OptimumConfig.KometGuardEnabled && !WorldJoinAdvisorySent)
        {
            showChat(KometPlayerJoinNotification);
            WorldJoinAdvisorySent = true;
        }
    }

    public static string GetKometStatusLine()
    {
        if (!OptimumConfig.KometDetected) return "none detected";
        string ver = !string.IsNullOrEmpty(KometDetectedVersion) ? $" v{KometDetectedVersion}" : "";
        string guardState = OptimumConfig.KometGuardEnabled ? "ACTIVE (safely yielding MDI/SIMD)" : "UNGUARDED (MDI/SIMD force-enabled)";
        return $"Komet{ver} ({guardState})";
    }

    public static string GetOptiTimeStatusLine()
    {
        if (!OptimumConfig.OptiTimeDetected) return "none detected";
        string ver = !string.IsNullOrEmpty(OptiTimeDetectedVersion) ? $" v{OptiTimeDetectedVersion}" : "";
        return $"OptiTime{ver} (REDUNDANT - natively integrated in Optimum)";
    }

    public static string GetTungstenStatusLine()
    {
        if (!OptimumConfig.TungstenDetected) return "none detected";
        string ver = !string.IsNullOrEmpty(TungstenDetectedVersion) ? $" v{TungstenDetectedVersion}" : "";
        return $"Tungsten{ver} (COMPATIBLE - server-side optimizations active)";
    }

    public static string GetSynergyStatusLine()
    {
        if (!OptimumConfig.SynergyDetected) return "none detected";
        string ver = !string.IsNullOrEmpty(SynergyDetectedVersion) ? $" v{SynergyDetectedVersion}" : "";
        return $"Synergy{ver} (COMPATIBLE - client-server synchronization active)";
    }

    /// <summary>
    /// Multi-line report for .optimum status command.
    /// </summary>
    public static string GetPerformanceModsReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("  ecosystem performance mods:");
        sb.AppendLine($"    Komet:    {GetKometStatusLine()}");
        sb.AppendLine($"    OptiTime: {GetOptiTimeStatusLine()}");
        sb.AppendLine($"    Tungsten: {GetTungstenStatusLine()}");
        sb.Append($"    Synergy:  {GetSynergyStatusLine()}");
        return sb.ToString();
    }

    /// <summary>
    /// Single-line summary for diagnostics.
    /// </summary>
    public static string GetPerformanceModsSummary()
    {
        var active = new List<string>();
        if (OptimumConfig.KometDetected) active.Add(GetKometStatusLine());
        if (OptimumConfig.OptiTimeDetected) active.Add(GetOptiTimeStatusLine());
        if (OptimumConfig.TungstenDetected) active.Add(GetTungstenStatusLine());
        if (OptimumConfig.SynergyDetected) active.Add(GetSynergyStatusLine());

        if (active.Count == 0) return "Optimum performance mods: none detected";
        return "Optimum performance mods: " + string.Join("; ", active);
    }

    public static void ResetSession()
    {
        WorldJoinAdvisorySent = false;
    }

    public static void ResetForTests()
    {
        OptimumConfig.KometDetected = false;
        OptimumConfig.KometGuardEnabled = true;
        OptimumConfig.OptiTimeDetected = false;
        OptimumConfig.TungstenDetected = false;
        OptimumConfig.SynergyDetected = false;
        KometDetectedVersion = null;
        KometDetectionSource = null;
        OptiTimeDetectedVersion = null;
        TungstenDetectedVersion = null;
        SynergyDetectedVersion = null;
        KometAdvisoryLogged = false;
        OptiTimeAdvisoryLogged = false;
        WorldJoinAdvisorySent = false;
    }

    private static void LogKometAdvisoryOnce(Action<string>? logger)
    {
        if (logger != null && !KometAdvisoryLogged)
        {
            logger(KometAdvisoryWarning);
            KometAdvisoryLogged = true;
        }
    }

    private static void LogOptiTimeAdvisoryOnce(Action<string>? logger)
    {
        if (logger != null && !OptiTimeAdvisoryLogged)
        {
            logger(OptiTimeAdvisoryWarning);
            OptiTimeAdvisoryLogged = true;
        }
    }
}
