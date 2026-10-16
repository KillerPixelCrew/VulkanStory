using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;

namespace VulkanStory.Game;

/// <summary>Routes GL imports and procedure loading in native libraries belonging to inspected mods.</summary>
internal static class NativeGlLibraryRouting
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Roots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<nint, ModuleRoute> Modules = new();
    private static MethodInfo? loadCallback;
    private sealed record ImportPatch(nint Slot, nint Original, nint Replacement);
    private sealed record ModuleRoute(string Path, List<ImportPatch> Patches);
    private static ProcessRuntime? runtime;
    private static int bootstrapLookups;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint ProcedureLoader(nint module, nint name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint GlProcedureLoader(nint name);
    private static readonly ProcedureLoader LoadProcedure = ResolveProcedure;
    private static readonly GlProcedureLoader LoadGlProcedure = ResolveGlProcedure;
    private static readonly nint LoaderPointer = Marshal.GetFunctionPointerForDelegate(LoadProcedure);
    private static readonly nint GlLoaderPointer = Marshal.GetFunctionPointerForDelegate(LoadGlProcedure);

    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern nint GetProcAddress(nint module, nint name);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetModuleFileNameW(nint module, StringBuilder path, int size);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint previous);

    internal static void RegisterImage(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return;
        lock (Gate) Roots.Add(Path.GetFullPath(Path.GetDirectoryName(path)!) + Path.DirectorySeparatorChar);
    }

    internal static void Prepare()
    {
        if (!OperatingSystem.IsWindows()) return;
        MethodInfo callback = typeof(NativeLibrary).GetMethod("LoadLibraryCallbackStub", BindingFlags.Static | BindingFlags.NonPublic,
            null, [typeof(string), typeof(Assembly), typeof(bool), typeof(uint)], null)
            ?? throw new MissingMethodException("CLR native import resolution callback is unavailable.");
        MethodInfo invoke = typeof(DllImportResolver).GetMethod("Invoke")!;
        if (callback.ReturnType != typeof(nint) || callback.GetMethodBody() is not { } body ||
            body.ExceptionHandlingClauses.Count != 0 || PatchProcessor.GetOriginalInstructions(callback).Count(instruction => instruction.Calls(invoke)) != 1)
            throw new NotSupportedException("CLR native import resolution callback does not match the supported return boundary.");
        loadCallback = callback;
    }

    internal static void Install(Harmony harmony, ProcessRuntime owner)
    {
        if (!OperatingSystem.IsWindows()) return;
        runtime = owner;
        if (loadCallback is null) throw new InvalidOperationException("Native import callback was not prepared.");
        foreach (string name in new[] { "glGetString", "glGetIntegerv", "glGetError" })
            if (ManagedGlCallPatches.ProcedureAddress(name) == 0)
                throw new MissingMethodException("Native GL bootstrap procedure is missing: " + name);
        // Native CLR code invokes this callback; it cannot inline it into a mod's
        // caller. Route the returned module before CLR enters its imported function.
        // The mod's own resolver and exception filters are never rewritten.
        harmony.Patch(loadCallback, postfix: new HarmonyMethod(typeof(NativeGlLibraryRouting), nameof(Loaded)) { priority = Priority.First });
        harmony.Patch(AccessTools.Method(typeof(NativeLibrary), nameof(NativeLibrary.Free), [typeof(nint)]),
            postfix: new HarmonyMethod(typeof(NativeGlLibraryRouting), nameof(Freed)) { priority = Priority.First });
        harmony.Patch(AccessTools.Method(typeof(NativeLibrary), nameof(NativeLibrary.GetExport), [typeof(nint), typeof(string)]),
            prefix: new HarmonyMethod(typeof(NativeGlLibraryRouting), nameof(GetExport)) { priority = Priority.First });
        harmony.Patch(AccessTools.Method(typeof(NativeLibrary), nameof(NativeLibrary.TryGetExport), [typeof(nint), typeof(string), typeof(nint).MakeByRefType()]),
            prefix: new HarmonyMethod(typeof(NativeGlLibraryRouting), nameof(TryGetExport)) { priority = Priority.First });
    }

    internal static void Restore()
    {
        lock (Gate)
        {
            foreach (nint module in Modules.Keys.ToArray()) RestoreModule(module);
            Roots.Clear();
            loadCallback = null;
        }
        runtime = null;
    }

    private static string ModulePath(nint module)
    {
        var text = new StringBuilder(32768);
        uint count = GetModuleFileNameW(module, text, text.Capacity);
        return count == 0 || count >= text.Capacity ? "" : text.ToString();
    }

    private static void Loaded(nint __result)
    {
        if (runtime?.HasCommitted != true || __result == 0 || !Environment.Is64BitProcess) return;
        string path = ModulePath(__result);
        lock (Gate)
        {
            if (Modules.ContainsKey(__result)) return;
            if (!Roots.Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
            {
                TraceBootstrap("native-gl.module-outside-mod-roots", path);
                return;
            }
            var patches = new List<ImportPatch>();
            Modules.Add(__result, new(path, patches));
            try
            {
                using var input = File.OpenRead(path);
                using var pe = new PEReader(input);
                if (pe.PEHeaders.PEHeader is not { Magic: PEMagic.PE32Plus } header) return;
                int imageSize = header.SizeOfImage;
                int importRva = header.ImportTableDirectory.RelativeVirtualAddress;
                if (importRva == 0) return;
                ReadOnlySpan<byte> imports = pe.GetSectionData(importRva).GetContent().AsSpan();
                for (int descriptor = 0; descriptor <= imports.Length - 20; descriptor += 20)
                {
                    int original = BinaryPrimitives.ReadInt32LittleEndian(imports.Slice(descriptor, 4));
                    int name = BinaryPrimitives.ReadInt32LittleEndian(imports.Slice(descriptor + 12, 4));
                    int first = BinaryPrimitives.ReadInt32LittleEndian(imports.Slice(descriptor + 16, 4));
                    if (name == 0 && first == 0) break;
                    string library = ReadName(pe, name);
                    bool gl = IsGlLibrary(library);
                    bool loader = library.Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase) || library.Equals("kernelbase.dll", StringComparison.OrdinalIgnoreCase) ||
                        library.StartsWith("api-ms-win-core-libraryloader-", StringComparison.OrdinalIgnoreCase);
                    if (!gl && !loader) continue;
                    ReadOnlySpan<byte> thunks = pe.GetSectionData(original != 0 ? original : first).GetContent().AsSpan();
                    for (int index = 0; index <= thunks.Length - 8; index += 8)
                    {
                        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(thunks.Slice(index, 8));
                        if (value == 0) break;
                        if ((value & (1UL << 63)) != 0)
                        {
                            if (gl) throw new NotSupportedException("Native GL ordinal imports are not supported.");
                            continue;
                        }
                        if (value > int.MaxValue - 2) throw new BadImageFormatException("Invalid native import name.");
                        string symbol = ReadName(pe, (int)value + 2);
                        nint replacement = loader && symbol == "GetProcAddress" ? LoaderPointer : gl ? GlAddress(symbol) : 0;
                        if (replacement == 0)
                        {
                            if (gl) throw new NotSupportedException("Native GL import is not implemented: " + symbol);
                            continue;
                        }
                        long rva = (long)first + index;
                        if (rva < 0 || rva > imageSize - nint.Size) throw new BadImageFormatException("Native import slot is outside its module.");
                        nint slot = __result + (nint)rva;
                        var patch = new ImportPatch(slot, Marshal.ReadIntPtr(slot), replacement);
                        patches.Add(patch);
                        WriteSlot(slot, replacement);
                    }
                }
                TraceBootstrap("native-gl.library-routed", path + "; imports=" + patches.Count);
            }
            catch
            {
                RestoreModule(__result);
                NativeLibrary.Free(__result); // The throwing Load cannot transfer its owned reference to the caller.
                throw;
            }
        }
    }

    private static bool IsGlLibrary(string name) => name.Equals("opengl32.dll", StringComparison.OrdinalIgnoreCase);
    private static string ReadName(PEReader pe, int rva)
    {
        ReadOnlySpan<byte> data = pe.GetSectionData(rva).GetContent().AsSpan();
        int end = data.IndexOf((byte)0);
        if (end < 0) throw new BadImageFormatException("Unterminated native import name.");
        return Encoding.ASCII.GetString(data[..end]);
    }
    private static void WriteSlot(nint slot, nint value)
    {
        if (!VirtualProtect(slot, (nuint)nint.Size, 0x04, out uint protection))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Native GL import slot is not writable.");
        try { Marshal.WriteIntPtr(slot, value); }
        finally
        {
            if (!VirtualProtect(slot, (nuint)nint.Size, protection, out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Native GL import protection could not be restored.");
        }
    }
    private static void Freed(nint __0)
    {
        // Free releases one reference, and DLL detach may still issue GL cleanup.
        // Keep routing while any reference remains; never touch an unloaded IAT.
        lock (Gate)
            if (Modules.TryGetValue(__0, out ModuleRoute? route) && !ModulePath(__0).Equals(route.Path, StringComparison.OrdinalIgnoreCase))
                Modules.Remove(__0);
    }
    private static void RestoreModule(nint module)
    {
        if (!Modules.TryGetValue(module, out ModuleRoute? route)) return;
        if (!ModulePath(module).Equals(route.Path, StringComparison.OrdinalIgnoreCase)) { Modules.Remove(module); return; }
        List<ImportPatch> patches = route.Patches;
        for (int i = patches.Count - 1; i >= 0; i--)
        {
            ImportPatch patch = patches[i];
            if (Marshal.ReadIntPtr(patch.Slot) == patch.Replacement) WriteSlot(patch.Slot, patch.Original);
        }
        Modules.Remove(module);
    }

    private static void TraceBootstrap(string name, string detail)
    {
        try { runtime?.Notify(name, detail); }
        catch { /* Diagnostics cannot change native loading or procedure results. */ }
    }
    private static nint GlAddress(string name)
    {
        nint address = name == "wglGetProcAddress" ? GlLoaderPointer : ManagedGlCallPatches.ProcedureAddress(name);
        if ((name is "wglGetProcAddress" or "glGetString" or "glGetStringi") && Interlocked.Increment(ref bootstrapLookups) <= 8)
            TraceBootstrap("native-gl.bootstrap-lookup", name + " -> 0x" + address.ToString("x") + "; context=" + (ManagedGlBridge.Current is not null));
        return address;
    }
    private static nint ResolveGlProcedure(nint name)
    {
        try { return runtime?.HasCommitted == true && unchecked((nuint)name) > ushort.MaxValue ? GlAddress(Marshal.PtrToStringUTF8(name) ?? "") : 0; }
        catch { return 0; }
    }
    private static nint ResolveProcedure(nint module, nint name)
    {
        try
        {
            string path = ModulePath(module);
            if (IsGlLibrary(Path.GetFileName(path)))
                return runtime?.HasCommitted == true && unchecked((nuint)name) > ushort.MaxValue ? GlAddress(Marshal.PtrToStringUTF8(name) ?? "") : 0;
            if (unchecked((nuint)name) > ushort.MaxValue && (Marshal.PtrToStringUTF8(name) is "wglGetProcAddress" or "glGetString") &&
                Interlocked.Increment(ref bootstrapLookups) <= 8)
                TraceBootstrap("native-gl.bootstrap-outside-gl-module", path + "; module=0x" + module.ToString("x"));
            return GetProcAddress(module, name);
        }
        catch { return 0; }
    }

    private static bool GetExport(nint __0, string __1, ref nint __result)
    {
        if (runtime?.HasCommitted != true || !IsGlLibrary(Path.GetFileName(ModulePath(__0)))) return true;
        __result = GlAddress(__1);
        if (__result == 0) throw new EntryPointNotFoundException("Native GL procedure is not implemented: " + __1);
        return false;
    }
    private static bool TryGetExport(nint __0, string __1, ref nint __2, ref bool __result)
    {
        if (runtime?.HasCommitted != true || !IsGlLibrary(Path.GetFileName(ModulePath(__0)))) return true;
        __2 = GlAddress(__1);
        __result = __2 != 0;
        return false;
    }
}
