using System.Runtime.InteropServices;
using System.Text.Json;
using Cairo;
using SkiaSharp;
using Vintagestory.API.Client;

namespace VulkanStory.Game.Input;

// PromptFont is private to our GUI composition. Never register an OS font or
// substitute its symbol codepoints into ordinary game text.
internal static class ControllerPromptFont
{
    private static readonly object Gate = new();
    private static readonly Dictionary<(string Glyph, int Size), ImageSurface> Cache = new();
    private static SKTypeface? typeface;
    private static Dictionary<string, string>? characters;
    private static bool initialized, failureReported;
    private static string? failure;

    private static string? GlyphName(string label) => label switch
    {
        "A" => "gamepad-a", "B" => "gamepad-b", "X" => "gamepad-x", "Y" => "gamepad-y",
        "×" or "Cross" => "sony-a", "○" or "Circle" => "sony-b",
        "□" or "Square" => "sony-x", "△" or "Triangle" => "sony-y",
        "LT" => "xbox-left-trigger", "RT" => "xbox-right-trigger",
        "LB" => "xbox-left-shoulder", "RB" => "xbox-right-shoulder",
        "L2" => "sony-left-trigger", "R2" => "sony-right-trigger",
        "L1" => "sony-left-shoulder", "R1" => "sony-right-shoulder",
        "ZL" => "nintendo-left-trigger", "ZR" => "nintendo-right-trigger",
        "L" => "nintendo-left-shoulder", "R" => "nintendo-right-shoulder",
        "View" => "xbox-view", "Menu" => "xbox-menu", "Home" => "gamepad-home",
        "Share" => "sony-share", "Options" => "sony-options",
        "−" => "nintendo-minus", "+" => "nintendo-plus",
        "LS" => "analog-l-click", "RS" => "analog-r-click",
        "D↑" => "dpad-up", "D↓" => "dpad-down", "D←" => "dpad-left", "D→" => "dpad-right",
        "LX−" => "analog-l-left", "LX+" => "analog-l-right",
        "LY−" => "analog-l-up", "LY+" => "analog-l-down",
        "RX−" => "analog-r-left", "RX+" => "analog-r-right",
        "RY−" => "analog-r-up", "RY+" => "analog-r-down",
        _ => null,
    };

    private static void Initialize()
    {
        if (initialized) return;
        initialized = true;
        var assembly = typeof(ControllerPromptFont).Assembly;
        using Stream font = assembly.GetManifestResourceStream("VulkanStory.Game.PromptFont.promptfont.ttf")
            ?? throw new InvalidDataException("Bundled PromptFont is missing.");
        typeface = SKTypeface.FromStream(font) ?? throw new InvalidDataException("Bundled PromptFont cannot be decoded.");
        using Stream metadata = assembly.GetManifestResourceStream("VulkanStory.Game.PromptFont.glyphs.json")
            ?? throw new InvalidDataException("PromptFont glyph metadata is missing.");
        using JsonDocument document = JsonDocument.Parse(metadata);
        characters = new(StringComparer.Ordinal);
        foreach (var entry in document.RootElement.EnumerateArray())
            characters[entry.GetProperty("code-name").GetString()!] = char.ConvertFromUtf32(entry.GetProperty("codepoint").GetInt32());
    }

    internal static bool TryDraw(Context context, ICoreClientAPI api, string label,
        double x, double y, double width, double height, double[] color)
    {
        string? name = GlyphName(label);
        if (name == null || !double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1) return false;
        lock (Gate)
        {
            try
            {
                Initialize();
                if (typeface == null || characters == null || !characters.TryGetValue(name, out string? glyph)) return false;
                int size = Math.Clamp((int)Math.Ceiling(Math.Min(width, height)), 8, 512);
                if (!Cache.TryGetValue((glyph, size), out var surface))
                {
                    using var font = new SKFont(typeface, size * .85f);
                    if (font.GetGlyphs(glyph).Any(value => value == 0)) return false;
                    font.MeasureText(glyph, out SKRect bounds);
                    if (bounds.Width <= 0 || bounds.Height <= 0) return false;
                    float fit = size * .85f / Math.Max(bounds.Width, bounds.Height);
                    if (fit < 1)
                    {
                        font.Size *= fit;
                        font.MeasureText(glyph, out bounds);
                    }
                    using var bitmap = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
                    using (var canvas = new SKCanvas(bitmap))
                    using (var paint = new SKPaint { Color = SKColors.White, IsAntialias = true })
                    {
                        canvas.Clear(SKColors.Transparent);
                        canvas.DrawText(glyph, (size - bounds.Width) / 2 - bounds.Left,
                            (size - bounds.Height) / 2 - bounds.Top, SKTextAlign.Left, font, paint);
                    }
                    surface = new ImageSurface(Format.Argb32, size, size);
                    try
                    {
                        byte[] row = new byte[size * 4];
                        for (int index = 0; index < size; index++)
                        {
                            Marshal.Copy(bitmap.GetPixels() + index * bitmap.RowBytes, row, 0, row.Length);
                            Marshal.Copy(row, 0, surface.DataPtr + index * surface.Stride, row.Length);
                        }
                        surface.MarkDirty();
                    }
                    catch { surface.Dispose(); throw; }
                    if (Cache.Count >= 64)
                    {
                        foreach (var item in Cache.Values) item.Dispose();
                        Cache.Clear();
                    }
                    Cache.Add((glyph, size), surface);
                }
                context.Save();
                try
                {
                    context.SetSourceRGBA(color);
                    context.MaskSurface(surface, x + (width - size) / 2, y + (height - size) / 2);
                }
                finally { context.Restore(); }
                return true;
            }
            catch (Exception error)
            {
                failure ??= error.Message;
                if (!failureReported)
                {
                    failureReported = true;
                    api.Logger.Warning("VulkanStory controller glyph font unavailable: {0}; using text hints.", failure);
                }
                return false;
            }
        }
    }

    internal static void Release()
    {
        lock (Gate)
        {
            foreach (var item in Cache.Values) item.Dispose();
            Cache.Clear();
            typeface?.Dispose(); typeface = null;
            characters = null;
            initialized = failureReported = false;
            failure = null;
        }
    }
}
