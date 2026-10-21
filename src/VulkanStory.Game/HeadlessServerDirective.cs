using System.Globalization;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Server;

namespace VulkanStory.Game;

/// <summary>One harness command-script line and the world frame it runs at.</summary>
/// <remarks><c>@&lt;frame&gt; &lt;line&gt;</c> schedules a line; other lines run at the script's command frame (frame 0 here).</remarks>
internal sealed record HeadlessTimedCommand(long Frame, string Line)
{
    internal static HeadlessTimedCommand Parse(string line)
    {
        if (!line.StartsWith('@')) return new(0, line);
        int space = line.IndexOf(' ');
        if (space < 2 || !long.TryParse(line.AsSpan(1, space - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long frame))
            throw new FormatException("Timed harness command needs '@<frame> <command>': " + line);
        return new(frame, line[(space + 1)..].Trim());
    }
}

/// <summary>Harness-only world setup run on the integrated server's main thread, for paths no stock command reaches.</summary>
/// <remarks>
/// <c>!sand dx dy dz</c> places sand above the first player and wakes it, so it falls as a falling-block entity.
/// <c>!drop dx dz count</c> spawns dropped flint items on the surface at that offset. Offsets are in blocks.
/// <c>!ground-items dx dz</c> places or refills real ground storage with two hot raw bowls in separate slots.
/// <c>!occluder distance</c> builds a granite wall across the camera's view at that distance, spawns four traders behind it
/// and two in front of it.
/// <c>!fallingsource</c> arms <see cref="HeadlessFallingMeshSource" /> on the client.
/// Only the headless harness reads command scripts; the server comes from the worldgen feature's constructor hook.
/// </remarks>
internal static class HeadlessServerDirective
{
    internal static void Run(string line, Vintagestory.API.Client.ICoreClientAPI client)
    {
        string[] parts = line[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && parts[0] == "fallingsource") { HeadlessFallingMeshSource.Arm(client); return; }
        if (parts.Length == 1 && parts[0] == "resolve-library") { ModCompatibilityConsumerPatches.VerifyManagedDependencyResolution(); return; }
        if (parts.Length == 1 && parts[0] == "evolution-particles") { HeadlessParticleEvolution.Emit(client); return; }
        ServerMain server = Performance.WorldgenPipeline.IntegratedServer ??
            throw new InvalidOperationException("No integrated server is known to the harness.");
        int Arg(int index) => int.Parse(parts[index], CultureInfo.InvariantCulture);
        switch (parts[0])
        {
            case "terrain-peek":
                HeadlessTerrainComparison.Start(server, parts);
                break;
            case "sand" when parts.Length == 4:
            {
                int dx = Arg(1), dy = Arg(2), dz = Arg(3);
                server.EnqueueMainThreadTask(() => Guarded(line, () =>
                {
                    IWorldAccessor world = server;
                    var player = world.AllOnlinePlayers.FirstOrDefault()?.Entity ?? throw new InvalidOperationException("No player.");
                    Block sand = world.GetBlock(new AssetLocation("sand-granite")) ?? throw new InvalidOperationException("No sand block.");
                    var pos = player.Pos.AsBlockPos.Add(dx, dy, dz);
                    world.BlockAccessor.SetBlock(sand.BlockId, pos);
                    world.BlockAccessor.TriggerNeighbourBlockUpdate(pos);
                }));
                break;
            }
            case "drop" when parts.Length == 4:
            {
                int dx = Arg(1), dz = Arg(2), count = Arg(3);
                server.EnqueueMainThreadTask(() => Guarded(line, () =>
                {
                    IWorldAccessor world = server;
                    var player = world.AllOnlinePlayers.FirstOrDefault()?.Entity ?? throw new InvalidOperationException("No player.");
                    Item flint = world.GetItem(new AssetLocation("flint")) ?? throw new InvalidOperationException("No flint item.");
                    int x = (int)player.Pos.X + dx, z = (int)player.Pos.Z + dz;
                    int y = world.BlockAccessor.GetRainMapHeightAt(x, z) + 2;
                    for (int index = 0; index < count; index++)
                        world.SpawnItemEntity(new ItemStack(flint), new Vec3d(x + .5, y, z + .5 + index * .3));
                }));
                break;
            }
            case "ground-items" when parts.Length == 3:
            {
                int dx = Arg(1), dz = Arg(2);
                server.EnqueueMainThreadTask(() => Guarded(line, () => PopulateGroundItems(server, dx, dz)));
                break;
            }
            case "occluder" when parts.Length == 2:
            {
                int distance = Arg(1);
                // The client camera's horizontal forward: the view matrix's third row, negated (column-major).
                double[] view = client.Render.CameraMatrixOrigin;
                double fx = -view[2], fz = -view[10], length = Math.Sqrt(fx * fx + fz * fz);
                if (length < 1e-3) throw new InvalidOperationException("The camera looks straight up or down.");
                fx /= length; fz /= length;
                server.EnqueueMainThreadTask(() => Guarded(line, () => BuildOccluder(server, distance, fx, fz)));
                break;
            }
            default: throw new FormatException("Unknown harness directive: " + line);
        }
    }

