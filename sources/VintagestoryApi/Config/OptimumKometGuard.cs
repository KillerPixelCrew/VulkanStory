using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Vintagestory.API.Config;

/// <summary>
/// Issue #85: Compatibility wrapper for Komet-specific guard queries.
/// Delegates to OptimumCompatibilityGuard.
/// </summary>
public static class OptimumKometGuard
{
    public const string ModId = OptimumCompatibilityGuard.KometModId;
    public const string AdvisoryWarning = OptimumCompatibilityGuard.KometAdvisoryWarning;
    public const string PlayerJoinNotification = OptimumCompatibilityGuard.KometPlayerJoinNotification;

    public static bool IsDetected => OptimumConfig.KometDetected;
    public static string? DetectionSource => OptimumCompatibilityGuard.KometDetectionSource;
    public static string? DetectedVersion => OptimumCompatibilityGuard.KometDetectedVersion;
    public static bool AdvisoryLogged => OptimumCompatibilityGuard.KometAdvisoryLogged;
    public static bool WorldJoinAdvisorySent => OptimumCompatibilityGuard.WorldJoinAdvisorySent;

    public static bool Detect(IModLoader? modLoader, Action<string>? logger = null)
    {
        OptimumCompatibilityGuard.DetectFromModLoader(modLoader, logger);
        return OptimumConfig.KometDetected;
    }

    public static bool DetectFromAssemblies(Action<string>? logger = null)
    {
        OptimumCompatibilityGuard.DetectFromAssemblies(logger);
        return OptimumConfig.KometDetected;
    }

    public static bool RunDetection(ICoreAPI? api)
    {
        OptimumCompatibilityGuard.RunDetection(api);
        return OptimumConfig.KometDetected;
    }

    public static void NotifyPlayerOnJoin(ICoreClientAPI? api) => OptimumCompatibilityGuard.NotifyPlayerOnJoin(api);
    public static void NotifyPlayerOnJoin(Action<string>? showChat) => OptimumCompatibilityGuard.NotifyPlayerOnJoin(showChat);
    public static string GetStatusLine() => OptimumCompatibilityGuard.GetKometStatusLine();
    public static void ResetSession() => OptimumCompatibilityGuard.ResetSession();
    public static void ResetForTests() => OptimumCompatibilityGuard.ResetForTests();
}
