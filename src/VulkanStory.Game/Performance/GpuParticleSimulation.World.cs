using System.Numerics;
using System.Reflection;
using System.Collections.Concurrent;
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
    private readonly record struct WorldRegion(IBlockAccessor Accessor, int Dimension, int MinX, int MinY, int MinZ, int MaxX, int MaxY, int MaxZ, int Kind = 0);
    private static readonly MethodInfo StockBoxes = AccessTools.Method(typeof(Block), nameof(Block.GetParticleCollisionBoxes),
        [typeof(IBlockAccessor), typeof(BlockPos)])!;
    private static readonly MethodInfo StockLiquid = AccessTools.Method(typeof(CollectibleObject), nameof(CollectibleObject.IsLiquid), Type.EmptyTypes)!;
    private static readonly MethodInfo StockFlowStill = AccessTools.PropertyGetter(typeof(Vintagestory.GameContent.BlockForFluidsLayer), nameof(IBlockFlowing.IsStill))!;
    private static readonly MethodInfo StockFlowPush = AccessTools.Method(typeof(Vintagestory.GameContent.BlockForFluidsLayer), nameof(IBlockFlowing.GetPushVector), [typeof(BlockPos)])!;
    private static readonly ConcurrentDictionary<Type, bool> stockBoxTypes = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo[]> stockFlowTypes = new();
    private static readonly ConcurrentDictionary<Type, bool> stockLiquidTypes = new();
    internal static bool HasStockLiquid(Block block)
    {
        // A hit is a read shared by the render and particle threads, not a
        // reason to serialize every cell on one metadata monitor.
        return stockLiquidTypes.GetOrAdd(block.GetType(), static type =>
            AccessTools.Method(type, nameof(Block.IsLiquid), Type.EmptyTypes)?.DeclaringType == StockLiquid.DeclaringType &&
            Harmony.GetPatchInfo(StockLiquid)?.Owners.Any(owner => !PerformancePatchSet.IsOwnOwner(owner)) != true);
    }
    private static bool HasStockFlow(Block block)
    {
        var methods = stockFlowTypes.GetOrAdd(block.GetType(), static type =>
        {
            var map = type.GetInterfaceMap(typeof(IBlockFlowing));
            int still = Array.FindIndex(map.InterfaceMethods, method => method.Name == "get_IsStill");
            int push = Array.FindIndex(map.InterfaceMethods, method => method.Name == nameof(IBlockFlowing.GetPushVector));
            return still >= 0 && push >= 0 && IsStockFlowTarget(map.TargetMethods[still], StockFlowStill) &&
                IsStockFlowTarget(map.TargetMethods[push], StockFlowPush)
                ? [map.TargetMethods[still], map.TargetMethods[push]] : Array.Empty<MethodInfo>();
        });
        // Keep live patch checks at every confirmation. Only structural
        // identity is cached, and no metadata lock spans the Harmony queries.
        return methods.Length != 0 && !HasForeignFlowPatch(methods[0]) && !HasForeignFlowPatch(methods[1]) &&
            (methods[0] == StockFlowStill || !HasForeignFlowPatch(StockFlowStill)) &&
            (methods[1] == StockFlowPush || !HasForeignFlowPatch(StockFlowPush));
    }
    private static bool HasForeignFlowPatch(MethodInfo method) =>
        Harmony.GetPatchInfo(method)?.Owners.Any(owner => !PerformancePatchSet.IsOwnOwner(owner)) == true;

    private static bool IsStockFlowTarget(MethodInfo method, MethodInfo stock)
    {
        if (method.Module == stock.Module && method.MetadataToken == stock.MetadataToken && method.DeclaringType == stock.DeclaringType) return true;
        if (method.IsStatic || method.IsGenericMethod || method.ContainsGenericParameters ||
            method.CallingConvention != stock.CallingConvention || method.ReturnType != stock.ReturnType ||
            method.DeclaringType == null || !stock.DeclaringType!.IsAssignableFrom(method.DeclaringType)) return false;
        var parameters = method.GetParameters(); var stockParameters = stock.GetParameters();
        if (parameters.Length != stockParameters.Length) return false;
        for (int i = 0; i < parameters.Length; i++)
            if (parameters[i].ParameterType != stockParameters[i].ParameterType) return false;
        try
        {
            // Official 1.22.7 water classes explicitly implement IsStill with
            // ldarg.0; call BlockForFluidsLayer.get_IsStill; ret. Accept only
            // an exact argument-forwarding nonvirtual call to the named base.
            var body = method.GetMethodBody(); var il = body?.GetILAsByteArray();
            int call = parameters.Length + 1;
            if (body == null || body.LocalVariables.Count != 0 || body.ExceptionHandlingClauses.Count != 0 ||
                il == null || il.Length != call + 6) return false;
            for (int i = 0; i < call; i++)
                if (il[i] != 0x02 + i) return false; // ldarg.0, then ldarg.1 for GetPushVector
            if (il[call] != 0x28 || il[^1] != 0x2a) return false; // call, ret; never callvirt or extra work
            var callee = method.Module.ResolveMethod(BitConverter.ToInt32(il, call + 1));
            return callee is MethodInfo target && target.Module == stock.Module &&
                target.MetadataToken == stock.MetadataToken && target.DeclaringType == stock.DeclaringType;
        }
        catch (Exception exception) when (exception is ArgumentException or BadImageFormatException or InvalidOperationException or NotSupportedException)
        { return false; }
    }
    private static bool HasStockBoxes(Block block)
    {
        return stockBoxTypes.GetOrAdd(block.GetType(), static type =>
            AccessTools.Method(type, nameof(Block.GetParticleCollisionBoxes),
                [typeof(IBlockAccessor), typeof(BlockPos)])?.DeclaringType == StockBoxes.DeclaringType);
    }

    // Observe mutable blocks/arrays for every prepared step. The game exposes no
    // coherent world revision, so the consumer confirms each observation the
    // result depends on against the live world at the actual stock tick.
    // Base field-returning boxes are captured directly; custom cells are inert
    // markers until the actual GPU query reaches their ordered CPU boundary.
    // Never call a custom box callback speculatively.
    private static bool CaptureWorld(ParticleGeneric particle, ParticlePhysics physics, PoolState state,
        Batch batch, float dt, out ParticleSimulationWorldInput world)
    {
        world = default;
        world.InitialPopulatedSize = (uint)PopulatedSize(physics.CollisionBoxList);
        if (!CaptureFluid(particle, physics, state, ref world, batch)) return false;
        world.MotionCap = physics.MotionCap;
        world.VisualPolicy = unchecked((byte)(TickCount(particle) + 1)) > 2 ? ParticleSimulationWorldInput.ThirdTick : 0;
        world.LightEmission = particle.LightEmission;
        if (particle.DieOnRainHeightmap) world.VisualPolicy |= ParticleSimulationWorldInput.Rain;
        // TickFixedStep resets advance immediately before TickNow. The worker
        // calls TickNow directly and retains its current interpolation value.
        world.RainInterpolation = new Vector4(particle.prevPosDeltaX, particle.prevPosDeltaY, particle.prevPosDeltaZ,
            current is { Worker: false } ? 0 : particle.prevPosAdvance);
        if (!particle.TerrainCollision) return true;
        var pos = particle.Position;
        float half = particle.ParticleHeight * particle.SizeMultiplier / 2f, cap = physics.MotionCap;
        if (!float.IsFinite(cap) || cap < 0 || cap > 2 || half < 0 || half > 2) return false;
        if ((world.VisualPolicy & ParticleSimulationWorldInput.ThirdTick) != 0 &&
            CaptureEnvironment(particle, physics, batch, dt, ref world)) world.VisualPolicy |= ParticleSimulationWorldInput.Environment;
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
        if (!state.WorldCapture.Capture(physics, batch, min, max, out world.BoxStart, out world.BoxCount)) return false;
        batch.Regions.Add(region, (world.BoxStart, world.BoxCount));
        return true;
    }
    // PoolState.Gate owns this visitor just like CollisionConfirmation. Retain
    // its delegate once instead of allocating a closure for each capture walk.
    private sealed class WorldCapture
    {
        private readonly Action<Block, int, int, int> visit;
        private Batch batch = null!;
        private bool supported;
        internal WorldCapture() => visit = Visit;
        internal bool Capture(ParticlePhysics physics, Batch batch, BlockPos minimum, BlockPos maximum, out uint start, out uint count)
        {
            this.batch = batch; supported = true;
            int first = batch.Boxes.Count;
            start = (uint)first; count = 0;
            try { physics.BlockAccess.WalkBlocks(minimum, maximum, visit); }
            finally { this.batch = null!; }
            if (!supported)
            {
                batch.Boxes.RemoveRange(first, batch.Boxes.Count - first);
                batch.BoxBlocks.RemoveRange(first, batch.BoxBlocks.Count - first);
                return false;
            }
            count = (uint)(batch.Boxes.Count - first);
            return true;
        }
        private void Visit(Block block, int x, int y, int z)
        {
            if (!supported) return;
            var boxes = batch.Boxes;
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
        }
    }
    // The stock HandleBoyancy observations. The consumer repeats this capture at
    // the stock tick; only an identical observation keeps a prepared step.
    private static bool CaptureFluid(ParticleGeneric particle, ParticlePhysics physics, PoolState state, ref ParticleSimulationWorldInput world, Batch? batch = null)
    {
        var pos = particle.Position;
        state.Position.SetAndCorrectDimension((int)pos.X, (int)pos.Y, (int)pos.Z);
        WorldRegion region = default;
        // Speculation may reuse stock observations within this batch. Confirmation
        // passes no batch and always re-reads the live cell at the actual tick.
        if (batch != null)
        {
            var cell = state.Position;
            region = new WorldRegion(physics.BlockAccess, cell.dimension, cell.X, cell.Y, cell.Z, cell.X, cell.Y, cell.Z,
                particle.SwimOnLiquid ? 1 : 0);
            if (batch.Fluids.TryGetValue(region, out var captured))
            { world.Liquid = captured.Liquid; world.Flow = captured.Flow; return true; }
        }
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
        if (batch != null && batch.Fluids.Count < MaximumLiquidRegions) batch.Fluids.TryAdd(region, (world.Liquid, world.Flow));
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
    private static bool CaptureEnvironment(ParticleGeneric particle, ParticlePhysics physics, Batch batch, float dt,
        ref ParticleSimulationWorldInput world)
    {
        var p = particle.Position;
        double cap = world.MotionCap;
        if (world.LightEmission != int.MaxValue && !CaptureCells(1,
            (int)(p.X - cap), (int)(p.Y - cap), (int)(p.Z - cap),
            (int)(p.X + cap), (int)(p.Y + cap), (int)(p.Z + cap), out world.LightStart, out world.LightBounds)) return false;
        if ((world.VisualPolicy & ParticleSimulationWorldInput.Rain) == 0) return true;
        var interpolation = world.RainInterpolation;
        float remainder = 1 - interpolation.W;
        double dx = interpolation.X * remainder * dt * 8f, dy = interpolation.Y * remainder * dt * 12f,
            dz = interpolation.Z * remainder * dt * 8f;
        if (!double.IsFinite(dx) || !double.IsFinite(dy) || !double.IsFinite(dz) || Math.Abs(dx) > 4 || Math.Abs(dy) > 4 || Math.Abs(dz) > 4) return false;
        return CaptureCells(2, (int)(p.X - cap + dx), (int)p.Y, (int)(p.Z - cap + dz),
            (int)(p.X + cap + dx), (int)p.Y, (int)(p.Z + cap + dz), out world.RainStart, out world.RainBounds);

        bool CaptureCells(int kind, int minX, int minY, int minZ,
            int maxX, int maxY, int maxZ, out uint start, out uint bounds)
        {
            start = bounds = 0;
            int x0 = (int)p.X, y0 = (int)p.Y, z0 = (int)p.Z;
            Span<int> offsets = stackalloc int[] { minX - x0, minY - y0, minZ - z0, maxX - x0, maxY - y0, maxZ - z0 };
            for (int i = 0; i < offsets.Length; i++)
            {
                if (offsets[i] < -8 || offsets[i] > 7) return false;
                bounds |= (uint)(offsets[i] + 8) << (i * 4);
            }
            var region = new WorldRegion(physics.BlockAccess, 0, minX, minY, minZ, maxX, maxY, maxZ, kind);
            if (batch.LiquidRegions.TryGetValue(region, out start)) return true;
            int count = (maxX - minX + 1) * (maxY - minY + 1) * (maxZ - minZ + 1);
            if (batch.LiquidRegions.Count == MaximumLiquidRegions || count > MaximumLiquidCells - batch.LiquidCells.Count) return false;
            start = (uint)batch.LiquidCells.Count;
            for (int z = minZ; z <= maxZ; z++)
                for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++)
                        batch.LiquidCells.Add(unchecked((uint)(kind == 1 ? physics.BlockAccess.GetLightRGBsAsInt(x, y, z) :
                            physics.BlockAccess.GetRainMapHeightAt(x, z))));
            batch.LiquidRegions.Add(region, start);
            return true;
        }
    }
    private static void ApplyCollisionResponse(ParticleGeneric particle, in ParticleSimulationOutput result, ICoreClientAPI api)
    {
        uint flags = result.CollisionFlags & ParticleSimulationOutput.CollisionMask;
        CollisionFlags(particle) = (EnumCollideFlags)flags;
        Drag(particle) = result.Drag;
        if (flags == 0) return;
        // Compute applied the X/Y/Z velocity response with this drag, from the
        // random drag before this tick's draw. Keep the world's shared RNG and
        // its conditional second draw at the actual ordered stock TickNow boundary.
        if (api.World.Rand.NextDouble() < 0.001)
            RandomDrag(particle) = Math.Max(0, (float)api.World.Rand.NextDouble() - 0.1f);
    }

    // Headless numeric comparison only, never part of the production tick path.
    private static Vector3 ReferenceResponse(Vector3 velocity, uint flags, float dt, float bounciness, float randomDrag)
    {
        float drag = (1f - GameMath.Clamp(6.060606f * dt, 0.001f, 1f)) * randomDrag;
        if ((flags & 1) != 0) { velocity.X *= (0f - bounciness) * 0.65f; velocity.Y *= Math.Min(1f, drag * 5f); velocity.Z *= drag; }
        if ((flags & 2) != 0) { velocity.X *= drag; velocity.Y *= (0f - bounciness) * 0.65f; velocity.Z *= drag; }
        if ((flags & 4) != 0) { velocity.X *= drag; velocity.Y *= Math.Min(1f, drag * 5f); velocity.Z *= (0f - bounciness) * 0.65f; }
        return velocity;
    }
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
        ReadOnlySpan<ParticleSimulationBox> boxes, ref Vector3 motion, ref uint populatedSize, out uint queryBounds, out uint reused)
    {
        reused = 0;
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
                bool cached = selected++ < populatedSize;
                double Offset(float local, int offset) => cached ? (double)(local + offset) : (double)local + offset;
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
        reused = Math.Min(populatedSize, selected);
        populatedSize = Math.Max(populatedSize, selected);
        return flags;
    }
}