    private static void PopulateGroundItems(ServerMain server, int dx, int dz)
    {
        IWorldAccessor world = server;
        var player = world.AllOnlinePlayers.FirstOrDefault()?.Entity ?? throw new InvalidOperationException("No player.");
        Block ground = world.GetBlock(new AssetLocation("game:groundstorage")) ?? throw new InvalidOperationException("No ground storage block.");
        Block bowl = world.GetBlock(new AssetLocation("game:bowl-blue-raw")) ?? throw new InvalidOperationException("No raw bowl block.");
        if (ground.BlockId == 0 || bowl.BlockId == 0) throw new InvalidOperationException("Ground storage assets were not resolved.");
        int x = (int)player.Pos.X + dx, z = (int)player.Pos.Z + dz;
        int surface = world.BlockAccessor.GetRainMapHeightAt(x, z);
        var pos = new BlockPos(x, surface + 1, z, player.Pos.Dimension);
        BlockEntity? storage = null;
        // Refill the same genuine block/slots after a network or provider transition.
        for (int y = surface + 2; y >= surface - 1; y--)
        {
            var probe = new BlockPos(x, y, z, player.Pos.Dimension);
            if (world.BlockAccessor.GetBlock(probe).BlockId != ground.BlockId) continue;
            storage = world.BlockAccessor.GetBlockEntity(probe); pos = probe; break;
        }
        if (storage == null)
        {
            if (world.BlockAccessor.GetBlock(pos).BlockId != 0)
                throw new InvalidOperationException("Ground storage target is occupied.");
            var below = pos.DownCopy();
            if (!world.BlockAccessor.GetBlock(below).CanAttachBlockAt(world.BlockAccessor, ground, below, BlockFacing.UP))
                throw new InvalidOperationException("Ground storage target has no supporting surface.");
            world.BlockAccessor.SetBlock(ground.BlockId, pos);
            storage = world.BlockAccessor.GetBlockEntity(pos) ?? throw new InvalidOperationException("Ground storage entity was not created.");
        }
        if (storage is not IBlockEntityContainer container || container.Inventory.Count < 2)
            throw new InvalidOperationException("Ground storage has no two-slot inventory.");
        var determine = AccessTools.Method(storage.GetType(), "DetermineStorageProperties", [typeof(ItemSlot)]) ??
            throw new MissingMethodException("Ground storage property derivation changed.");
        var angle = AccessTools.Property(storage.GetType(), "MeshAngle") ?? throw new MissingMemberException("Ground storage mesh angle changed.");
        if (angle.PropertyType != typeof(float) || angle.SetMethod == null) throw new InvalidOperationException("Ground storage angle type changed.");
        for (int i = 0; i < 2; i++)
        {
            var stack = new ItemStack(bowl, 1);
            bowl.SetTemperature(world, stack, 800f, true);
            container.Inventory[i].Itemstack = stack;
        }
        determine.Invoke(storage, [null]);
        angle.SetValue(storage, 0f);
        for (int i = 0; i < 2; i++) container.Inventory[i].MarkDirty();
        storage.MarkDirty(true);
    }

    // A granite wall across the view, four traders behind it and two in plain view in front of it.
    private static void BuildOccluder(ServerMain server, int distance, double fx, double fz)
    {
        const int HalfWidth = 6, Below = 3, Above = 9;
        IWorldAccessor world = server;
        var player = world.AllOnlinePlayers.FirstOrDefault()?.Entity ?? throw new InvalidOperationException("No player.");
        Block rock = world.GetBlock(new AssetLocation("rock-granite")) ?? throw new InvalidOperationException("No granite block.");
        EntityProperties trader = world.GetEntityType(new AssetLocation("trader-male-commodities-temperate")) ??
            throw new InvalidOperationException("No trader entity type.");
        double px = player.Pos.X, pz = player.Pos.Z, sx = -fz, sz = fx;
        int eye = (int)player.Pos.Y;
        var pos = new BlockPos(player.Pos.Dimension);
        for (double side = -HalfWidth; side <= HalfWidth; side += 0.5)
            for (int y = eye - Below; y <= eye + Above; y++)
            {
                pos.Set((int)Math.Floor(px + fx * distance + sx * side), y, (int)Math.Floor(pz + fz * distance + sz * side));
                world.BlockAccessor.SetBlock(rock.BlockId, pos);
            }
        void Spawn(double depth, double side)
        {
            double x = px + fx * depth + sx * side, z = pz + fz * depth + sz * side;
            Entity entity = world.ClassRegistry.CreateEntity(trader);
            entity.Pos.SetPos(x, world.BlockAccessor.GetRainMapHeightAt((int)Math.Floor(x), (int)Math.Floor(z)) + 1, z);
            entity.Pos.Dimension = player.Pos.Dimension;
            world.SpawnEntity(entity);
        }
        foreach (double side in new[] { -3.0, -1.0, 1.0, 3.0 }) Spawn(distance + 8, side);
        Spawn(distance - 4, -4);
        Spawn(distance - 4, 4);
    }

    // Directives run on the server main thread, where an escaping exception would stop the server.
    private static void Guarded(string line, Action action)
    {
        try { action(); }
        catch (Exception error) { ServerMain.Logger.Error("VulkanStory headless directive failed: {0}: {1}", line, error.Message); }
    }
}
