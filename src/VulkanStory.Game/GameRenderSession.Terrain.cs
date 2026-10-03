using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    private static readonly AccessTools.FieldRef<ClientMain, ChunkRenderer> TerrainChunks =
        AccessTools.FieldRefAccess<ClientMain, ChunkRenderer>("chunkRenderer");
    private static readonly AccessTools.FieldRef<ClientMain, ClientEventManager> TerrainEvents =
        AccessTools.FieldRefAccess<ClientMain, ClientEventManager>("eventManager");
    private bool TerrainShadersReady() => graphics?.TerrainShadersReady == true;
    private void ApplyTerrainLodBias(float providerBias)
    {
        RequireOwner();
        // The shared settings state owns the active plan; it also supplies the
        // ordinary scale/TAA bias when that plan is retired or unavailable.
        if (!float.IsFinite(providerBias)) throw new ArgumentOutOfRangeException(nameof(providerBias));
        if (temporal?.CurrentClient is { } client) RefreshTerrainLodBias(client);
    }
    internal void RefreshTerrainLodBias(ClientMain client)
    {
        RequireOwner();
        if (!ReferenceEquals(client.Platform, platform)) throw new InvalidOperationException("Terrain belongs to another platform.");
        if (graphics == null || TerrainChunks(client) is not { } chunks) return;
        graphics.ApplyTerrainLodBias(chunks.textureIds, services.RendererSettings.EffectiveTerrainLodBias);
    }
    private void ReloadTerrainShaders()
    {
        RequireOwner();
        bool ready = ShaderRegistry.ReloadShaders();
        if (Temporal.CurrentClient is { } client)
        {
            if (TerrainEvents(client) is { } events) ready = events.TriggerReloadShaders() & ready;
            RefreshTerrainLodBias(client);
        }
        if (!ready)
        {
            Temporal.SetMotionShaderMode(false);
            Temporal.PublishMotionCoverage(false);
            Graphics.SetAmbientOcclusionShaderMode(false);
            platform.Logger.Warning("VulkanStory: shader reload failed; compiled temporal/AO modes remain disabled.");
        }
    }
}
