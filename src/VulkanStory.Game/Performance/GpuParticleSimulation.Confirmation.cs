using System.Numerics;
using System.Runtime.InteropServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VulkanStory.Contracts;

namespace VulkanStory.Game.Performance;

internal static partial class GpuParticleSimulation
{
    // A prepared step observed the world one pool call earlier. Repeat the
    // observations its result depends on at the actual stock tick, in the
    // stock read order, and keep the step only when every one is identical.
    private static bool Confirm(ParticleGeneric particle, ParticlePhysics physics, PoolState state, Batch batch, int index,
        in ParticleSimulationOutput result)
    {
        ref readonly var expected = ref batch.World[index];
        if (particle.LightEmission != expected.LightEmission || particle.DieOnRainHeightmap != ((expected.VisualPolicy & ParticleSimulationWorldInput.Rain) != 0)) return false;
        var observed = default(ParticleSimulationWorldInput);
        if (!CaptureFluid(particle, physics, state, ref observed) || !Same(observed.Liquid, expected.Liquid) || !Same(observed.Flow, expected.Flow) ||
            BitConverter.SingleToUInt32Bits(physics.MotionCap) != BitConverter.SingleToUInt32Bits(expected.MotionCap)) return false;
        if ((result.QueryBounds & ParticleSimulationOutput.LiquidPolicyApplied) != 0 && !ConfirmLiquidCell(particle, physics, in expected, in result))
            return false;
        // Reject environmental/byte mismatches before collision confirmation
        // can populate the stock cache and alter first-allocation rounding.
        if (!ConfirmEnvironment(particle, physics, batch, index, in result)) return false;
        return !particle.TerrainCollision || state.Collision.Confirm(physics, batch, index, in result);
    }

    private static bool ConfirmEnvironment(ParticleGeneric particle, ParticlePhysics physics, Batch batch, int index,
        in ParticleSimulationOutput result)
    {
        var world = batch.World[index];
        if ((world.VisualPolicy & ParticleSimulationWorldInput.ThirdTick) == 0) return true;
        // The shader's checked arithmetic produced the stock trajectory; risky
        // intermediates reject the whole step before reaching this confirmation.
        // Compare the three packed bytes before accepting it, including the
        // x64 .NET 10 NaN/overflow conversion; a mismatch runs the stock tick.
        var v = result.VelocityAge;
        float length = GameMath.RootSumOfSquares(v.X, v.Y, v.Z);
        uint direction = (uint)(unchecked((byte)(v.X / length * 128f)) |
            unchecked((byte)(v.Y / length * 128f)) << 8 | unchecked((byte)(v.Z / length * 128f)) << 16);
        if (direction != result.Direction) return false;
        if ((world.VisualPolicy & ParticleSimulationWorldInput.Environment) == 0) return true;
        var input = batch.Input[index];
        int light = world.LightEmission;
        if (world.LightEmission != int.MaxValue && (!Observed(world.LightStart, world.LightBounds, (int)result.X, (int)result.Y, (int)result.Z, out light) ||
            physics.BlockAccess.GetLightRGBsAsInt((int)result.X, (int)result.Y, (int)result.Z) != light)) return false;
        int emission = world.LightEmission;
        if (emission != 0) light = Math.Max(light & 255, emission & 255) |
            Math.Max(light & 65280, emission & 65280) | Math.Max(light & 16711680, emission & 16711680);
        if (result.Light != light) return false;
        if ((world.VisualPolicy & ParticleSimulationWorldInput.Rain) == 0) return true;
        var interpolation = world.RainInterpolation;
        if (!Same(interpolation, new Vector4(particle.prevPosDeltaX, particle.prevPosDeltaY, particle.prevPosDeltaZ, particle.prevPosAdvance))) return false;
        float remainder = 1 - interpolation.W, dt = input.Timing.W;
        double dx = interpolation.X * remainder * dt * 8f, dy = interpolation.Y * remainder * dt * 12f,
            dz = interpolation.Z * remainder * dt * 8f;
        int x = (int)(result.X + dx), z = (int)(result.Z + dz);
        return Observed(world.RainStart, world.RainBounds, x, (int)input.Y, z, out int rain) &&
            physics.BlockAccess.GetRainMapHeightAt(x, z) == rain &&
            ((result.VisualPolicy & ParticleSimulationWorldInput.RainSurvived) != 0) == (rain - dy < result.Y);

        bool Observed(uint start, uint bounds, int x, int y, int z, out int observed)
        {
            int Offset(int shift) => (int)((bounds >> shift) & 15) - 8;
            int minX = (int)input.X + Offset(0), minY = (int)input.Y + Offset(4), minZ = (int)input.Z + Offset(8),
                maxX = (int)input.X + Offset(12), maxY = (int)input.Y + Offset(16), maxZ = (int)input.Z + Offset(20);
            observed = 0;
            if (x < minX || x > maxX || y < minY || y > maxY || z < minZ || z > maxZ) return false;
            int address = (int)start + ((z - minZ) * (maxY - minY + 1) + y - minY) * (maxX - minX + 1) + x - minX;
            observed = unchecked((int)batch.LiquidCells[address]);
            return true;
        }
    }

