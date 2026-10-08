using System.Security.Cryptography;
using Mono.Cecil;
using Vintagestory.API.Common;

namespace VulkanStory.Game;

/// <summary>One metadata-visible graphics operation and its declaring or calling IL location.</summary>
/// <param name="Caller">Complete managed caller signature, or the native-import declaration when no local call exists.</param>
/// <param name="Offset">Caller IL offset; null identifies an imported entry-point declaration.</param>
/// <param name="Operation">Exact managed signature or native library and entry point.</param>
internal sealed record ModGlCall(string Caller, int? Offset, string Operation)
{
    /// <summary>Formats a call site without discarding overload or native-entry-point information.</summary>
    internal string Detail => $"{Caller} at {(Offset is int offset ? $"IL_{offset:x4}" : "import declaration")}: {Operation}";
}

/// <summary>Assembly identity and bounded direct-GL discovery; an empty operation list is not compatibility acceptance.</summary>
/// <param name="Info">Official mod metadata, including the source-mod attribute identity when available.</param>
/// <param name="Source">Assembly path or emitted-source label.</param>
/// <param name="AssemblyName">Full managed assembly identity.</param>
/// <param name="Mvid">Main-module identity read without loading the mod.</param>
/// <param name="Sha256">SHA256 of the inspected bytes.</param>
/// <param name="Calls">Unsupported metadata-visible GL operations.</param>
/// <param name="Failure">Metadata-read failure, leaving discovery incomplete.</param>
internal sealed record ModGlInspection(ModInfo? Info, string Source, string AssemblyName, Guid Mvid,
    string Sha256, IReadOnlyList<ModGlCall> Calls, string? Failure)
{
    /// <summary>True only when an unsupported graphics operation was observed.</summary>
    internal bool Refused => Calls.Count != 0;
    /// <summary>Mod-manager identity retained from the game's own metadata reader.</summary>
    internal string Identity => $"{Info?.ModID ?? "unknown-mod"}@{Info?.Version ?? "unknown-version"}";
    /// <summary>Explicit refusal or a bounded non-acceptance result.</summary>
    internal string Decision => (Calls.Count != 0 ? $"refused: {Calls.Count} unsupported direct GL operation(s); an exact mod adapter is required" :
        Failure != null ? "inspection incomplete; original loader behavior retained; compatibility unverified" :
        "no detectable direct GL; compatibility unverified") + (Failure != null ? "; metadata error: " + Failure : "");
    /// <summary>Formats image identity and every discovered operation for logs and the status command.</summary>
    internal string Detail => $"{Identity}: {Decision}; {Source}; assembly={AssemblyName}; MVID={Mvid}; SHA256={Sha256}" +
        string.Concat(Calls.Select(call => "\n  " + call.Detail));
}

/// <summary>Reads managed mod bytes for direct GL calls and native imports without executing target code.</summary>
/// <remarks>Reflection, dynamically assembled symbols, custom loaders, external managed dependencies and native dependency internals are outside this metadata scan.</remarks>
internal static class ModGlUsage
{
    /// <summary>Inspects the exact image the game is about to load and optionally obtains its source-mod identity through the official metadata parser.</summary>
    /// <param name="image">Managed assembly bytes, including freshly emitted source-mod bytes.</param>
    /// <param name="source">Path or source-emission label for the diagnostic.</param>
    /// <param name="info">Already selected official mod metadata, if discovery has assigned it.</param>
    /// <param name="readInfo">Official metadata-only identity reader used when source compilation precedes mod-info assignment.</param>
    /// <returns>The image identity, all detected operation sites, and any inspection failure.</returns>
    internal static ModGlInspection Inspect(byte[] image, string source, ModInfo? info,
        System.Func<AssemblyDefinition, ModInfo?>? readInfo = null)
    {
        string hash = Convert.ToHexString(SHA256.HashData(image));
        string name = "unreadable";
        Guid mvid = Guid.Empty;
        var calls = new List<ModGlCall>();
        try
        {
            using var stream = new MemoryStream(image, writable: false);
            using var assembly = AssemblyDefinition.ReadAssembly(stream,
                new ReaderParameters { ReadSymbols = false, InMemory = true });
            name = assembly.Name.FullName;
            mvid = assembly.MainModule.Mvid;
            foreach (var module in assembly.Modules)
            {
                MethodDefinition[] methods = module.GetTypes().SelectMany(type => type.Methods).ToArray();
                var imports = methods.Where(method => method.HasPInvokeInfo && IsGlImport(method.PInvokeInfo))
                    .ToDictionary(method => method.FullName, method =>
                        $"{method.PInvokeInfo.Module.Name}!{method.PInvokeInfo.EntryPoint}", StringComparer.Ordinal);
                var calledImports = new HashSet<string>(StringComparer.Ordinal);
                foreach (var method in methods.Where(method => method.HasBody))
                {
                    foreach (var instruction in method.Body.Instructions)
                    {
                        // Include ldftn/ldvirtftn as well as calls: delegates can
                        // execute the same native operation after initialization.
                        if (instruction.Operand is not MethodReference target) continue;
                        if (imports.TryGetValue(target.FullName, out string? entry))
                        {
                            calls.Add(new(method.FullName, instruction.Offset, entry));
                            calledImports.Add(target.FullName);
                        }
                        else if (IsManagedGl(target))
                            calls.Add(new(method.FullName, instruction.Offset, target.FullName));
                    }
                }
                foreach (var import in imports.Where(import => !calledImports.Contains(import.Key)))
                    calls.Add(new(import.Key, null, import.Value));
            }
            info ??= readInfo?.Invoke(assembly);
            return new(info, source, name, mvid, hash, calls, null);
        }
        catch (Exception error) when (error is BadImageFormatException or IOException or ArgumentException or
            InvalidOperationException or NotSupportedException or IndexOutOfRangeException or AssemblyResolutionException)
        {
            return new(info, source, name, mvid, hash, calls, error.Message);
        }
    }

