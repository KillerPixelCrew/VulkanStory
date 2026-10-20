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
///
/// Animated rows also carry element bases. Their packed index buffer adds the slice's
/// vertex base, so every VertexIndex load subtracts it again and the original reads its
/// local index. Every element address of the deformation and animation blocks adds the
/// draw's element base in the slot's shared heaps, where its own buffers were copied.
/// The program's arithmetic is unchanged; anything this cannot prove declines the variant.
/// </summary>
internal static class EntityRecordShader
{
    internal const string BlockName = "VulkanEntityDrawRecords";
    // Element bases of an animated row: the packed vertex base, then one per input block.
    internal const int BaseVertex = 0, BaseDeformation = 1, BasePreviousDeformation = 2, BaseAnimation = 3,
        BaseAnimationPrev = 4;
    private const int BaseBytes = 32;
    internal const uint DeformationStride = 16, AnimationStride = 64;
    internal static int PushOffset(ShaderProgramResources source) => (source.UniformShadow.Length + 15) & ~15;
    internal static int BaseOffset(ShaderProgramResources source) =>
        (PushOffset(source) + source.Interface.PushConstantSize + 15) & ~15;
    // Only programs with input blocks carry bases; rigid rows keep their original stride.
    internal static bool HasBases(ShaderProgramResources source) =>
        source.Interface.StorageBlocks.Count + source.Interface.UniformBlocks.Count > 0;
    internal static int Stride(ShaderProgramResources source) => BaseOffset(source) + (HasBases(source) ? BaseBytes : 0);

    /// <summary>The set 2 binding of each block an animated row offsets, with its base index and element stride.</summary>
    private static bool TryInputBlocks(ProgramInterfaceLayout layout, out Dictionary<uint, (uint Base, uint Stride)> blocks)
    {
        blocks = new();
        foreach (var block in layout.StorageBlocks)
        {
            int index = block.BlockName == VulkanDevice.DeformationBlock ? BaseDeformation :
                block.BlockName == VulkanDevice.PreviousDeformationBlock ? BasePreviousDeformation : -1;
            if (index < 0 || block.Set != SetConvention.StorageSet ||
                !blocks.TryAdd((uint)block.Binding, ((uint)index, DeformationStride))) return false;
        }
        foreach (var block in layout.UniformBlocks)
        {
            int index = block.BlockName == "Animation" ? BaseAnimation : block.BlockName == "AnimationPrev" ? BaseAnimationPrev : -1;
            if (index < 0 || block.Set != SetConvention.StorageSet ||
                !blocks.TryAdd((uint)block.Binding, ((uint)index, AnimationStride))) return false;
        }
        return true;
    }

    // FaceData when free, as before; animated programs read their deformation there.
    private static int RecordBinding(ProgramInterfaceLayout layout)
    {
        static bool Used(ProgramInterfaceLayout layout, int binding)
        {
            foreach (var block in layout.UniformBlocks) if (block.Binding == binding) return true;
            foreach (var block in layout.StorageBlocks) if (block.Binding == binding) return true;
            return false;
        }
        if (!Used(layout, SetConvention.FaceDataBinding)) return SetConvention.FaceDataBinding;
        for (int binding = SetConvention.NamedBlockFirstBinding; binding <= SetConvention.NamedBlockLastBinding; binding++)
            if (!Used(layout, binding)) return binding;
        return -1;
    }

