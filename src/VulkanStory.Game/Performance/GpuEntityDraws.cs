using Vintagestory.API.Common.Entities;
using System.Reflection;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;

namespace VulkanStory.Game.Performance;

internal static class GpuEntityDraws
{
    private static GameGraphicsAdapter? graphics;
    internal static bool Armed { get; private set; }
    private static readonly HashSet<Assembly> stockRendererAssemblies = new();
    internal static bool OwnsRenderer(Type type) => stockRendererAssemblies.Contains(type.Assembly);
    internal static void Attach(GameGraphicsAdapter? adapter) => graphics = adapter;
    internal static bool Handles(Entity entity) => graphics?.CanGpuCullEntity(entity) == true;
    internal static PerformanceFeature Create(Assembly essentials, Assembly survival) => new("gpu-entity-draws",
        settings => settings.GpuEntityDrawPreparation, GameGraphicsAdapter.ValidateEntityVisibilityBindings, _ =>
        {
            stockRendererAssemblies.Add(typeof(EntityShapeRenderer).Assembly);
            stockRendererAssemblies.Add(essentials); stockRendererAssemblies.Add(survival);
            Armed = true;
        }, Uninstall: () => { Armed = false; stockRendererAssemblies.Clear(); });
}