    /// <summary>Recognizes GL bindings and exposed graphics procedure loaders by their exact metadata owner.</summary>
    /// <param name="method">Referenced managed method; no external assembly resolution is performed.</param>
    /// <returns>True when the method belongs to a GL binding or known graphics procedure loader.</returns>
    private static bool IsManagedGl(MethodReference method)
    {
        string type = method.DeclaringType.FullName;
        return ((type.StartsWith("OpenTK.Graphics.OpenGL", StringComparison.Ordinal) ||
                 type.StartsWith("OpenTK.Graphics.ES", StringComparison.Ordinal)) &&
                (method.DeclaringType.Name == "GL" || type.Contains(".GL/", StringComparison.Ordinal))) ||
            type is "Silk.NET.OpenGL.GL" or "Silk.NET.OpenGLES.GL" or "OpenGL.Gl" ||
            (type == "OpenTK.Windowing.GraphicsLibraryFramework.GLFW" &&
                method.Name is "GetProcAddress" or "MakeContextCurrent" or "SwapBuffers") ||
            (method.Name == "GetProcAddress" &&
                (type.StartsWith("OpenTK.", StringComparison.Ordinal) ||
                 type.StartsWith("Silk.NET.Core.Contexts.", StringComparison.Ordinal)));
    }

    /// <summary>Recognizes native GL APIs, context APIs and graphics-specific procedure lookup imports.</summary>
    /// <param name="import">P/Invoke metadata, including its native library and entry point.</param>
    /// <returns>True for a native graphics dependency that needs an explicit adapter.</returns>
    private static bool IsGlImport(PInvokeInfo import)
    {
        string library = Path.GetFileName(import.Module.Name).ToLowerInvariant();
        string entry = import.EntryPoint;
        return library.StartsWith("opengl32", StringComparison.Ordinal) ||
            library.StartsWith("libgl.", StringComparison.Ordinal) ||
            library.StartsWith("libopengl", StringComparison.Ordinal) ||
            library.StartsWith("libgles", StringComparison.Ordinal) ||
            library.StartsWith("libglx", StringComparison.Ordinal) ||
            library.StartsWith("libegl", StringComparison.Ordinal) ||
            library.StartsWith("egl.", StringComparison.Ordinal) ||
            library.StartsWith("glew", StringComparison.Ordinal) ||
            (entry.Length > 2 && entry.StartsWith("gl", StringComparison.Ordinal) && char.IsUpper(entry[2])) ||
            entry.StartsWith("wgl", StringComparison.Ordinal) || entry.StartsWith("egl", StringComparison.Ordinal) ||
            entry.StartsWith("SDL_GL_", StringComparison.Ordinal) ||
            entry is "glfwGetProcAddress" or "glfwMakeContextCurrent" or "glfwSwapBuffers";
    }
}
