using System.Text.Json;

namespace VulkanStory.Game;

/// <summary>Startup member operands pinned separately to each accepted 1.22.7 assembly.</summary>
public static class StartupIlProfile
{
    private sealed record Document(string ProfileId, string GameAssemblySha256, StartupMethodInventory[] Methods);
    private static readonly Document Expected = Load("Startup1227.json",
        "E08F22B493B92FEAF0AAEB79D22437EA0F7EFC38AA7F72A04A47F98BC0E40DF0");
    private static readonly Document Serpih = Load("Startup1227Serpih.json",
        "5633179A6AC6FDF3CAF6688DC250BF4A655893729CCA137C3500DD44CAEFC2A9");

    /// <summary>The selected OS profile; both official 1.22.7 packages contain the identical pinned game library.</summary>
    public static string Id => OperatingSystem.IsLinux() ? "vs-1.22.7-linux-x64" : Expected.ProfileId;
    public static string GameAssemblySha256 => Expected.GameAssemblySha256;

    /// <summary>
    /// Checks original-body anchors before patch binding. A later transpiler must
    /// also check its incoming instructions, since another Harmony owner may alter them.
    /// </summary>
    /// <param name="actual">Original-body inventory captured before patch installation.</param>
    /// <param name="gameAssemblySha256">Exact library identity selecting its pinned operands; null selects the official profile.</param>
    /// <remarks>Target identity, IL lengths and ordered member uses must match. Incoming Harmony instructions still require separate transpiler checks.</remarks>
    public static void VerifyProfile1227(IReadOnlyList<StartupMethodInventory> actual, string? gameAssemblySha256 = null)
    {
        Document expected = gameAssemblySha256 is null ||
            string.Equals(gameAssemblySha256, Expected.GameAssemblySha256, StringComparison.OrdinalIgnoreCase)
            ? Expected
            : string.Equals(gameAssemblySha256, Serpih.GameAssemblySha256, StringComparison.OrdinalIgnoreCase)
                ? Serpih
                : throw new InvalidOperationException("No startup IL profile for game assembly " + gameAssemblySha256);
        Verify(expected.Methods, actual);
    }

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

    private static Document Load(string resource, string sha256)
    {
        using Stream stream = typeof(StartupIlProfile).Assembly.GetManifestResourceStream(
            "VulkanStory.Game.Profiles." + resource) ??
            throw new InvalidOperationException("Embedded startup IL profile is missing.");
        Document document = JsonSerializer.Deserialize<Document>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
            throw new InvalidDataException("Embedded startup IL profile is empty.");
        if (document.ProfileId != "vs-1.22.7-win-x64" || document.Methods.Length != StartupTargets.Profile1227.Length)
            throw new InvalidDataException("Embedded startup IL profile identity is invalid.");
        if (document.GameAssemblySha256 != sha256)
            throw new InvalidDataException("Embedded startup IL library identity is invalid.");
        return document;
    }
}
