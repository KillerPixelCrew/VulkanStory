using System.Text.Json;

namespace VulkanStory.Game;

/// <summary>The original startup member operands pinned to the official 1.22.7 assembly.</summary>
public static class StartupIlProfile
{
    private sealed record Document(string ProfileId, string GameAssemblySha256, StartupMethodInventory[] Methods);
    private static readonly Document Expected = Load();

    /// <summary>The selected OS profile; both official 1.22.7 packages contain the identical pinned game library.</summary>
    public static string Id => OperatingSystem.IsLinux() ? "vs-1.22.7-linux-x64" : Expected.ProfileId;
    public static string GameAssemblySha256 => Expected.GameAssemblySha256;

    /// <summary>
    /// Checks original-body anchors before patch binding. A later transpiler must
    /// also check its incoming instructions, since another Harmony owner may alter them.
    /// </summary>
    /// <param name="actual">Original-body inventory captured before patch installation.</param>
    /// <remarks>Target identity, IL lengths and ordered member uses must match. Incoming Harmony instructions still require separate transpiler checks.</remarks>
    public static void VerifyProfile1227(IReadOnlyList<StartupMethodInventory> actual) =>
        Verify(Expected.Methods, actual);

    internal static void Verify(IReadOnlyList<StartupMethodInventory> expected,
        IReadOnlyList<StartupMethodInventory> actual)
    {
        if (actual.Count != expected.Count)
            throw new InvalidOperationException($"Startup IL profile expected {expected.Count} methods; found {actual.Count}.");

        var byEvent = new Dictionary<string, StartupMethodInventory>(StringComparer.Ordinal);
        foreach (StartupMethodInventory method in actual)
            if (!byEvent.TryAdd(method.Event, method))
                throw new InvalidOperationException("Duplicate startup IL target: " + method.Event);

        foreach (StartupMethodInventory required in expected)
        {
            if (!byEvent.TryGetValue(required.Event, out StartupMethodInventory? found))
                throw new InvalidOperationException("Missing startup IL target: " + required.Event);
            if (found.Target != required.Target || found.IlLength != required.IlLength)
                throw new InvalidOperationException($"Startup IL shape changed for {required.Event}: " +
                    $"expected {required.Target} with {required.IlLength} bytes; found {found.Target} with {found.IlLength} bytes.");
            if (found.Uses.Count != required.Uses.Count)
                throw new InvalidOperationException($"Startup IL member count changed for {required.Event}: " +
                    $"expected {required.Uses.Count}; found {found.Uses.Count}.");

            for (int index = 0; index < required.Uses.Count; index++)
            {
                StartupIlUse anchor = required.Uses[index];
                if (found.Uses[index] != anchor)
                    throw new InvalidOperationException($"Startup IL anchor changed for {required.Event} at 0x{anchor.Offset:x}: " +
                        $"expected {anchor.Opcode} {anchor.Member}; found {found.Uses[index].Opcode} {found.Uses[index].Member} " +
                        $"at 0x{found.Uses[index].Offset:x}.");
            }
        }
    }

    private static Document Load()
    {
        using Stream stream = typeof(StartupIlProfile).Assembly.GetManifestResourceStream(
            "VulkanStory.Game.Profiles.Startup1227.json") ??
            throw new InvalidOperationException("Embedded startup IL profile is missing.");
        Document document = JsonSerializer.Deserialize<Document>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
            throw new InvalidDataException("Embedded startup IL profile is empty.");
        if (document.ProfileId != "vs-1.22.7-win-x64" || document.Methods.Length != StartupTargets.Profile1227.Length)
            throw new InvalidDataException("Embedded startup IL profile identity is invalid.");
        // Static archive inspection verified the complete library hash on both
        // platforms, which also proves every recorded IL operand is shared.
        if (document.GameAssemblySha256 != "E08F22B493B92FEAF0AAEB79D22437EA0F7EFC38AA7F72A04A47F98BC0E40DF0")
            throw new InvalidDataException("Shared Windows/Linux startup IL library identity is invalid.");
        return document;
    }
}
