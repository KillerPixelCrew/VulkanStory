using Microsoft.Win32;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Install;

public sealed record GameInstallation(string Directory, string? Version);

/// <summary>Read-only discovery; a null version must never imply pack compatibility.</summary>
public sealed class GameInstallProbe(ISystemProbe probe)
{
    public IReadOnlyList<GameInstallation> Detect(IEnumerable<string>? additionalDirectories = null) =>
        Detect(additionalDirectories, ReadVersion,
            probe.Os == OsKind.Windows ? WindowsRegistryDirectories() : []);

    internal IReadOnlyList<GameInstallation> Detect(
        IEnumerable<string>? additionalDirectories, Func<string, string?> readVersion,
        IEnumerable<string>? registryDirectories = null)
    {
        var comparer = probe.Os == OsKind.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        var found = new List<GameInstallation>();
        foreach (string candidate in (additionalDirectories ?? []).Concat(registryDirectories ?? []).Concat(Candidates()))
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathRooted(candidate)) continue;
            string directory;
            try { directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (!seen.Add(directory) || !probe.DirectoryExists(directory)) continue;
            // Exclude data folders, server-only installs and partial extractions.
            if (!new[] { "Vintagestory.dll", "VintagestoryLib.dll", "VintagestoryAPI.dll" }
                .All(file => probe.FileExists(Path.Combine(directory, file)))) continue;
            found.Add(new(directory, readVersion(Path.Combine(directory, "VintagestoryAPI.dll"))));
        }
        return found;
    }

    private static IReadOnlyList<string> WindowsRegistryDirectories()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var directories = new List<string>();
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
                using RegistryKey? entries = root.OpenSubKey(uninstall);
                if (entries is null) continue;
                foreach (string name in entries.GetSubKeyNames())
                {
                    using RegistryKey? entry = entries.OpenSubKey(name);
                    if (entry is null) continue;
                    string? displayName = entry.GetValue("DisplayName") as string;
                    if (displayName is null ||
                        !(displayName.StartsWith("Vintage Story", StringComparison.OrdinalIgnoreCase) ||
                          displayName.StartsWith("Vintagestory", StringComparison.OrdinalIgnoreCase))) continue;
                    string? directory = entry.GetValue("InstallLocation") as string;
                    if (string.IsNullOrWhiteSpace(directory))
                        directory = entry.GetValue("Inno Setup: App Path") as string;
                    if (!string.IsNullOrWhiteSpace(directory)) directories.Add(directory);
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                // An inaccessible registry view must not prevent manual selection.
            }
        }
        return directories;
    }

    private IEnumerable<string> Candidates()
    {
        if (probe.Os == OsKind.Windows)
        {
            foreach (string variable in new[] { "APPDATA", "LOCALAPPDATA", "ProgramFiles", "ProgramFiles(x86)" })
            {
                string? root = probe.GetEnvironmentVariable(variable);
                if (string.IsNullOrWhiteSpace(root)) continue;
                foreach (string name in new[] { "Vintagestory", "Vintage Story" })
                {
                    yield return Path.Combine(root, name);
                    if (variable == "LOCALAPPDATA") yield return Path.Combine(root, "Programs", name);
                }
            }
            foreach (string name in new[] { "Vintagestory", "Vintage Story" })
            {
                yield return Path.Combine(probe.HomeDirectory, name);
                yield return Path.Combine(probe.HomeDirectory, "Games", name);
            }
        }
        else
        {
            yield return Path.Combine(probe.HomeDirectory, "vintagestory");
            yield return Path.Combine(probe.HomeDirectory, ".local", "share", "vintagestory");
            yield return "/opt/vintagestory";
            yield return "/usr/share/vintagestory";
        }
    }

    // Read the literal, preserving rc/pre suffixes, without loading or executing
    // game assemblies (or resolving their dependencies).
    internal static string? ReadVersion(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return null;
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.TypeDefinitions)
            {
                var type = metadata.GetTypeDefinition(handle);
                if (metadata.GetString(type.Namespace) != "Vintagestory.API.Config" ||
                    metadata.GetString(type.Name) != "GameVersion") continue;
                foreach (var fieldHandle in type.GetFields())
                {
                    var field = metadata.GetFieldDefinition(fieldHandle);
                    if (metadata.GetString(field.Name) != "ShortGameVersion") continue;
                    var constantHandle = field.GetDefaultValue();
                    if (constantHandle.IsNil) return null;
                    var constant = metadata.GetConstant(constantHandle);
                    if (constant.TypeCode != ConstantTypeCode.String) return null;
                    var blob = metadata.GetBlobReader(constant.Value);
                    string value = blob.ReadUTF16(blob.Length);
                    return string.IsNullOrWhiteSpace(value) ? null : value;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException) { }
        return null;
    }
}
