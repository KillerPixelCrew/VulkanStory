using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using VulkanStory.Input;

namespace VulkanStory.Input.Companion;

public sealed class AnalogInputCompanion : ModSystem
{
    private const string HarmonyOwner = "vulkanstory.input.companion.physics";
    private ICoreServerAPI? api;
    private Harmony? harmony;
    private readonly List<WeakReference<AnalogPlayerBehavior>> attached = new();
    private static ConditionalWeakTable<EntityControls, AnalogPlayerBehavior> controls = new();
    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;
    public override void StartServerSide(ICoreServerAPI server)
    {
        api = server;
        var target = AccessTools.Method(typeof(EntityControls), nameof(EntityControls.CalcMovementVectors), [typeof(EntityPos), typeof(float)]);
        if (target == null || !target.IsVirtual || target.GetMethodBody() == null)
            throw new InvalidOperationException("Original server movement-vector method changed.");
        harmony = new Harmony(HarmonyOwner);
        try
        {
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(AnalogInputCompanion), nameof(PrepareSpeed)),
                postfix: new HarmonyMethod(typeof(AnalogInputCompanion), nameof(ApplyDirection)) { priority = Priority.Last });
            server.Event.PlayerNowPlaying += Attach;
            server.Event.PlayerRespawn += Attach;
            server.Event.PlayerDisconnect += Disconnect;
            foreach (IServerPlayer player in server.World.AllOnlinePlayers.OfType<IServerPlayer>()) Attach(player);
        }
        catch { Dispose(); throw; }
    }
    private void Attach(IServerPlayer player)
    {
        if (api == null || player.Entity == null) return;
        if (player.Entity.GetBehavior<AnalogPlayerBehavior>() is { } existing) { existing.ClearInput(); return; }
        var behavior = new AnalogPlayerBehavior(player.Entity, api, player);
        player.Entity.AddBehavior(behavior);
        attached.RemoveAll(reference => !reference.TryGetTarget(out _));
        attached.Add(new WeakReference<AnalogPlayerBehavior>(behavior));
    }
    private void Disconnect(IServerPlayer player) => player.Entity?.GetBehavior<AnalogPlayerBehavior>()?.ResetConnection();
    internal static void Bind(EntityControls value, AnalogPlayerBehavior behavior)
    {
        controls.Remove(value); controls.Add(value, behavior);
    }
    internal static void Unbind(EntityControls value) => controls.Remove(value);
    private static void PrepareSpeed(EntityControls __instance)
    { if (controls.TryGetValue(__instance, out var owner)) owner.PrepareSpeed(); }
    private static void ApplyDirection(EntityControls __instance, EntityPos __0, float __1)
    {
        if (controls.TryGetValue(__instance, out _)) AnalogMovement.ApplyDirection(__instance, __0, __1);
    }
    public override void Dispose()
    {
        if (api != null)
        {
            api.Event.PlayerNowPlaying -= Attach; api.Event.PlayerRespawn -= Attach; api.Event.PlayerDisconnect -= Disconnect;
        }
        foreach (var reference in attached)
            if (reference.TryGetTarget(out var behavior))
            {
                behavior.ClearInput(); behavior.entity.RemoveBehavior(behavior);
            }
        attached.Clear();
        harmony?.UnpatchAll(HarmonyOwner); harmony = null; api = null;
        controls = new();
        base.Dispose();
    }
}

internal sealed class AnalogPlayerBehavior(Entity entity, ICoreServerAPI api, IServerPlayer player) : EntityBehavior(entity)
{
    private bool negotiated, hasInput;
    private float factor = 1f;
    private long expires;
    private EntityControls? bound;
    public override string PropertyName() => "vulkanstoryanalog";
    public override void OnReceivedClientPacket(IServerPlayer sender, int packetid, byte[] data, ref EnumHandling handled)
    {
        if (packetid is not (AnalogMovement.ProbePacketId or AnalogMovement.PacketId)) return;
        handled = EnumHandling.PreventSubsequent;
        // Entity packets are dispatched to their named entity by the original
        // server. Accept only this connection's actual player entity.
        if (!ReferenceEquals(sender.Entity, entity) || sender.PlayerUID != player.PlayerUID) return;
        if (packetid == AnalogMovement.ProbePacketId)
        {
            if (data is not { Length: 1 } || data[0] != AnalogMovement.ProtocolVersion) return;
            negotiated = true;
            api.Network.SendEntityPacket(sender, entity.EntityId, AnalogMovement.AckPacketId, [AnalogMovement.ProtocolVersion]);
            return;
        }
        if (!negotiated || data == null || (data.Length != 1 && data.Length != 3) || entity is not EntityPlayer current) return;
        EntityControls value = current.Controls;
        if (bound != null && !ReferenceEquals(bound, value)) ClearInput();
        bound = value; factor = AnalogMovement.Decode(data[0]);
        expires = Environment.TickCount64 + 600; hasInput = true;
        AnalogInputCompanion.Bind(value, this);
        AnalogMovement.SetAxes(value, data.Length == 3 ? AnalogMovement.DecodeAxis(data[1]) : 0f,
            data.Length == 3 ? AnalogMovement.DecodeAxis(data[2]) : 0f);
        value.MovespeedMultiplier = sender.WorldData.MoveSpeedMultiplier * factor;
        value.Dirty = true;
    }
    internal void PrepareSpeed()
    {
        if (!hasInput || bound == null) return;
        if (Environment.TickCount64 > expires) { ClearInput(); return; }
        bound.MovespeedMultiplier = player.WorldData.MoveSpeedMultiplier * factor;
    }
    public override void OnGameTick(float deltaTime)
    { if (hasInput && Environment.TickCount64 > expires) ClearInput(); }
    public override void OnEntityDespawn(EntityDespawnData despawn)
    { ClearInput(); negotiated = false; base.OnEntityDespawn(despawn); }
    internal void ClearInput()
    {
        if (bound != null)
        {
            AnalogMovement.ClearAxes(bound);
            bound.MovespeedMultiplier = player.WorldData.MoveSpeedMultiplier;
            AnalogInputCompanion.Unbind(bound); bound = null;
        }
        hasInput = false; factor = 1f; expires = 0;
    }
    internal void ResetConnection() { ClearInput(); negotiated = false; }
}
