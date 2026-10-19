using System.Numerics;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.Client.NoObf;
using VulkanStory.Contracts;

namespace VulkanStory.Game.Performance;

internal static partial class GpuParticleSimulation
{
    private const int MaximumWorldBoxes = 262144;
    private const int MaximumLiquidCells = 262144, MaximumLiquidRegions = 512;
    private static readonly AccessTools.FieldRef<Vintagestory.API.Datastructures.CachedCuboidList, int> PopulatedSize =
        AccessTools.FieldRefAccess<Vintagestory.API.Datastructures.CachedCuboidList, int>("populatedSize");
    private readonly record struct WorldRegion(IBlockAccessor Accessor, int Dimension, int MinX, int MinY, int MinZ, int MaxX, int MaxY, int MaxZ);
    private static readonly MethodInfo StockBoxes = AccessTools.Method(typeof(Block), nameof(Block.GetParticleCollisionBoxes),
        [typeof(IBlockAccessor), typeof(BlockPos)])!;
    private static readonly MethodInfo StockLiquid = AccessTools.Method(typeof(CollectibleObject), nameof(CollectibleObject.IsLiquid), Type.EmptyTypes)!;
    private static readonly Dictionary<Type, bool> stockBoxTypes = new();
    private static readonly Dictionary<Type, bool> stockFlowTypes = new();
    private static readonly Dictionary<Type, bool> stockLiquidTypes = new();
    internal static bool HasStockLiquid(Block block)
    {
        lock (stockLiquidTypes)
        {
            var type = block.GetType();
            if (!stockLiquidTypes.TryGetValue(type, out bool stock))
                // Inherited methods can have different ReflectedType values; compare the actual declaration.
                stockLiquidTypes[type] = stock = AccessTools.Method(type, nameof(Block.IsLiquid), Type.EmptyTypes)?.DeclaringType == StockLiquid.DeclaringType &&
                    Harmony.GetPatchInfo(StockLiquid)?.Owners.Any(owner => !PerformancePatchSet.IsOwnOwner(owner)) != true;
            return stock;
        }
    }
    private static bool HasStockFlow(Block block)
    {
        lock (stockFlowTypes)
        {
            var type = block.GetType();
            if (stockFlowTypes.TryGetValue(type, out bool supported)) return supported;
            var map = type.GetInterfaceMap(typeof(IBlockFlowing));
            supported = true;
            for (int i = 0; i < map.InterfaceMethods.Length; i++)
            {
                if (map.InterfaceMethods[i].Name is not ("get_IsStill" or "GetPushVector")) continue;
                var method = map.TargetMethods[i];
                supported &= method.DeclaringType == typeof(Vintagestory.GameContent.BlockForFluidsLayer) &&
                    Harmony.GetPatchInfo(method)?.Owners.Any(owner => !PerformancePatchSet.IsOwnOwner(owner)) != true;
            }
            return stockFlowTypes[type] = supported;
        }
    }
    private static bool HasStockBoxes(Block block)
    {
        lock (stockBoxTypes)
        {
            var type = block.GetType();
            if (!stockBoxTypes.TryGetValue(type, out bool stock))
                stockBoxTypes[type] = stock = AccessTools.Method(type, nameof(Block.GetParticleCollisionBoxes),
                    [typeof(IBlockAccessor), typeof(BlockPos)])?.DeclaringType == StockBoxes.DeclaringType;
            return stock;
        }
    }

