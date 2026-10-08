namespace VulkanStory.Contracts;

/// <summary>Host-provided message sink used by renderer and provider code without game API dependencies.</summary>
public interface IRenderLogger
{
    /// <summary>Writes an informational notification using the host's formatting convention.</summary>
    /// <param name="format">Message text or composite format string.</param>
    /// <param name="arguments">Values supplied to the host formatter.</param>
    void Notification(string format, params object?[] arguments);
    /// <summary>Writes a warning using the host's formatting convention.</summary>
    /// <param name="format">Message text or composite format string.</param>
    /// <param name="arguments">Values supplied to the host formatter.</param>
    void Warning(string format, params object?[] arguments);
}
