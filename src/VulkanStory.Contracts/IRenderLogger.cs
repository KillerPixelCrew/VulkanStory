namespace VulkanStory.Contracts;

public interface IRenderLogger
{
    void Notification(string format, params object?[] arguments);
    void Warning(string format, params object?[] arguments);
}
