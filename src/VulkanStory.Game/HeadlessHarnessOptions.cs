// Migrated from optimum-render-device.cs, baseline 386e0d05386d0b228b439d09aeca851428f7bbf3.
// Frame planning, command parsing and Netpbm writer retained; no modified API dependency.
namespace VulkanStory.Game;

/// <summary>Parses explicit headless environment configuration and writes capture/result files for the isolated harness.</summary>
public static class HeadlessHarnessOptions
{
    /// <summary>True when <c>VULKANSTORY_HEADLESS</c> asks for an invisible window.</summary>
    public static readonly bool Enabled = ResolveFlag("VULKANSTORY_HEADLESS");
    /// <summary>Explicit diagnostic launch only; routine harness windows stay hidden.</summary>
    public static readonly bool KeepWindowHidden = Enabled && !ResolveFlag("VULKANSTORY_HEADLESS_VISIBLE");
    internal static readonly bool MainMenuOptions = Enabled && ResolveFlag("VULKANSTORY_HEADLESS_MAIN_OPTIONS");

    /// <summary>The chat-command script, or null when there is none.</summary>
    public static readonly string? CommandScriptPath = ResolveExistingFile("VULKANSTORY_HEADLESS_COMMANDS");

    /// <summary>The in-world frame the command script is dispatched on, counted from 0.</summary>
    public static readonly long CommandFrame = ResolveLong("VULKANSTORY_HEADLESS_COMMAND_FRAME", 30L);

    /// <summary>Seconds per simulated frame, or 0 when the wall clock keeps driving it.</summary>
    public static readonly float FixedDeltaTime = ResolveFixedDeltaTime();

    /// <summary>The absolute directory frames are written to, or null when no frames are wanted.</summary>
    public static readonly string? FrameDirectory = ResolveDirectory("VULKANSTORY_HEADLESS_FRAMES");

    /// <summary>The validated scenario, or null when the bounded scenario runner is not active.</summary>
    internal static readonly HeadlessScenario? Scenario = ResolveScenario();

    /// <summary>The in-world frames to write, ascending and without duplicates. Never null.</summary>
    public static readonly long[] Frames = ResolveFrames();

    /// <summary>True when frames will be written.</summary>
    public static readonly bool CaptureEnabled = FrameDirectory != null && Frames.Length > 0;

    /// <summary>
    /// True when <c>VULKANSTORY_HEADLESS_EXIT_WHEN_DONE</c> asks the client to close
    /// itself once the capture (and the parity dump, if one was asked for) has
    /// finished, instead of waiting to be signalled.
    ///
    /// <para>Why this exists.</para> A headless window is never mapped, so the
    /// clean close a human gets - the window manager's close event - cannot be
    /// delivered to it: <c>scripts/dev/kill-client.sh</c> finds nothing to send
    /// alt+F4 to and falls through to SIGTERM. SIGTERM lands on a signal-handler
    /// thread, which calls <c>WindowExit</c> - and therefore <c>Close()</c> - from
    /// off the render thread, while that thread is still inside a frame. The
    /// result is a shutdown race that writes a crash report on every run
    /// (2026-09-12: <c>ShaderProgramBase.Use</c> dereferencing a program whose
    /// graphics were already torn down), which is exactly the noise that makes a
    /// harness useless as evidence - nobody can tell that crash from a real one.
    ///
    /// <para>Closing from here instead is the path the main menu's quit button
    /// already takes: <c>WindowExit</c> on the render thread, between frames, with
    /// the game loop agreeing to stop rather than being interrupted.</para>
    /// </summary>
    public static readonly bool ExitWhenDone = ResolveFlag("VULKANSTORY_HEADLESS_EXIT_WHEN_DONE");

    /// <summary>
    /// True when the client has to do anything at all per frame for the harness.
    /// The single test the render loop makes.
    /// </summary>
    public static readonly bool Active = Enabled || CaptureEnabled || CommandScriptPath != null
        || FixedDeltaTime > 0f || Scenario != null;

