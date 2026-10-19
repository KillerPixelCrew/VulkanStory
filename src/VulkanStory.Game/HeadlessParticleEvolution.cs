using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VulkanStory.Game;

/// <summary>Emits real stock particles covering visual evolution functions in an isolated harness world.</summary>
internal static class HeadlessParticleEvolution
{
    internal static void Emit(ICoreClientAPI api)
    {
        if (!HeadlessHarnessOptions.Enabled) throw new InvalidOperationException("Particle evolution fixture requires the hidden harness.");
        EvolvingNatFloat Evolution(int mode, float factor, float maximum)
        {
            object value = EvolvingNatFloat.create((EnumTransformFunction)mode, factor);
            if (mode % 2 == 0) AccessTools.Field(typeof(EvolvingNatFloat), "maxvalue").SetValue(value, (float?)maximum);
            return (EvolvingNatFloat)value;
        }
        foreach (var model in new[] { EnumParticleModel.Cube, EnumParticleModel.Quad })
            for (int function = 0; function < 12; function++)
            {
                float factor = function == 2 ? -0.35f : 0.35f;
                var particles = new SimpleParticleProperties
                {
                    MinQuantity = 8, AddQuantity = 0, ParticleModel = model,
                    MinPos = api.World.Player.Entity.Pos.XYZ.Add((function % 4 - 1.5) * 0.35, 1.3 + function / 4 * 0.2, 3),
                    AddPos = new Vec3d(0.1, 0.1, 0.1), MinVelocity = new Vec3f(0, 0.2f, 0), AddVelocity = new Vec3f(0.05f, 0.1f, 0.05f),
                    MinSize = 2, MaxSize = 2, Color = ColorUtil.ToRgba(model == EnumParticleModel.Quad ? 255 : 192, 88, 68, 48), LifeLength = 4,
                    GravityEffect = 0.02f, LightEmission = int.MaxValue, WithTerrainCollision = true,
                    VertexFlags = model == EnumParticleModel.Quad && function % 2 == 0 ? int.MinValue : 0,
                    SizeEvolve = Evolution(function, factor, 2.05f), OpacityEvolve = Evolution(function, factor, 210),
                    RedEvolve = Evolution(function, factor, 100), GreenEvolve = Evolution(function, factor, 100), BlueEvolve = Evolution(function, factor, 100),
                };
                api.World.SpawnParticles(particles);
            }
    }
}