    // Observe mutable blocks/arrays every submission. The game exposes no coherent
    // world revision that would permit keeping these observations across ticks.
    // Base field-returning boxes are captured directly; custom cells are inert
    // markers until the actual GPU query reaches their ordered CPU boundary.
    // Never call a custom box callback speculatively.
    private static bool CaptureWorld(ParticleGeneric particle, ParticlePhysics physics, PoolState state,
        Batch batch, out ParticleSimulationWorldInput world)
    {
        world = default;
        world.InitialPopulatedSize = (uint)PopulatedSize(physics.CollisionBoxList);
        var pos = particle.Position;
        state.Position.SetAndCorrectDimension((int)pos.X, (int)pos.Y, (int)pos.Z);
        var fluid = physics.BlockAccess.GetBlock(state.Position, 2);
        if (!HasStockLiquid(fluid)) return false;
        bool liquid = fluid.IsLiquid();
        bool aboveLiquid = false;
        if (particle.SwimOnLiquid && liquid)
        {
            var above = physics.BlockAccess.GetBlockAbove(state.Position, 2);
            if (!HasStockLiquid(above)) return false;
            aboveLiquid = above.IsLiquid();
        }
        world.Liquid = new Vector4(liquid ? 1 : 0,
            aboveLiquid ? 1 : 0,
            fluid.LiquidLevel, particle.SwimOnLiquid ? 1 : 0);
        if (fluid is IBlockFlowing flowing)
        {
            // Custom flow callbacks keep the original path and ordering.
            if (!HasStockFlow(fluid)) return false;
            if (!flowing.IsStill)
            {
                var push = flowing.GetPushVector(state.Position);
                if (!float.IsFinite(push.X) || !float.IsFinite(push.Y) || !float.IsFinite(push.Z)) return false;
                world.Flow = new Vector4(push.X, push.Y, push.Z, 1);
            }
        }
        world.MotionCap = physics.MotionCap;
        if (!particle.TerrainCollision) return true;
        float half = particle.ParticleHeight * particle.SizeMultiplier / 2f, cap = physics.MotionCap;
        if (!float.IsFinite(cap) || cap < 0 || cap > 2 || half < 0 || half > 2) return false;
        // Only collision-clipped motion has a proven cap here. Unclipped stock
        // particles keep their original post-step CPU liquid policy.
        world.LiquidPolicy = (particle.DieInAir ? ParticleSimulationWorldInput.DieInAir : 0) |
            (particle.DieInLiquid && unchecked((byte)(TickCount(particle) + 1)) > 2 ? ParticleSimulationWorldInput.DieInLiquid : 0);
        if (world.LiquidPolicy != 0 && !CaptureLiquidCells(particle, physics, batch, ref world)) return false;
        var min = state.WorldMinimum; var max = state.WorldMaximum;
        min.SetAndCorrectDimension((int)(pos.X - half - cap), (int)(pos.Y - cap - 1), (int)(pos.Z - half - cap));
        max.SetAndCorrectDimension((int)(pos.X + half + cap), (int)(pos.Y + half + cap), (int)(pos.Z + half + cap));
        if (min.dimension != max.dimension) return false;
        var region = new WorldRegion(physics.BlockAccess, min.dimension, min.X, min.Y, min.Z, max.X, max.Y, max.Z);
        if (batch.Regions.TryGetValue(region, out var captured))
        { world.BoxStart = captured.Start; world.BoxCount = captured.Count; return true; }
        var boxes = batch.Boxes;
        int start = boxes.Count;
        bool supported = true;
        physics.BlockAccess.WalkBlocks(min, max, (block, x, y, z) =>
        {
            if (!supported) return;
            if (!HasStockBoxes(block))
            {
                // The cap-sized region is only a conservative envelope. A
                // custom block outside the actual motion query must neither
                // reject stock work nor invoke its callback speculatively.
                if (boxes.Count == MaximumWorldBoxes) { supported = false; return; }
                boxes.Add(new ParticleSimulationBox { Minimum = new Vector4(0, 0, 0, 1), X = x, Y = y, Z = z });
                batch.BoxBlocks.Add(block);
                return;
            }
            var values = block.ParticleCollisionBoxes ?? block.CollisionBoxes;
            if (values == null) return;
            foreach (var box in values)
            {
                if (box == null) continue;
                // Preserve local floats. GPU selection assigns the actual cache
                // ordinal before choosing first-allocation or reused rounding.
                if (boxes.Count == MaximumWorldBoxes || !float.IsFinite(box.X1) || !float.IsFinite(box.X2) ||
                    !float.IsFinite(box.Y1) || !float.IsFinite(box.Y2) || !float.IsFinite(box.Z1) || !float.IsFinite(box.Z2))
                { supported = false; return; }
                boxes.Add(new ParticleSimulationBox { Minimum = new Vector4(box.X1, box.Y1, box.Z1, 0),
                    Maximum = new Vector4(box.X2, box.Y2, box.Z2, 0), X = x, Y = y, Z = z });
                batch.BoxBlocks.Add(block);
            }
        });
        if (!supported)
        { boxes.RemoveRange(start, boxes.Count - start); batch.BoxBlocks.RemoveRange(start, batch.BoxBlocks.Count - start); return false; }
        world.BoxStart = (uint)start; world.BoxCount = (uint)(boxes.Count - start);
        batch.Regions.Add(region, (world.BoxStart, world.BoxCount));
        return true;
    }
    private static bool CaptureLiquidCells(ParticleGeneric particle, ParticlePhysics physics, Batch batch,
        ref ParticleSimulationWorldInput world)
    {
        var p = particle.Position;
        // Use raw integer Y exactly like GetBlockRaw, including dimension bits
        // and truncation toward zero on either side of zero. 0.999 only shrinks
        // the collision-clipped displacement, so the inclusive cap encloses it.
        double cap = world.MotionCap;
        int minX = (int)(p.X - cap), maxX = (int)(p.X + cap),
            minY = (int)(p.Y - cap + 0.15000000596046448), maxY = (int)(p.Y + cap + 0.15000000596046448),
            minZ = (int)(p.Z - cap), maxZ = (int)(p.Z + cap);
        var region = new WorldRegion(physics.BlockAccess, 0, minX, minY, minZ, maxX, maxY, maxZ);
        if (!batch.LiquidRegions.TryGetValue(region, out uint start))
        {
            int count = (maxX - minX + 1) * (maxY - minY + 1) * (maxZ - minZ + 1);
            if (batch.LiquidRegions.Count == MaximumLiquidRegions || count > MaximumLiquidCells - batch.LiquidCells.Count) return false;
            start = (uint)batch.LiquidCells.Count;
            for (int z = minZ; z <= maxZ; z++)
                for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++)
                    {
                        var block = physics.BlockAccess.GetBlockRaw(x, y, z, 2);
                        // Mark opaque callbacks without invoking them. Only the
                        // actual selected cell becomes an ordered CPU boundary.
                        batch.LiquidCells.Add(!HasStockLiquid(block) ? 2u : block.IsLiquid() ? 1u : 0u);
                    }
            batch.LiquidRegions.Add(region, start);
        }
        int x0 = (int)p.X, y0 = (int)p.Y, z0 = (int)p.Z;
        world.LiquidStart = start;
        world.LiquidBounds = (uint)(minX - x0 + 8) | (uint)(minY - y0 + 8) << 4 | (uint)(minZ - z0 + 8) << 8 |
            (uint)(maxX - x0 + 8) << 12 | (uint)(maxY - y0 + 8) << 16 | (uint)(maxZ - z0 + 8) << 20;
        return true;
    }
    private static void MaterializeCollisionList(ParticlePhysics physics, Batch batch, int index,
        in ParticleSimulationOutput result, Cuboidf scratch)
    {
        // CachedCuboidList changes its float/double addition after first use.
        // Preserve that real cache state for a later unsupported/custom CPU tick,
        // without repeating any clipping, world query or collision callback.
        var source = batch.Input[index]; var world = batch.World[index];
        int x = (int)source.X, y = (int)source.Y % 32768, z = (int)source.Z;
        uint queryBounds = result.QueryBounds;
        int Delta(int shift) => (int)((queryBounds >> shift) & 15) - 8;
        int minX = x + Delta(0), minY = y + Delta(4), minZ = z + Delta(8),
            maxX = x + Delta(12), maxY = y + Delta(16), maxZ = z + Delta(20);
        var list = physics.CollisionBoxList; list.Clear();
        for (uint j = 0; j < world.BoxCount; j++)
        {
            int boxIndex = (int)(world.BoxStart + j); var box = batch.Boxes[boxIndex];
            if (box.X < minX || box.X > maxX || box.Y < minY || box.Y > maxY || box.Z < minZ || box.Z > maxZ) continue;
            scratch.Set(box.Minimum.X, box.Minimum.Y, box.Minimum.Z, box.Maximum.X, box.Maximum.Y, box.Maximum.Z);
            list.Add(scratch, box.X, box.Y, box.Z, batch.BoxBlocks[boxIndex]);
        }
    }
    private static void ApplyCollisionResponse(ParticleGeneric particle, float dt, ICoreClientAPI api, uint flags)
    {
        CollisionFlags(particle) = (EnumCollideFlags)flags;
        if (flags == 0) { Drag(particle) = 1; return; }
        float drag = (1f - GameMath.Clamp(6.060606f * dt, 0.001f, 1f)) * RandomDrag(particle);
        Drag(particle) = drag;
        // Keep the world's shared RNG, its conditional second draw, and the
        // X/Y/Z response order at the actual ordered stock TickNow boundary.
        if (api.World.Rand.NextDouble() < 0.001)
            RandomDrag(particle) = Math.Max(0, (float)api.World.Rand.NextDouble() - 0.1f);
        if ((flags & 1) != 0)
        { particle.Velocity.X *= -particle.Bounciness * 0.65f; particle.Velocity.Y *= Math.Min(1, drag * 5); particle.Velocity.Z *= drag; }
        if ((flags & 2) != 0)
        { particle.Velocity.X *= drag; particle.Velocity.Y *= -particle.Bounciness * 0.65f; particle.Velocity.Z *= drag; }
        if ((flags & 4) != 0)
        { particle.Velocity.X *= drag; particle.Velocity.Y *= Math.Min(1, drag * 5); particle.Velocity.Z *= -particle.Bounciness * 0.65f; }
    }

    // Headless numeric comparison only, never part of the production tick path.
    private static void ReferenceBuoyancy(ref Vector3 velocity, in ParticleSimulationInput state, ParticleSimulationWorldInput world)
    {
        float dt = state.Timing.W;
        var flow = new Vector3(world.Flow.X, world.Flow.Y, world.Flow.Z);
        if (world.Liquid.W > 0.5f)
        {
            if (world.Liquid.X <= 0.5f) return;
            float submerged = GameMath.Clamp((float)(int)state.Y + world.Liquid.Z / 8f +
                (world.Liquid.Y > 0.5f ? 1.125f : 0) - (float)state.Y + state.Physics.X, 0, 1);
            velocity.Y += state.VelocityGravity.W * dt * GameMath.Clamp(9f * submerged, -1.25f, 1.25f);
            velocity.Y /= GameMath.Clamp(30f * Math.Abs(velocity.Y) - 0.02f, 1, 1.25f);
            velocity.X *= 0.99f; velocity.Z *= 0.99f;
            if (world.Flow.W > 0.5f && submerged >= 0) velocity += flow * (7.5f * (dt / 0.033f));
        }
        else if (world.Flow.W > 0.5f) velocity += flow * (30f * dt);
    }
    private static uint ReferenceCollision(in ParticleSimulationInput state, ParticleSimulationWorldInput world,
        ReadOnlySpan<ParticleSimulationBox> boxes, ref Vector3 motion, ref uint populatedSize, out uint queryBounds)
    {
        float half = state.Physics.X / 2f;
        var particle = new Cuboidd(state.X - half, state.Y, state.Z - half, state.X + half, state.Y + half, state.Z + half);
        motion = Vector3.Clamp(motion, new Vector3(-world.MotionCap), new Vector3(world.MotionCap));
        int minX = (int)(particle.X1 + Math.Min(0, motion.X)), minY = (int)(particle.Y1 + Math.Min(0, motion.Y) - 1) % 32768,
            minZ = (int)(particle.Z1 + Math.Min(0, motion.Z));
        int maxX = (int)(particle.X2 + Math.Max(0, motion.X)), maxY = (int)(particle.Y2 + Math.Max(0, motion.Y)) % 32768,
            maxZ = (int)(particle.Z2 + Math.Max(0, motion.Z));
        int x = (int)state.X, y0 = (int)state.Y % 32768, z = (int)state.Z;
        queryBounds = (uint)(minX - x + 8) | (uint)(minY - y0 + 8) << 4 | (uint)(minZ - z + 8) << 8 |
            (uint)(maxX - x + 8) << 12 | (uint)(maxY - y0 + 8) << 16 | (uint)(maxZ - z + 8) << 20;
        particle.Y1 %= 32768; particle.Y2 %= 32768;
        uint flags = 0;
        uint selected = 0;
        for (int pass = 0; pass < 3; pass++)
        {
            selected = 0;
            int axis = pass == 0 ? 1 : pass == 1 ? 0 : 2;
            for (uint j = 0; j < world.BoxCount; j++)
            {
                var value = boxes[(int)(world.BoxStart + j)];
                if (value.X < minX || value.X > maxX || value.Y < minY || value.Y > maxY || value.Z < minZ || value.Z > maxZ) continue;
                if (value.Minimum.W > 0.5f) return ParticleSimulationOutput.RequiresCpuCollision;
                int y = value.Y % 32768;
                bool reused = selected++ < populatedSize;
                double Offset(float local, int offset) => reused ? (double)(local + offset) : (double)local + offset;
                var box = new Cuboidd(Offset(value.Minimum.X, value.X), Offset(value.Minimum.Y, y), Offset(value.Minimum.Z, value.Z),
                    Offset(value.Maximum.X, value.X), Offset(value.Maximum.Y, y), Offset(value.Maximum.Z, value.Z));
                var direction = EnumPushDirection.None;
                if (axis == 1) motion.Y = (float)box.pushOutY(particle, motion.Y, ref direction);
                else if (axis == 0) motion.X = (float)box.pushOutX(particle, motion.X, ref direction);
                else motion.Z = (float)box.pushOutZ(particle, motion.Z, ref direction);
                if (direction != EnumPushDirection.None) flags |= 1u << axis;
            }
            if (pass == 0) particle.Translate(0, motion.Y, 0);
            if (pass == 1) particle.Translate(motion.X, 0, 0);
        }
        populatedSize = Math.Max(populatedSize, selected);
        return flags;
    }
}
