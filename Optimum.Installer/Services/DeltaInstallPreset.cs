using System.Text.Json;

namespace Optimum.Installer.Services;

public enum DeltaInstallPreset
{
    ExistingSettings,
    Desktop,
    Handheld,
}

internal static class DeltaPresetSettings
{
    internal static bool IsEmptyDataPath(string path) =>
        Path.IsPathFullyQualified(path) && !File.Exists(path) &&
        (!Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any());

    internal static async Task<string> SeedAsync(string dataPath, DeltaInstallPreset preset, CancellationToken token)
    {
        if (preset == DeltaInstallPreset.ExistingSettings)
            throw new ArgumentException("A preset is required.", nameof(preset));
        if (!IsEmptyDataPath(dataPath))
            throw new InvalidDataException("Presets need a new or empty separate data folder.");

        string configDirectory = Path.Combine(dataPath, "ModConfig");
        Directory.CreateDirectory(configDirectory);
        string configPath = Path.Combine(configDirectory, "optimum.json");
        string clientSettingsPath = Path.Combine(dataPath, "clientsettings.json");
        object values = preset switch
        {
            DeltaInstallPreset.Desktop => new
            {
                Renderer = "vulkan", Taa = true, Upscaler = "off", RenderScale = 1.0f,
            },
            DeltaInstallPreset.Handheld => new
            {
                Renderer = "vulkan", Taa = false, Upscaler = "xess", UpscalerQuality = "quality",
                RenderScale = 0.75f, GodRaysSampleCap = true, AmbientOcclusionPreset = "low",
                GreedyMeshEnabled = true, GreedyMeshLightTolerance = 1,
                GreedyMeshFarDistance = 128,
                AvoidForcedGc = true,
                IdleThreadWait = true,
                HighResolutionFrameWait = true,
                HandheldShadowTier = true,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };
        bool createdConfig = false;
        bool createdClientSettings = false;
        try
        {
            if (preset == DeltaInstallPreset.Handheld)
            {
                // ClientSettings loads these dictionaries over the game's defaults.
                // Keep the remaining client settings under game ownership.
                object clientSettings = new
                {
                    intSettings = new
                    {
                        maxAsyncQuadParticles = 16000,
                        maxAsyncCubeParticles = 16000,
                        particleLevel = 60,
                        mipmapLevel = 4,
                        cloudRenderMode = 2,
                        viewDistance = 160,
                        maxFps = 60,
                        vsyncMode = 2,
                        shadowMapQuality = 2,
                    },
                    floatSettings = new { lodBias = 0.25f, lodBiasFar = 0.55f },
                    boolSettings = new { showMoreGfxOptions = true },
                };
                await using var clientStream = new FileStream(clientSettingsPath,
                    FileMode.CreateNew, FileAccess.Write, FileShare.None);
                createdClientSettings = true;
                await JsonSerializer.SerializeAsync(clientStream, clientSettings, cancellationToken: token);
            }
            await using var stream = new FileStream(configPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            createdConfig = true;
            await JsonSerializer.SerializeAsync(stream, values, cancellationToken: token);
            return configPath;
        }
        catch
        {
            if (createdConfig) File.Delete(configPath);
            if (createdClientSettings) File.Delete(clientSettingsPath);
            if (!Directory.EnumerateFileSystemEntries(configDirectory).Any()) Directory.Delete(configDirectory);
            throw;
        }
    }
}
