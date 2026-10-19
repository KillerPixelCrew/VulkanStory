using System.Runtime.InteropServices;

namespace VulkanStory.Contracts;

/// <summary>Immutable model/inverse-bind pairs for the visual joints of one animator pose.</summary>
public sealed class JointPoseSnapshot : IEquatable<JointPoseSnapshot>
{
    private readonly float[] pairs;
    private readonly bool[] products;
    private readonly byte[] payload;
    private readonly int hash;
    public JointPoseSnapshot(ReadOnlySpan<float> modelAndInversePairs)
    {
        if (modelAndInversePairs.Length == 0 || modelAndInversePairs.Length % 32 != 0)
            throw new ArgumentException("Each joint needs two complete matrices.", nameof(modelAndInversePairs));
        pairs = modelAndInversePairs.ToArray();
        products = new bool[JointCount]; Array.Fill(products, true);
        payload = Pack(pairs, products);
        var code = new HashCode(); code.AddBytes(Bytes); hash = code.ToHashCode();
    }
    private JointPoseSnapshot(float[] pairs, bool[] products)
    {
        this.pairs = pairs; this.products = products; payload = Pack(pairs, products);
        var code = new HashCode(); code.AddBytes(Bytes); hash = code.ToHashCode();
    }
    public int JointCount => pairs.Length / 32;
    public ReadOnlySpan<float> Pairs => pairs;
    public ReadOnlySpan<byte> Bytes => payload;
    public bool Matches(ReadOnlySpan<float> other) => products.All(value => value) &&
        MemoryMarshal.AsBytes(other).SequenceEqual(MemoryMarshal.AsBytes(pairs.AsSpan()));
    public bool Equals(JointPoseSnapshot? other) => other != null && Bytes.SequenceEqual(other.Bytes);
    public override bool Equals(object? other) => other is JointPoseSnapshot pose && Equals(pose);
    public override int GetHashCode() => hash;

    /// <summary>Materializes the same column-major product for an explicit CPU consumer.</summary>
    public void CopyMatrices(Span<float> output)
    {
        if (output.Length < JointCount * 16) throw new ArgumentException("Joint destination is too small.", nameof(output));
        for (int joint = 0; joint < JointCount; joint++)
            if (products[joint]) Multiply(pairs.AsSpan(joint * 32, 16), pairs.AsSpan(joint * 32 + 16, 16), output.Slice(joint * 16, 16));
            else pairs.AsSpan(joint * 32, 16).CopyTo(output.Slice(joint * 16, 16));
    }

    /// <summary>Replaces an uploaded prefix while preserving untouched named-block matrix values.</summary>
    public static JointPoseSnapshot WithPrefix(JointPoseSnapshot prefix, JointPoseSnapshot? previous, ReadOnlySpan<byte> shadow)
    {
        int count = shadow.Length / 64;
        if (count == 0 || shadow.Length % 64 != 0 || prefix.JointCount > count || (previous != null && previous.JointCount != count))
            throw new ArgumentException("Pose upload range does not match its named block.", nameof(shadow));
        var pairs = new float[count * 32]; var products = new bool[count];
        if (previous != null) { previous.Pairs.CopyTo(pairs); previous.products.CopyTo(products, 0); }
        else
        {
            var matrices = MemoryMarshal.Cast<byte, float>(shadow);
            for (int i = 0; i < count; i++) matrices.Slice(i * 16, 16).CopyTo(pairs.AsSpan(i * 32, 16));
        }
        prefix.Pairs.CopyTo(pairs); prefix.products.CopyTo(products, 0);
        return new JointPoseSnapshot(pairs, products);
    }

    private static byte[] Pack(float[] pairs, bool[] products)
    {
        var bytes = new byte[products.Length * 144];
        for (int i = 0; i < products.Length; i++)
        {
            MemoryMarshal.AsBytes(pairs.AsSpan(i * 32, 32)).CopyTo(bytes.AsSpan(i * 144, 128));
            bytes[i * 144 + 128] = products[i] ? (byte)1 : (byte)0;
        }
        return bytes;
    }

    public static void Multiply(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> output)
    {
        for (int column = 0; column < 4; column++)
            for (int row = 0; row < 4; row++)
                output[column * 4 + row] = ((a[row] * b[column * 4] + a[4 + row] * b[column * 4 + 1]) +
                    a[8 + row] * b[column * 4 + 2]) + a[12 + row] * b[column * 4 + 3];
    }
}
