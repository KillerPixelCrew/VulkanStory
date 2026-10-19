using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Render.Vulkan.Shaders;

/// <summary>
/// A deliberately narrow binary variant: change only typed addresses of per-draw
/// loads, retaining the original native or translated instructions and specialization.
/// SPIR-V grammar: KhronosGroup/SPIRV-Headers, spirv.core.grammar.json.
/// InstanceIndex is a scalar vertex input starting at firstInstance (Vulkan spec).
/// </summary>
internal static class EntityRecordShader
{
    internal const string BlockName = "VulkanEntityDrawRecords";
    internal static int PushOffset(ShaderProgramResources source) => (source.UniformShadow.Length + 15) & ~15;
    internal static int Stride(ShaderProgramResources source) =>
        (PushOffset(source) + source.Interface.PushConstantSize + 15) & ~15;

    internal static bool TryCreate(ShaderProgramResources source, out TranslatedProgram? variant,
        out int varyingComponents)
    {
        variant = null; varyingComponents = 0;
        if (source.OriginalSpirv.Count != 2 || source.Interface.StorageBlocks.Count != 0 ||
            source.Interface.UniformBlocks.Count != 0 || source.UniformShadow.Length == 0 ||
            source.Interface.PushConstantSize < 0 || source.Interface.PushConstantSize > SetConvention.PushConstantBytes ||
            Stride(source) <= 0 || Stride(source) > 65536 ||
            !source.OriginalSpirv.TryGetValue(ShaderStageKind.VertexShader, out var vertex) ||
            !source.OriginalSpirv.TryGetValue(ShaderStageKind.FragmentShader, out var fragment)) return false;
        var vr = SpirvReflection.Reflect(vertex);
        var fr = SpirvReflection.Reflect(fragment);
        int location = 0;
        foreach (var v in vr.Outputs.Concat(fr.Inputs))
        {
            // Reserve the whole location span, including matrices and fixed arrays.
            int columns = v.GlslType.StartsWith("mat", StringComparison.Ordinal) && v.GlslType.Length > 3 &&
                char.IsDigit(v.GlslType[3]) ? v.GlslType[3] - '0' : 1;
            if (v.ScalarWidth > 32 || v.ArrayDimensions > 1 || v.ArrayLength < 0) return false;
            location = Math.Max(location, v.Location + columns * Math.Max(1, v.ArrayLength));
        }
        if (location >= 128) return false;
        varyingComponents = (location + 1) * 4;
        if (!TryStage(vertex, vr, source, true, (uint)location, out var vs) ||
            !TryStage(fragment, fr, source, false, (uint)location, out var fs)) return false;
        variant = new TranslatedProgram
        {
            Layout = source.Interface.WithEntityRecords(), Specialization = source.Specialization,
            IsNative = source.IsNative, WorldWarpWaving = source.WorldWarpWaving,
        };
        variant.Spirv.Add(ShaderStageKind.VertexShader, vs);
        variant.Spirv.Add(ShaderStageKind.FragmentShader, fs);
        return true;
    }

    private static uint[] Instruction(uint op, params uint[] operands)
    {
        var words = new uint[operands.Length + 1];
        words[0] = ((uint)words.Length << 16) | op;
        operands.CopyTo(words, 1);
        return words;
    }

