using Vintagestory.API.Common;
using VulkanStory.Contracts;

namespace VulkanStory.Game;

/// <summary>Forwards renderer notifications and warnings to the borrowed original game logger.</summary>
internal sealed class GameRenderLogger(ILogger logger) : IRenderLogger
{
    /// <inheritdoc />
    public void Notification(string format, params object?[] arguments) => logger.Notification(format, arguments);
    /// <inheritdoc />
    public void Warning(string format, params object?[] arguments) => logger.Warning(format, arguments);
}
