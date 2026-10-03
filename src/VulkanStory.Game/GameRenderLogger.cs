using Vintagestory.API.Common;
using VulkanStory.Contracts;

namespace VulkanStory.Game;

internal sealed class GameRenderLogger(ILogger logger) : IRenderLogger
{
    public void Notification(string format, params object?[] arguments) => logger.Notification(format, arguments);
    public void Warning(string format, params object?[] arguments) => logger.Warning(format, arguments);
}