    private static uint[] Name(uint id, string name)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(name);
        byte[] padded = new byte[(utf8.Length + 4) & ~3];
        utf8.CopyTo(padded, 0);
        var operands = new List<uint> { id };
        operands.AddRange(MemoryMarshal.Cast<byte, uint>(padded).ToArray());
        return Instruction(5, operands.ToArray());
    }

    private static bool TryStage(byte[] binary, SpirvModuleReflection reflection,
        ShaderProgramResources source, bool vertex, uint location, out byte[] result)
    {
        result = [];
        if (binary.Length % 4 != 0 || binary.Length < 20) return false;
        uint[] words = MemoryMarshal.Cast<byte, uint>(binary).ToArray();
        // StorageBuffer is core in SPIR-V 1.3. Preserve the module version and capabilities.
        if (words[0] != 0x07230203 || words[1] < 0x00010300 || words[1] > 0x00010600 || words[3] > 1000000)
            return false;
        var code = new List<uint[]>();
        for (int at = 5; at < words.Length;)
        {
            int n = (int)(words[at] >> 16);
            if (n == 0 || at + n > words.Length) return false;
            code.Add(words.AsSpan(at, n).ToArray()); at += n;
        }
        var definitions = new Dictionary<uint, uint[]>();
        var sets = new Dictionary<uint, uint>();
        var bindings = new Dictionary<uint, uint>();
        uint entry = 0;
        int entryCount = 0;
        foreach (var i in code)
        {
            uint op = i[0] & 65535;
            if (op is >= 19 and <= 39) definitions[i[1]] = i;
            if (op == 59) definitions[i[2]] = i;
            if (op == 15)
            {
                entryCount++; entry = i[2];
                if (i[1] != (vertex ? 0u : 4u)) return false;
            }
            if (op == 71 && i.Length >= 4)
            {
                if (i[2] == 34) sets[i[1]] = i[3];
                if (i[2] == 33) bindings[i[1]] = i[3];
                // Existing instance-sensitive shaders must keep their zero-based instance semantics.
                if (i[2] == 11 && i[3] == 43) return false;
            }
            // Decoration groups and pointer-addressing extensions need a different proof.
            if (op is 73 or 74 or 75) return false;
        }
        if (entryCount != 1) return false;
        var roots = new Dictionary<uint, (uint Struct, int Field)>();
        foreach (var i in code)
        {
            if ((i[0] & 65535) != 59) continue;
            bool record = sets.GetValueOrDefault(i[2], uint.MaxValue) == SetConvention.StorageSet &&
                bindings.GetValueOrDefault(i[2], uint.MaxValue) == SetConvention.ProgramRecordBinding;
            bool push = i[3] == 9;
            if (!record && !push) continue;
            if ((record && i[3] != 2) || roots.Values.Any(r => r.Field == (push ? 1 : 0)) ||
                !definitions.TryGetValue(i[1], out var pointer) || (pointer[0] & 65535) != 32 ||
                !definitions.TryGetValue(pointer[3], out var block) || (block[0] & 65535) != 30)
                return false;
            roots.Add(i[2], (pointer[3], push ? 1 : 0));
        }
        foreach (var b in reflection.Bindings)
        {
            if (b.Set == SetConvention.StorageSet && b.Binding != SetConvention.ProgramRecordBinding) return false;
            if (b.Set == SetConvention.StorageSet && (b.Block == null ||
                b.Block.Size > source.UniformShadow.Length || !SafeBlock(b.Block))) return false;
        }
        if (reflection.PushConstants is { } pb &&
            (pb.Size > source.Interface.PushConstantSize || !SafeBlock(pb))) return false;

        uint next = words[3];
        uint Id() => next++;
        var declarations = new List<uint[]>();
        var decorations = new List<uint[]>();
        var names = new List<uint[]>();
        uint scalar = 0;
        foreach (var d in definitions.Values)
            if ((d[0] & 65535) == 21 && d[2] == 32 && d[3] == 0) { scalar = d[1]; break; }
        if (scalar == 0) { scalar = Id(); declarations.Add(Instruction(21, scalar, 32, 0)); }
        uint zero = Id(), one = Id();
        declarations.Add(Instruction(43, scalar, zero, 0));
        declarations.Add(Instruction(43, scalar, one, 1));
        uint inputPointer = Id(); declarations.Add(Instruction(32, inputPointer, 1, scalar));
        uint selector = Id(); declarations.Add(Instruction(59, inputPointer, selector, 1));
        if (vertex) decorations.Add(Instruction(71, selector, 11, 43)); // BuiltIn InstanceIndex
        else
        {
            decorations.Add(Instruction(71, selector, 30, location));
            decorations.Add(Instruction(71, selector, 14)); // Flat
        }
        uint varying = 0;
        if (vertex)
        {
            uint outputPointer = Id(); declarations.Add(Instruction(32, outputPointer, 3, scalar));
            varying = Id(); declarations.Add(Instruction(59, outputPointer, varying, 3));
            decorations.Add(Instruction(71, varying, 30, location));
            decorations.Add(Instruction(71, varying, 14));
        }
        // A stage may omit one of the original blocks. Its reserved bytes remain
        // in the common row layout; a dummy scalar type occupies the unused field.
        uint[] fields = [scalar, scalar];
        foreach (var root in roots.Values)
        {
            uint clone = Id(); fields[root.Field] = clone;
            var structure = (uint[])definitions[root.Struct].Clone(); structure[1] = clone;
            declarations.Add(structure);
            foreach (var i in code)
            {
                if (((i[0] & 65535) == 5 || (i[0] & 65535) == 6) && i[1] == root.Struct)
                {
                    var name = (uint[])i.Clone(); name[1] = clone; names.Add(name);
                }
                if ((i[0] & 65535) == 72 && i[1] == root.Struct)
                {
                    var d = (uint[])i.Clone(); d[1] = clone; decorations.Add(d);
                }
            }
        }
        // Always leave at least four bytes before the second field, including
        // stages with no program block (the CPU uses the same common offset).
        uint pushOffset = (uint)PushOffset(source);
        if (pushOffset == 0) return false;
        uint row = Id(), rows = Id(), outer = Id(), outerPointer = Id(), records = Id();
        names.Add(Name(outer, BlockName)); names.Add(Name(records, BlockName));
        bool hasPush = source.Interface.PushConstantSize > 0;
        declarations.Add(hasPush ? Instruction(30, row, fields[0], fields[1]) : Instruction(30, row, fields[0]));
        decorations.Add(Instruction(72, row, 0, 35, 0));
        if (hasPush) decorations.Add(Instruction(72, row, 1, 35, pushOffset));
        declarations.Add(Instruction(29, rows, row));
        decorations.Add(Instruction(71, rows, 6, (uint)Stride(source)));
        declarations.Add(Instruction(30, outer, rows));
        decorations.Add(Instruction(71, outer, 2)); // Block only on the outer SSBO.
        decorations.Add(Instruction(72, outer, 0, 35, 0));
        decorations.Add(Instruction(72, outer, 0, 24)); // NonWritable
        decorations.Add(Instruction(71, records, 34, SetConvention.StorageSet));
        decorations.Add(Instruction(71, records, 33, SetConvention.FaceDataBinding));
        declarations.Add(Instruction(32, outerPointer, 12, outer));
        declarations.Add(Instruction(59, outerPointer, records, 12));
        var pointers = new HashSet<uint>(roots.Keys);
        var storagePointers = new Dictionary<uint, uint>();
        uint StoragePointer(uint original)
        {
            if (storagePointers.TryGetValue(original, out uint p)) return p;
            if (!definitions.TryGetValue(original, out var d) || (d[0] & 65535) != 32) return 0;
            p = Id(); declarations.Add(Instruction(32, p, 12, d[3]));
            foreach (var annotation in code)
                if ((annotation[0] & 65535) == 71 && annotation[1] == original)
                {
                    var copy = (uint[])annotation.Clone(); copy[1] = p; decorations.Add(copy);
                }
            storagePointers.Add(original, p); return p;
        }
        var body = new List<uint[]>();
        uint function = 0;
        bool firstLabel = false, writeVarying = false;
        foreach (var original in code)
        {
            var i = original;
            uint op = i[0] & 65535;
            if (op == 15)
            {
                var added = new List<uint>(i) { selector };
                if (vertex) added.Add(varying);
                if (words[1] >= 0x00010400) added.Add(records);
                added[0] = ((uint)added.Count << 16) | 15; i = added.ToArray();
            }
            if (op == 54) { function = i[2]; firstLabel = true; }
            if (op == 248 && firstLabel)
            {
                firstLabel = false; writeVarying = vertex && function == entry;
                body.Add(i); continue;
            }
            // Function OpVariable declarations must remain first in the entry block.
            if (writeVarying && op is not (59 or 8 or 317))
            {
                uint index = Id(); body.Add(Instruction(61, scalar, index, selector));
                body.Add(Instruction(62, varying, index)); writeVarying = false;
            }
            if (function != 0)
            {
                if ((op == 65 || op == 66) && pointers.Contains(i[3]))
                {
                    uint pointer = StoragePointer(i[1]);
                    if (pointer == 0) return false;
                    var access = new List<uint>(i); access[1] = pointer;
                    if (roots.TryGetValue(i[3], out var root))
                    {
                        if (i.Length == 4) return false; // A pointer to the whole cloned struct has a different type.
                        uint index = Id(); body.Add(Instruction(61, scalar, index, selector));
                        access[3] = records;
                        access.InsertRange(4, [zero, index, root.Field == 0 ? zero : one]);
                    }
                    access[0] = ((uint)access.Count << 16) | op;
                    pointers.Add(i[2]); i = access.ToArray();
                }
                else if (op == 61 && pointers.Contains(i[3]))
                {
                    // A block-wide load has a different struct result type. Ordinary
                    // scalar/vector/matrix/array loads through member pointers are exact.
                    if (roots.ContainsKey(i[3])) return false;
                }
                else if (op is not (54 or 56 or 8 or 317))
                {
                    // No pointer escapes, aliases, writes, pointer arithmetic, Phi or
                    // function arguments. Unknown relevant instructions decline the variant.
                    // ExtInst's instruction number and composite indices are
                    // literals, not IDs. Do not mistake a common GLSL.std.450
                    // operation number for a pointer with the same numeric ID.
                    int first = op == 12 ? 5 : 1;
                    int end = op == 79 ? Math.Min(5, i.Length) :
                        op == 81 ? Math.Min(4, i.Length) : op == 82 ? Math.Min(5, i.Length) : i.Length;
                    for (int operand = first; operand < end; operand++)
                        if (pointers.Contains(i[operand])) return false;
                }
            }
            body.Add(i);
            if (op == 56) function = 0;
        }
        if (writeVarying) return false;
        var output = new List<uint>(words.AsSpan(0, 5).ToArray());
        bool named = false, annotated = false, declared = false;
        foreach (var i in body)
        {
            uint op = i[0] & 65535;
            if (!named && (op is 71 or 72 or 73 or 74 or 75 or 332 || op is >= 19 and <= 39))
            { foreach (var n in names) output.AddRange(n); named = true; }
            if (!annotated && op is >= 19 and <= 39)
            { foreach (var d in decorations) output.AddRange(d); annotated = true; }
            if (!declared && op == 54)
            { foreach (var d in declarations) output.AddRange(d); declared = true; }
            output.AddRange(i);
        }
        if (!annotated || !declared) return false;
        output[3] = next;
        result = MemoryMarshal.AsBytes(output.ToArray().AsSpan()).ToArray();
        return true;
    }

    private static bool SafeBlock(SpirvBlock block)
    {
        foreach (var m in block.Members)
            if (!m.HasExplicitOffset || m.HasConflictingLayoutDecorations || m.RuntimeArray ||
                m.ArrayDimensions > 1 || m.Offset < 0 || m.Size <= 0 || m.ScalarWidth != 32 ||
                (m.GlslType.StartsWith("mat", StringComparison.Ordinal) && m.MatrixStride <= 0) ||
                (m.ArrayDimensions != 0 && m.ArrayStride <= 0)) return false;
        return true;
    }
}
