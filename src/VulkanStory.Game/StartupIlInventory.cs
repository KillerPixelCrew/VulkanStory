using System.Reflection;
using System.Reflection.Emit;

namespace VulkanStory.Game;

/// <summary>A resolved member operand in one original startup method's IL.</summary>
/// <param name="Offset">Byte offset of the instruction within the original IL body.</param>
/// <param name="Opcode">Instruction mnemonic.</param>
/// <param name="Token">Metadata token decoded from the original operand.</param>
/// <param name="Member">Resolved declaring type/member signature used as the pinned anchor.</param>
public sealed record StartupIlUse(int Offset, string Opcode, int Token, string Member);

/// <summary>The exact profile target and every member operand in its original body.</summary>
/// <param name="Event">Pinned bootstrap observation event identity.</param>
/// <param name="Target">Complete original method/constructor signature.</param>
/// <param name="IlLength">Original body length in bytes.</param>
/// <param name="Uses">Ordered resolved member operands from the body.</param>
public sealed record StartupMethodInventory(
    string Event, string Target, int IlLength, IReadOnlyList<StartupIlUse> Uses);

/// <summary>
/// Reads startup IL without executing a game method. The later patch transaction
/// uses this inventory to pin call and field anchors before replacing any route.
/// </summary>
public static class StartupIlInventory
{
    private static readonly OpCode[] SingleByte = new OpCode[256];
    private static readonly OpCode[] TwoByte = new OpCode[256];

    static StartupIlInventory()
    {
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opcode) continue;
            ushort value = unchecked((ushort)opcode.Value);
            if ((value & 0xff00) == 0xfe00) TwoByte[value & 0xff] = opcode;
            else if (value < 256) SingleByte[value] = opcode;
        }
    }

    /// <summary>Reads exact original startup bodies and resolves their member operands without executing game code.</summary>
    /// <param name="game">Official VintagestoryLib assembly whose original targets are inspected.</param>
    /// <returns>One inventory per pinned startup event, in profile order.</returns>
    /// <remarks>Missing or bodyless exact targets throw before routing changes.</remarks>
    public static IReadOnlyList<StartupMethodInventory> CaptureProfile1227(Assembly game)
    {
        ArgumentNullException.ThrowIfNull(game);
        var result = new List<StartupMethodInventory>(StartupTargets.Profile1227.Length);
        foreach (StartupTarget target in StartupTargets.Profile1227)
        {
            Type type = game.GetType(target.TypeName, throwOnError: true)!;
            MethodBase method = StartupTargets.Resolve(type, target);
            byte[] il = method.GetMethodBody()?.GetILAsByteArray() ??
                throw new InvalidOperationException("No IL for " + target.TypeName + "." + target.Member);
            result.Add(new StartupMethodInventory(target.Event,
                target.TypeName + "." + target.Member + "(" + string.Join(",", target.Parameters) + ")",
                il.Length, Scan(method, il)));
        }
        return result;
    }

    internal static IReadOnlyList<StartupIlUse> Scan(MethodBase method)
    {
        byte[] il = method.GetMethodBody()?.GetILAsByteArray() ??
            throw new InvalidOperationException("No IL for " + method);
        return Scan(method, il);
    }

    private static IReadOnlyList<StartupIlUse> Scan(MethodBase method, byte[] il)
    {
        var uses = new List<StartupIlUse>();
        Type[] typeArguments = method.DeclaringType?.GetGenericArguments() ?? Type.EmptyTypes;
        Type[] methodArguments = method is MethodInfo info ? info.GetGenericArguments() : Type.EmptyTypes;
        int cursor = 0;
        while (cursor < il.Length)
        {
            int offset = cursor;
            byte first = il[cursor++];
            OpCode opcode = first == 0xfe
                ? TwoByte[ReadByte(il, ref cursor)]
                : SingleByte[first];
            string? opcodeName = opcode.Name;
            if (opcodeName is null)
                throw new InvalidDataException($"Unknown IL opcode at 0x{offset:x} in {method}.");

            int operandBytes = opcode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or
                    OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or
                    OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => SwitchBytes(il, cursor),
                _ => throw new InvalidDataException($"Unsupported IL operand {opcode.OperandType} in {method}."),
            };
            if (cursor + operandBytes > il.Length)
                throw new InvalidDataException($"Truncated IL operand at 0x{offset:x} in {method}.");

            if (opcode.OperandType is OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineTok or
                OperandType.InlineType)
            {
                int token = BitConverter.ToInt32(il, cursor);
                MemberInfo member;
                try
                {
                    member = method.Module.ResolveMember(token, typeArguments, methodArguments) ??
                        throw new InvalidOperationException("Unresolved metadata token " + token.ToString("x8"));
                }
                catch (Exception error)
                {
                    throw new InvalidOperationException(
                        $"Could not resolve IL token 0x{token:x8} at 0x{offset:x} in {method}.", error);
                }
                uses.Add(new StartupIlUse(offset, opcodeName, token, Signature(member)));
            }
            cursor += operandBytes;
        }
        return uses;
    }

    private static byte ReadByte(byte[] il, ref int cursor)
    {
        if (cursor >= il.Length) throw new InvalidDataException("Truncated two-byte IL opcode.");
        return il[cursor++];
    }

    private static int SwitchBytes(byte[] il, int cursor)
    {
        if (cursor + 4 > il.Length) throw new InvalidDataException("Truncated IL switch count.");
        int count = BitConverter.ToInt32(il, cursor);
        if (count < 0 || 4L + 4L * count > il.Length - cursor)
            throw new InvalidDataException("Invalid IL switch table.");
        return 4 + 4 * count;
    }

    private static string Signature(MemberInfo member) => member switch
    {
        MethodBase method => (method.DeclaringType?.FullName ?? "?") + "::" + method.Name + "(" +
            string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.FullName ??
                parameter.ParameterType.Name)) + ")",
        FieldInfo field => (field.DeclaringType?.FullName ?? "?") + "::" + field.Name + ":" +
            (field.FieldType.FullName ?? field.FieldType.Name),
        Type type => type.FullName ?? type.Name,
        _ => member.ToString() ?? member.Name,
    };
}