    private static bool ResolveFlag(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();
        return value != "0" && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveExistingFile(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return null;
        string full = System.IO.Path.GetFullPath(value);
        return System.IO.File.Exists(full) ? full : null;
    }

    private static HeadlessScenario? ResolveScenario()
    {
        string? value = Environment.GetEnvironmentVariable("VULKANSTORY_HEADLESS_SCENARIO");
        if (string.IsNullOrWhiteSpace(value)) return null;
        return HeadlessScenario.Load(System.IO.Path.GetFullPath(value));
    }

    private static string? ResolveDirectory(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value) || !System.IO.Path.IsPathRooted(value)) return null;
        return System.IO.Path.GetFullPath(value);
    }

    private static long ResolveLong(string name, long fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return long.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out long parsed) && parsed >= 0 ? parsed : fallback;
    }

    private static float ResolveFixedDeltaTime()
    {
        string? value = Environment.GetEnvironmentVariable("VULKANSTORY_HEADLESS_FIXED_DT");
        if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float seconds)) return 0f;
        // A negative or absurd step would make the simulation meaningless rather
        // than reproducible; one second per frame is already far past useful.
        return seconds > 0f && seconds <= 1f ? seconds : 0f;
    }

    private static long[] ResolveFrames()
    {
        string? list = Environment.GetEnvironmentVariable("VULKANSTORY_HEADLESS_FRAME_LIST");
        if (!string.IsNullOrWhiteSpace(list)) return ParseFrameList(list);

        long count = ResolveLong("VULKANSTORY_HEADLESS_FRAME_COUNT", 0L);
        if (count <= 0L) return new long[0];
        // Default: the first frame after the command script has run, so a camera
        // started by the script is already moving on frame one of the capture.
        return PlanFrames(
            ResolveLong("VULKANSTORY_HEADLESS_FIRST_FRAME", CommandScriptPath != null ? CommandFrame + 1L : 0L),
            count,
            ResolveLong("VULKANSTORY_HEADLESS_FRAME_STRIDE", 1L));
    }

    /// <summary>
    /// The most frames one capture may plan. Past this the number is a typo or a
    /// stray environment variable, not a request: the array alone would be
    /// gigabytes, and it is allocated in a static initialiser, so failing it takes
    /// the client down with a TypeInitializationException instead of a bad capture.
    /// Clamping caps <c>first</c> and <c>stride</c> too, which keeps
    /// <c>first + i * stride</c> far away from overflowing.
    /// </summary>
    public const long MaxFrames = 100000L;

    /// <summary>A cadence: <paramref name="count" /> frames from <paramref name="first" />, every <paramref name="stride" />.</summary>
    /// <returns>Ordered capture frame numbers, or an empty array for nonpositive count.</returns>
    public static long[] PlanFrames(long first, long count, long stride)
    {
        if (count <= 0L) return new long[0];
        if (count > MaxFrames) count = MaxFrames;
        if (stride <= 0L) stride = 1L;
        if (stride > MaxFrames) stride = MaxFrames;
        if (first < 0L) first = 0L;
        if (first > MaxFrames * MaxFrames) first = MaxFrames * MaxFrames;
        long[] frames = new long[count];
        for (long i = 0; i < count; i++) frames[i] = first + i * stride;
        return frames;
    }

    /// <summary>
    /// An explicit frame list: comma, space or semicolon separated, negatives and
    /// unparsable entries dropped, ascending and without duplicates.
    /// </summary>
    /// <param name="list">Comma, space or semicolon-separated zero-based frame indices.</param>
    /// <returns>Sorted unique nonnegative parsed indices; malformed entries are omitted.</returns>
    public static long[] ParseFrameList(string list)
    {
        if (string.IsNullOrWhiteSpace(list)) return new long[0];
        string[] parts = list.Split(new char[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
        var frames = new System.Collections.Generic.List<long>(parts.Length);
        for (int i = 0; i < parts.Length; i++)
        {
            if (long.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out long frame) && frame >= 0
                && !frames.Contains(frame))
            {
                frames.Add(frame);
            }
        }
        frames.Sort();
        return frames.ToArray();
    }

    /// <summary>True when this in-world frame is one of the frames to write.</summary>
    public static bool ShouldCapture(long worldFrame) => ShouldCapture(Frames, worldFrame);

    /// <summary>True when this in-world frame is one of <paramref name="frames" /> (ascending).</summary>
    public static bool ShouldCapture(long[] frames, long worldFrame)
    {
        if (frames == null) return false;
        for (int i = 0; i < frames.Length; i++)
        {
            if (frames[i] == worldFrame) return true;
            if (frames[i] > worldFrame) return false;
        }
        return false;
    }

    /// <summary>True once this in-world frame is at or past the last frame to write.</summary>
    public static bool CaptureFinished(long worldFrame) => CaptureFinished(Frames, worldFrame);

    /// <summary>True once this in-world frame is at or past the last of <paramref name="frames" />.</summary>
    public static bool CaptureFinished(long[] frames, long worldFrame)
    {
        return frames == null || frames.Length == 0 || worldFrame >= frames[frames.Length - 1];
    }

    /// <summary>The one file name both backends write, so two captures pair by name.</summary>
    /// <param name="worldFrame">Zero-based rendered frame index.</param>
    /// <returns>Invariant frame-NNNNNN.ppm basename.</returns>
    public static string FrameFileName(long worldFrame)
    {
        return "frame-" + worldFrame.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + ".ppm";
    }

    /// <summary>
    /// The command script's lines, blank lines and <c>#</c> comments removed.
    /// Empty when there is no script or it cannot be read - a capture that loses
    /// its scene is worth a warning, not a crashed client.
    /// </summary>
    public static string[] ReadCommands() => ReadCommands(CommandScriptPath);

    /// <summary>The command lines of one script file; empty when it cannot be read.</summary>
    public static string[] ReadCommands(string? path)
    {
        if (path == null) return new string[0];
        string[] lines;
        try
        {
            lines = System.IO.File.ReadAllLines(path);
        }
        catch (Exception)
        {
            return new string[0];
        }
        var commands = new System.Collections.Generic.List<string>(lines.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            commands.Add(line);
        }
        return commands.ToArray();
    }

    /// <summary>
    /// Writes one presented frame into <see cref="FrameDirectory" />. Returns
    /// false when nothing was written.
    /// </summary>
    public static bool WriteFrame(long worldFrame, int width, int height, byte[] pixels, bool bgra)
    {
        if (FrameDirectory == null) return false;
        return WriteFrame(
            System.IO.Path.Combine(FrameDirectory, FrameFileName(worldFrame)), width, height, pixels, bgra);
    }
    /// <summary>Writes tightly packed four-channel pixels as an RGB Netpbm frame.</summary>
    /// <param name="path">Output pathname; its parent directory is created when needed.</param>
    /// <param name="width">Positive image width in pixels.</param>
    /// <param name="height">Positive image height in pixels.</param>
    /// <param name="pixels">Row-major RGBA/BGRA data containing at least width times height times four bytes.</param>
    /// <param name="bgra">True for BGRA source order; false for RGBA.</param>
    /// <returns>True after file writing, false for malformed input.</returns>
    /// <remarks>The writer strips alpha and preserves source row order. File errors propagate.</remarks>
    public static bool WriteFrame(string path, int width, int height, byte[] pixels, bool bgra)
    {
        if (path == null || pixels == null || width <= 0 || height <= 0) return false;
        if (pixels.LongLength < (long)width * height * 4) return false;
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) System.IO.Directory.CreateDirectory(directory);
        // BGRA: start at B's neighbour R (index 2) and walk backwards, so the file
        // gets R, G, B either way.
        WriteNetpbm(path, width, height, pixels, bgra ? 2 : 0, 3, bgra ? -1 : 1);
        return true;
    }

    /// <summary>Writes selected channels from four-byte texels, preserving the capture row order.</summary>
    internal static void WriteNetpbm(string path, int width, int height, byte[] rgba, int firstChannel, int channels,
        int step = 1)
    {
        using var file = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write);
        byte[] header = System.Text.Encoding.ASCII.GetBytes(
            (channels == 3 ? "P6\n" : "P5\n") + width + " " + height + "\n255\n");
        file.Write(header, 0, header.Length);
        byte[] row = new byte[width * channels];
        for (int y = 0; y < height; y++)
        {
            int source = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < channels; c++)
                {
                    row[x * channels + c] = rgba[source + x * 4 + firstChannel + c * step];
                }
            }
            file.Write(row, 0, row.Length);
        }
    }

}
