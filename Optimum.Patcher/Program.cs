using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;
using Optimum.Patcher;

if (args.Length == 2 && args[0] == "--list-cecil-owned")
{
    string root = Path.GetFullPath(args[1]);
    if (!Directory.Exists(Path.Combine(root, "patches", "VintagestoryLib")))
    {
        Console.Error.WriteLine($"No patch tree in {root}");
        return 1;
    }
    foreach (string path in PatchManifest.Create().CecilOwnedPatchPaths(root))
        Console.WriteLine(path);
    return 0;
}

// Verification mode for check-vanilla-compat.sh: report decompiler
// type-misbinding artifacts (same-named cast target bound to the wrong
// namespace, the EventHelper System.Func/API Func class of bug).
if (args.Length == 3 && args[0] == "--compare-casts")
{
    if (!File.Exists(args[1])) { Console.Error.WriteLine($"Not found: {args[1]}"); return 1; }
    if (!File.Exists(args[2])) { Console.Error.WriteLine($"Not found: {args[2]}"); return 1; }
    var divergences = CastComparer.Compare(args[1], args[2]);
    foreach (var d in divergences)
        Console.Error.WriteLine($"  CAST DIVERGENCE: {d}");
    Console.WriteLine($"{divergences.Count} cast divergence(s) between {Path.GetFileName(args[1])} and {Path.GetFileName(args[2])}");
    return divergences.Count == 0 ? 0 : 1;
}

if (args.Length == 4 && args[0] == "--api")
{
    return ApiPatcher.Patch(args[1], args[2], args[3]) ? 0 : 1;
}

if (args.Length == 5 && args[0] == "--mod")
{
    return ModPatcher.Patch(args[1], args[2], args[3], args[4]) ? 0 : 1;
}

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: Optimum.Patcher <vanilla.dll> <compiled.dll> <output.dll>");
    Console.Error.WriteLine("       Optimum.Patcher --compare-casts <vanilla.dll> <compiled.dll>");
    Console.Error.WriteLine("       Optimum.Patcher --api <vanilla.dll> <contracts.dll> <output.dll>");
    Console.Error.WriteLine("       Optimum.Patcher --mod <name> <vanilla.dll> <donor.dll> <output.dll>");
    Console.Error.WriteLine("       Optimum.Patcher --list-cecil-owned <repository root>");
    return 1;
}

string vanillaPath = args[0];
string compiledPath = args[1];
string outputPath = args[2];

if (!File.Exists(vanillaPath)) { Console.Error.WriteLine($"Not found: {vanillaPath}"); return 1; }
if (!File.Exists(compiledPath)) { Console.Error.WriteLine($"Not found: {compiledPath}"); return 1; }

Console.WriteLine($"Patching {Path.GetFileName(vanillaPath)}...");

PatchManifest manifest = PatchManifest.Create();

int total = ILPatcher.PatchWithInjection(
    vanillaPath, compiledPath, outputPath,
    manifest.TypesToInject, manifest.MembersToInject, manifest.Targets,
    manifest.Hooks,
    fieldsToRetype: manifest.FieldsToRetype,
    typesToUnseal: manifest.TypesToUnseal,
    methodsToVirtualize: manifest.MethodsToVirtualize);

Console.WriteLine($"\nDone.");
return total > 0 ? 0 : 1;