    internal static bool TryCreate(ShaderProgramResources source, out TranslatedProgram? variant,
        out int varyingComponents)
    {
        variant = null; varyingComponents = 0;
        if (source.OriginalSpirv.Count != 2 || !TryInputBlocks(source.Interface, out var inputs) ||
            source.UniformShadow.Length == 0 ||
            source.Interface.PushConstantSize < 0 || source.Interface.PushConstantSize > SetConvention.PushConstantBytes ||
            Stride(source) <= 0 || Stride(source) > 65536 ||
            !source.OriginalSpirv.TryGetValue(ShaderStageKind.VertexShader, out var vertex) ||
            !source.OriginalSpirv.TryGetValue(ShaderStageKind.FragmentShader, out var fragment)) return false;
        int recordBinding = RecordBinding(source.Interface);
        if (recordBinding < 0) return false;
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
        if (!TryStage(vertex, vr, source, true, (uint)location, inputs, (uint)recordBinding, out var vs) ||
            !TryStage(fragment, fr, source, false, (uint)location, inputs, (uint)recordBinding, out var fs)) return false;
        var layout = source.Interface.WithEntityRecords();
        // A fresh entry of the copied list: the record block's actual binding.
        layout.StorageBlocks[^1] = new BlockBinding { BlockName = BlockName, Binding = recordBinding };
        variant = new TranslatedProgram
        {
            Layout = layout, Specialization = source.Specialization,
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
        ShaderProgramResources source, bool vertex, uint location,
        Dictionary<uint, (uint Base, uint Stride)> inputs, uint recordBinding, out byte[] result)
    {
        result = [];
        bool bases = HasBases(source);
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
        var constants = new Dictionary<uint, uint[]>();
        var sets = new Dictionary<uint, uint>();
        var bindings = new Dictionary<uint, uint>();
        uint entry = 0, vertexIndex = 0;
        int entryCount = 0;
        foreach (var i in code)
        {
            uint op = i[0] & 65535;
            if (op is >= 19 and <= 39) definitions[i[1]] = i;
            if (op == 59) definitions[i[2]] = i;
            if (op == 43) constants[i[2]] = i;
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
                if (i[2] == 11 && i[3] == 42) vertexIndex = i[1];
            }
            // Decoration groups and pointer-addressing extensions need a different proof.
            if (op is 73 or 74 or 75) return false;
            if (bases)
            {
                // A vertex index inside a block would escape the row's vertex base.
                if (op == 72 && i.Length >= 5 && i[3] == 11 && i[4] == 42) return false;
                // Bases are added in 32-bit integer arithmetic. Narrow or wide integers,
                // variable or physical pointers need a different proof.
                if (op == 17 && i.Length >= 2 && i[1] is 11 or 22 or 39 or 4441 or 4442 or 5347) return false;
            }
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
            if (b.Set != SetConvention.StorageSet) continue;
            if (b.Binding != SetConvention.ProgramRecordBinding)
            {
                // Only the input blocks below; their element accesses are proved separately.
                if (!inputs.ContainsKey((uint)b.Binding)) return false;
                continue;
            }
            if (b.Block == null || b.Block.Size > source.UniformShadow.Length || !SafeBlock(b.Block)) return false;
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
        // Input block variables, by base index. Each is a StorageBuffer block whose only
        // member, at offset zero, is an array of the expected element stride. A fixed-length
        // array is redeclared runtime-sized with the same element type and decorations, so
        // an element past its length in the shared heap is not out of bounds by type.
        var blockBases = new Dictionary<uint, uint>();
        var movedVariables = new HashSet<uint>();
        foreach (var i in code)
        {
            if ((i[0] & 65535) != 59 || sets.GetValueOrDefault(i[2], uint.MaxValue) != SetConvention.StorageSet ||
                !inputs.TryGetValue(bindings.GetValueOrDefault(i[2], uint.MaxValue), out var input)) continue;
            if (!bases || i.Length != 4 || i[3] != 12 || !definitions.TryGetValue(i[1], out var pointer) ||
                (pointer[0] & 65535) != 32 || pointer[2] != 12 ||
                !definitions.TryGetValue(pointer[3], out var block) || (block[0] & 65535) != 30 || block.Length != 3 ||
                !definitions.TryGetValue(block[2], out var array) || (array[0] & 65535) is not (28 or 29) ||
                Decoration(code, block[2], 6) != input.Stride || MemberOffset(code, pointer[3], 0) != 0) return false;
            blockBases.Add(i[2], input.Base);
            if ((array[0] & 65535) == 29) continue;
            uint runtime = Id(), structure = Id(), blockPointer = Id();
            declarations.Add(Instruction(29, runtime, array[2]));
            decorations.Add(Instruction(71, runtime, 6, input.Stride));
            declarations.Add(Instruction(30, structure, runtime));
            foreach (var annotation in code)
                if (((annotation[0] & 65535) is 71 or 72) && annotation[1] == pointer[3])
                {
                    var copy = (uint[])annotation.Clone(); copy[1] = structure; decorations.Add(copy);
                }
            declarations.Add(Instruction(32, blockPointer, 12, structure));
            // Moved after its new pointer type; decorations and the entry point name it by ID.
            var variable = (uint[])i.Clone(); variable[1] = blockPointer;
            declarations.Add(variable); movedVariables.Add(i[2]);
        }
        // The vertex index is only offset where this row carries bases.
        if (!bases || !vertex) vertexIndex = 0;
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
        // Animated rows end in their element bases, five 32-bit words at BaseOffset.
        uint basesField = hasPush ? 2u : 1u, basesStruct = 0, basePointer = 0;
        if (bases)
        {
            basesStruct = Id(); declarations.Add(Instruction(30, basesStruct, scalar, scalar, scalar, scalar, scalar));
            for (uint member = 0; member < 5; member++) decorations.Add(Instruction(72, basesStruct, member, 35, member * 4));
            basePointer = Id(); declarations.Add(Instruction(32, basePointer, 12, scalar));
        }
        var rowMembers = new List<uint> { row, fields[0] };
        if (hasPush) rowMembers.Add(fields[1]);
        if (bases) rowMembers.Add(basesStruct);
        declarations.Add(Instruction(30, rowMembers.ToArray()));
        decorations.Add(Instruction(72, row, 0, 35, 0));
        if (hasPush) decorations.Add(Instruction(72, row, 1, 35, pushOffset));
        if (bases) decorations.Add(Instruction(72, row, basesField, 35, (uint)BaseOffset(source)));
        declarations.Add(Instruction(29, rows, row));
        decorations.Add(Instruction(71, rows, 6, (uint)Stride(source)));
        declarations.Add(Instruction(30, outer, rows));
        decorations.Add(Instruction(71, outer, 2)); // Block only on the outer SSBO.
        decorations.Add(Instruction(72, outer, 0, 35, 0));
        decorations.Add(Instruction(72, outer, 0, 24)); // NonWritable
        decorations.Add(Instruction(71, records, 34, SetConvention.StorageSet));
        decorations.Add(Instruction(71, records, 33, recordBinding));
        declarations.Add(Instruction(32, outerPointer, 12, outer));
        declarations.Add(Instruction(59, outerPointer, records, 12));
        var integerConstants = new Dictionary<uint, uint> { [0] = zero, [1] = one };
        uint Constant(uint value)
        {
            if (integerConstants.TryGetValue(value, out uint id)) return id;
            id = Id(); declarations.Add(Instruction(43, scalar, id, value));
            integerConstants.Add(value, id); return id;
        }
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
        // This row's element base, read where it is added (the selector is an input).
        uint LoadBase(uint field)
        {
            uint index = Id(); body.Add(Instruction(61, scalar, index, selector));
            uint pointer = Id();
            body.Add(Instruction(65, basePointer, pointer, records, zero, index, Constant(basesField), Constant(field)));
            uint value = Id(); body.Add(Instruction(61, scalar, value, pointer));
            return value;
        }
        uint function = 0;
        bool firstLabel = false, writeVarying = false;
        foreach (var original in code)
        {
            var i = original;
            uint op = i[0] & 65535;
            if (op == 59 && function == 0 && movedVariables.Contains(i[2])) continue;
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
                if ((op == 65 || op == 66) && blockBases.TryGetValue(i[3], out uint inputBase))
                {
                    // Member zero, then the element index: the same element of this draw's
                    // inputs at its base in the shared heap. Later indices stay inside it.
                    if (i.Length < 6 || !constants.TryGetValue(i[4], out var member) || member.Length != 4 ||
                        member[3] != 0) return false;
                    uint offset = LoadBase(inputBase);
                    uint element = Id(); body.Add(Instruction(128, scalar, element, i[5], offset));
                    var access = (uint[])i.Clone(); access[5] = element; i = access;
                }
                else if (op == 61 && vertexIndex != 0 && i.Length >= 4 && i[3] == vertexIndex)
                {
                    // Packed indices add the slice's vertex base; the original reads its local index.
                    uint raw = Id();
                    var load = (uint[])i.Clone(); load[2] = raw; body.Add(load);
                    body.Add(Instruction(130, i[1], i[2], raw, LoadBase(BaseVertex)));
                    continue;
                }
                else if ((op == 65 || op == 66) && pointers.Contains(i[3]))
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
                    // Input blocks and the vertex index are used only as rewritten above.
                    for (int operand = first; operand < end; operand++)
                        if (blockBases.ContainsKey(i[operand]) || (vertexIndex != 0 && i[operand] == vertexIndex)) return false;
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

    // The literal of a one-operand decoration, or MaxValue when absent or stated differently twice.
    private static uint Decoration(List<uint[]> code, uint id, uint decoration)
    {
        uint value = uint.MaxValue;
        foreach (var i in code)
            if ((i[0] & 65535) == 71 && i.Length == 4 && i[1] == id && i[2] == decoration)
            {
                if (value != uint.MaxValue && value != i[3]) return uint.MaxValue;
                value = i[3];
            }
        return value;
    }

    private static uint MemberOffset(List<uint[]> code, uint structure, uint member)
    {
        uint value = uint.MaxValue;
        foreach (var i in code)
            if ((i[0] & 65535) == 72 && i.Length == 5 && i[1] == structure && i[2] == member && i[3] == 35)
            {
                if (value != uint.MaxValue && value != i[4]) return uint.MaxValue;
                value = i[4];
            }
        return value;
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
