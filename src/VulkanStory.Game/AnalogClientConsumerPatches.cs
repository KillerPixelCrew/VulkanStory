using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using OpenTK.Mathematics;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;
using VulkanStory.Input;

namespace VulkanStory.Game;

/// <summary>Negotiates controller analog movement with the optional server companion and applies acknowledged axes to original client controls.</summary>
/// <remarks>Patch discovery and installation belong to startup. Callbacks use the committed routing predicate; game object identity remains in the integration assembly.</remarks>
internal static class AnalogClientConsumerPatches
{
    private const string Owner = "vulkanstory.routing.controller-analog-client";
    private static ProcessRuntime? runtime;
    private static ConditionalWeakTable<SystemPlayerControl, ClientState> states = new();
    private static readonly AccessTools.FieldRef<ClientSystem, ClientMain> Game = AccessTools.FieldRefAccess<ClientSystem, ClientMain>("game");
    private static readonly AccessTools.FieldRef<SystemPlayerControl, int> Forward = AccessTools.FieldRefAccess<SystemPlayerControl, int>("forwardKey");
    private static readonly AccessTools.FieldRef<SystemPlayerControl, int> Back = AccessTools.FieldRefAccess<SystemPlayerControl, int>("backwardKey");
    private static readonly AccessTools.FieldRef<SystemPlayerControl, int> Left = AccessTools.FieldRefAccess<SystemPlayerControl, int>("leftKey");
    private static readonly AccessTools.FieldRef<SystemPlayerControl, int> Right = AccessTools.FieldRefAccess<SystemPlayerControl, int>("rightKey");
    private static readonly FieldInfo Speed = typeof(EntityControls).GetField(nameof(EntityControls.MovespeedMultiplier))!;
    private static readonly MethodInfo Tick = AccessTools.Method(typeof(SystemPlayerControl), "OnGameTick", [typeof(float)])!;
    private static readonly MethodInfo Send = AccessTools.Method(typeof(SystemPlayerControl), "SendServerPackets", [typeof(EntityControls), typeof(EntityControls)])!;
    private static readonly MethodInfo Receive = AccessTools.Method(typeof(ClientSystemEntities), "HandleEntityPacket", [typeof(Packet_Server)])!;
    private sealed class ClientState
    {
        internal long Player = long.MinValue, NextProbe, NextRefresh;
        internal int Attempts, FactorCode, XCode, YCode;
        internal float BaseSpeed, Factor = 1f, PreviousBaseSpeed;
        internal Vector2 Axes;
        internal EntityControls? Controls;
    }
    private static void Check(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList();
        if (body.Count(instruction => instruction.opcode == OpCodes.Stfld && Equals(instruction.operand, Speed)) != 1 ||
            body.Count(instruction => instruction.Calls(Send)) != 1)
            throw new InvalidOperationException("Original/incoming analog control anchors changed.");
    }
    /// <summary>Creates the dormant patch group for this consumer path; validation and installation remain separate transaction steps.</summary>
    /// <param name="owner">Process runtime that owns this group and its session.</param>
    /// <returns>Validation, installation and removal callbacks for the startup transaction.</returns>
    /// <remarks>Binding or IL-anchor mismatches reject the group. Creating the group does not enable graphics routing.</remarks>
    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner)
    {
        var harmony = new Harmony(Owner); bool attempted = false;
        return new StartupPatchGroup("graphics-controller-analog-client", () =>
        {
            foreach (var method in new[] { Tick, Send, Receive })
                if (method == null || method.ReturnType != typeof(void) || method.GetMethodBody() == null)
                    throw new InvalidOperationException("Original analog client lifecycle changed.");
            Check(PatchProcessor.GetOriginalInstructions(Tick));
        }, () =>
        {
            if (runtime != null) throw new InvalidOperationException("Analog client already has a routing owner.");
            runtime = owner; attempted = true;
            harmony.Patch(Tick, transpiler: new HarmonyMethod(typeof(AnalogClientConsumerPatches), nameof(Transpiler)) { priority = Priority.First });
            harmony.Patch(Receive, prefix: new HarmonyMethod(typeof(AnalogClientConsumerPatches), nameof(Acknowledge)) { priority = Priority.First });
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner); if (ReferenceEquals(runtime, owner)) { runtime = null; states = new(); }
            attempted = false;
        });
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList(); Check(body);
        foreach (var instruction in body)
        {
            if (instruction.opcode == OpCodes.Stfld && Equals(instruction.operand, Speed))
            {
                var receiver = new CodeInstruction(OpCodes.Ldarg_0);
                receiver.labels.AddRange(instruction.labels); instruction.labels.Clear();
                receiver.blocks.AddRange(instruction.blocks.Where(block => block.blockType != ExceptionBlockType.EndExceptionBlock));
                instruction.blocks.RemoveAll(block => block.blockType != ExceptionBlockType.EndExceptionBlock);
                yield return receiver;
                instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(AnalogClientConsumerPatches), nameof(ApplyControl));
                yield return instruction;
                continue;
            }
            yield return instruction;
            if (instruction.Calls(Send))
            {
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(AnalogClientConsumerPatches), nameof(SendAnalog)));
            }
        }
    }
    private static GameRenderSession? Session(ClientMain client)
    {
        if (runtime?.Routing.RoutingEnabled != true) return null;
        if (client.Platform is not ClientPlatformWindows platform || !runtime.TrySession(platform, out var session))
            throw new InvalidOperationException("Active analog client lost its session.");
        return session;
    }
    private static void ApplyControl(EntityControls controls, float baseSpeed, SystemPlayerControl owner)
    {
        ClientMain client = Game(owner);
        if (Session(client) is not { } session || client.EntityPlayer == null)
        { controls.MovespeedMultiplier = baseSpeed; return; }
        ClientState state = states.GetValue(owner, _ => new ClientState());
        long player = client.EntityPlayer.EntityId;
        if (state.Player != player)
        {
            if (state.Controls != null) AnalogMovement.ClearAxes(state.Controls);
            state.Player = player; state.Attempts = state.FactorCode = state.XCode = state.YCode = 0;
            state.NextProbe = state.NextRefresh = 0;
        }
        var movement = session.ControllerMovement;
        bool ready = movement.IsAcknowledged(client, player);
        if (!ready && state.Attempts < 3 && client.ElapsedMilliseconds >= state.NextProbe)
        {
            movement.BeginProbe(client, player);
            client.SendEntityPacket(player, AnalogMovement.ProbePacketId, [AnalogMovement.ProtocolVersion]);
            state.Attempts++; state.NextProbe = client.ElapsedMilliseconds + 2000;
        }
        bool physical = session.Input.Input.PhysicalMovementHeld(Forward(owner), Back(owner), Left(owner), Right(owner));
        state.Axes = ready && client.EntityPlayer.MountedOn == null && !physical ? movement.Axes : Vector2.Zero;
        state.Factor = AnalogMovement.EffectiveFactor(movement.Factor, physical, ready);
        if (state.Controls != null && !ReferenceEquals(state.Controls, controls)) AnalogMovement.ClearAxes(state.Controls);
        state.Controls = controls; state.BaseSpeed = baseSpeed;
        AnalogMovement.SetAxes(controls, state.Axes.X, state.Axes.Y);
        controls.MovespeedMultiplier = baseSpeed * state.Factor;
    }
    private static void SendAnalog(SystemPlayerControl owner)
    {
        ClientMain client = Game(owner);
        if (Session(client) is not { } session || !states.TryGetValue(owner, out var state) ||
            !session.ControllerMovement.IsAcknowledged(client, state.Player)) return;
        byte factor = AnalogMovement.Encode(state.Factor), x = AnalogMovement.EncodeAxis(state.Axes.X), y = AnalogMovement.EncodeAxis(state.Axes.Y);
        if (state.FactorCode == factor + 1 && state.XCode == x + 1 && state.YCode == y + 1 &&
            state.PreviousBaseSpeed == state.BaseSpeed && ((x == 0 && y == 0) || client.ElapsedMilliseconds < state.NextRefresh)) return;
        client.SendEntityPacket(state.Player, AnalogMovement.PacketId, [factor, x, y]);
        state.FactorCode = factor + 1; state.XCode = x + 1; state.YCode = y + 1;
        state.PreviousBaseSpeed = state.BaseSpeed; state.NextRefresh = client.ElapsedMilliseconds + 250;
    }
    private static bool Acknowledge(ClientSystemEntities __instance, Packet_Server __0)
    {
        if (runtime?.Routing.RoutingEnabled != true || __0.EntityPacket.Packetid != AnalogMovement.AckPacketId) return true;
        ClientMain client = Game(__instance); var packet = __0.EntityPacket;
        if (client.EntityPlayer != null && packet.EntityId == client.EntityPlayer.EntityId &&
            packet.Data is { Length: 1 } && packet.Data[0] == AnalogMovement.ProtocolVersion && Session(client) is { } session)
            session.ControllerMovement.Acknowledge(client, packet.EntityId);
        return false;
    }
    internal static void ClientLeaving(ClientMain client)
    {
        if (Session(client) is not { } session) return;
        if (client.EntityPlayer is { } player)
        {
            AnalogMovement.ClearAxes(player.Controls);
            if (player.MountedOn?.Controls is { } mounted) AnalogMovement.ClearAxes(mounted);
        }
        session.ControllerMovement.Reset();
    }
}