    private static bool ConfirmLiquidCell(ParticleGeneric particle, ParticlePhysics physics, in ParticleSimulationWorldInput world,
        in ParticleSimulationOutput result)
    {
        // The same short circuit as the GPU policy: the selected cell matters only
        // for liquid death or an air death the lifetime has not already decided.
        bool needsCell = (world.LiquidPolicy & ParticleSimulationWorldInput.DieInLiquid) != 0 ||
            ((world.LiquidPolicy & ParticleSimulationWorldInput.DieInAir) != 0 && result.VelocityAge.W < particle.LifeLength);
        if (!needsCell) return true;
        var block = physics.BlockAccess.GetBlockRaw((int)result.X, (int)(result.Y + 0.15000000596046448), (int)result.Z, 2);
        return HasStockLiquid(block) && block.IsLiquid() == ((result.QueryBounds & ParticleSimulationOutput.LiquidCell) != 0);
    }

    private static bool Same(Vector4 a, Vector4 b) =>
        BitConverter.SingleToUInt32Bits(a.X) == BitConverter.SingleToUInt32Bits(b.X) && BitConverter.SingleToUInt32Bits(a.Y) == BitConverter.SingleToUInt32Bits(b.Y) &&
        BitConverter.SingleToUInt32Bits(a.Z) == BitConverter.SingleToUInt32Bits(b.Z) && BitConverter.SingleToUInt32Bits(a.W) == BitConverter.SingleToUInt32Bits(b.W);

    // Repeats the stock UpdateMotion walk over the step's actual query. Every
    // box must equal the observation the GPU used, and each one is added to
    // CachedCuboidList exactly as stock adds it, so a confirmed step leaves the
    // real cache state behind. A custom box callback is never invoked here: it
    // ends confirmation, and the stock CPU tick that follows makes the call in
    // order after rebuilding the same list prefix.
    private sealed class CollisionConfirmation
    {
        private readonly BlockPos minimum = new(0), maximum = new(0);
        private readonly Action<Block, int, int, int> visit;
        private Batch batch = null!;
        private CachedCuboidList list = null!;
        private int minX, minY, minZ, maxX, maxY, maxZ;
        private int cursor, end;
        private bool mismatch;
        internal CollisionConfirmation() => visit = Visit;

        internal bool Confirm(ParticlePhysics physics, Batch batch, int index, in ParticleSimulationOutput result)
        {
            ref readonly var source = ref batch.Input[index];
            ref readonly var world = ref batch.World[index];
            int x = (int)source.X, y = (int)source.Y % 32768, z = (int)source.Z;
            uint bounds = result.QueryBounds;
            int Delta(int shift) => (int)((bounds >> shift) & 15) - 8;
            minX = x + Delta(0); minY = y + Delta(4); minZ = z + Delta(8);
            maxX = x + Delta(12); maxY = y + Delta(16); maxZ = z + Delta(20);
            this.batch = batch;
            cursor = (int)world.BoxStart; end = (int)(world.BoxStart + world.BoxCount);
            var boxes = CollectionsMarshal.AsSpan(batch.Boxes);
            uint selected = 0;
            for (int j = cursor; j < end; j++)
            {
                if (!Inside(in boxes[j])) continue;
                if (boxes[j].Minimum.W > 0.5f) return false;
                selected++;
            }
            // Compute chose float or double box rounding from the cache prefix it
            // assumed; the stock tick sees the prefix this pool actually has now.
            uint reused = (result.CollisionFlags >> ParticleSimulationOutput.ReusedShift) & ParticleSimulationOutput.ReusedMask;
            if (Math.Min((uint)PopulatedSize(physics.CollisionBoxList), selected) != reused) return false;
            // Same envelope dimension as the capture; the query lies inside it.
            int dimension = (int)(source.Y - world.MotionCap - 1) / 32768;
            minimum.Set(minX, minY, minZ).SetDimension(dimension);
            maximum.Set(maxX, maxY, maxZ).SetDimension(dimension);
            list = physics.CollisionBoxList; list.Clear(); mismatch = false;
            physics.BlockAccess.WalkBlocks(minimum, maximum, visit);
            return !mismatch && !Next(out _);
        }

        private void Visit(Block block, int x, int y, int z)
        {
            if (mismatch) return;
            if (!HasStockBoxes(block)) { mismatch = true; return; }
            var values = block.ParticleCollisionBoxes ?? block.CollisionBoxes;
            if (values == null) return;
            foreach (var box in values)
            {
                if (box == null) continue;
                if (!Next(out int stored)) { mismatch = true; return; }
                var captured = batch.Boxes[stored];
                if (captured.X != x || captured.Y != y || captured.Z != z || !ReferenceEquals(batch.BoxBlocks[stored], block) ||
                    !Same(captured.Minimum, new Vector4(box.X1, box.Y1, box.Z1, 0)) || !Same(captured.Maximum, new Vector4(box.X2, box.Y2, box.Z2, 0)))
                { mismatch = true; return; }
                list.Add(box, x, y, z, block);
            }
        }

        // The captured envelope walked the same X, Y, Z order; restricted to the
        // query it yields boxes in the order the stock walk adds them.
        private bool Next(out int stored)
        {
            var boxes = CollectionsMarshal.AsSpan(batch.Boxes);
            while (cursor < end)
            {
                int j = cursor++;
                if (Inside(in boxes[j])) { stored = j; return true; }
            }
            stored = -1;
            return false;
        }

        private bool Inside(in ParticleSimulationBox box) =>
            box.X >= minX && box.X <= maxX && box.Y >= minY && box.Y <= maxY && box.Z >= minZ && box.Z <= maxZ;
    }
}
