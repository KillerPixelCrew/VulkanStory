using System;
using System.Collections.Generic;
using System.Threading;

namespace VulkanStory.Game.Input;

/// <summary>Immutable controller prompt snapshot shared by the SDL input and game UI.</summary>
public static class ControllerHints
{
    private static Dictionary<string, string>? active;
    private static int controllerActive;
    private static readonly List<WeakReference<Action>> listeners = new();
    private static readonly object listenerLock = new();

    public static string? GlyphFor(string hotkeyCode)
    {
        Dictionary<string, string>? snapshot = Volatile.Read(ref active);
        return Volatile.Read(ref controllerActive) != 0 && snapshot != null &&
            snapshot.TryGetValue(hotkeyCode, out string? glyph) ? glyph : null;
    }

    public static bool SetControllerActive(bool value)
    {
        bool changed = Interlocked.Exchange(ref controllerActive, value ? 1 : 0) != (value ? 1 : 0);
        if (changed) NotifyChanged();
        return changed;
    }

    public static string LabelFor(string hotkeyCode, string keyboardLabel)
    {
        string? glyph = GlyphFor(hotkeyCode);
        return glyph == null ? keyboardLabel : "[" + glyph + "] " + keyboardLabel;
    }

    public static void Publish(IReadOnlyDictionary<string, string>? glyphs)
    {
        Volatile.Write(ref active, glyphs == null ? null : new Dictionary<string, string>(glyphs));
        if (glyphs == null) Interlocked.Exchange(ref controllerActive, 0);
        NotifyChanged();
    }

    public static void Subscribe(Action listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (listenerLock) listeners.Add(new WeakReference<Action>(listener));
    }

    private static void NotifyChanged()
    {
        List<Action> callbacks = new();
        lock (listenerLock)
        {
            for (int i = listeners.Count - 1; i >= 0; i--)
            {
                if (listeners[i].TryGetTarget(out Action? listener)) callbacks.Add(listener);
                else listeners.RemoveAt(i);
            }
        }
        foreach (Action callback in callbacks)
        {
            // A stale GUI page must not interrupt keyboard or gamepad input.
            try { callback(); }
            catch { }
        }
    }
}
