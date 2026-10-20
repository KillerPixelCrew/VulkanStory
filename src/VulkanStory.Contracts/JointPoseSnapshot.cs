using System.Runtime.InteropServices;
using System.Threading;

namespace VulkanStory.Contracts;

/// <summary>Immutable model/inverse-bind pairs for the visual joints of one animator pose.</summary>
public sealed class JointPoseSnapshot : IEquatable<JointPoseSnapshot>
{
    private readonly float[] pairs;
    private readonly bool[] products;
    private readonly byte[] payload;
    private int hash;
    private int references = 1;
    private readonly bool pooled;
    private static readonly Dictionary<int, Stack<JointPoseSnapshot>> pool = new();

    private JointPoseSnapshot(int count)
    {
        pairs = new float[count * 32]; products = new bool[count]; payload = new byte[count * 144]; pooled = true;
    }

    /// <summary>Rents immutable storage. Every retained owner must release its reference.</summary>
    public static JointPoseSnapshot Rent(ReadOnlySpan<float> values)
    {
        if (values.Length == 0 || values.Length % 32 != 0) throw new ArgumentException("Each joint needs two complete matrices.", nameof(values));
        var result = RentStorage(values.Length / 32);
        values.CopyTo(result.pairs); Array.Fill(result.products, true); result.Pack();
        return result;
    }

    private static JointPoseSnapshot RentStorage(int count)
    {
        lock (pool)
            if (pool.TryGetValue(count, out var available) && available.Count != 0)
            { var result = available.Pop(); result.references = 1; return result; }
        return new JointPoseSnapshot(count);
    }

    public JointPoseSnapshot Retain() { Interlocked.Increment(ref references); return this; }
    public void Release()
    {
        if (Interlocked.Decrement(ref references) != 0 || !pooled) return;
        lock (pool)
        {
            if (!pool.TryGetValue(JointCount, out var available)) pool.Add(JointCount, available = new());
            available.Push(this);
        }
    }
    public JointPoseSnapshot(ReadOnlySpan<float> modelAndInversePairs)
    {
        if (modelAndInversePairs.Length == 0 || modelAndInversePairs.Length % 32 != 0)
            throw new ArgumentException("Each joint needs two complete matrices.", nameof(modelAndInversePairs));
        pairs = modelAndInversePairs.ToArray();
        products = new bool[JointCount]; Array.Fill(products, true);
        payload = new byte[JointCount * 144]; Pack();
    }
    public int JointCount => pairs.Length / 32;
    public ReadOnlySpan<float> Pairs => pairs;
    public ReadOnlySpan<byte> Bytes => payload;
    public bool Matches(ReadOnlySpan<float> other)
    {
        foreach (bool product in products) if (!product) return false;
        return MemoryMarshal.AsBytes(other).SequenceEqual(MemoryMarshal.AsBytes(pairs.AsSpan()));
    }
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
        var result = RentStorage(count);
        var pairs = result.pairs; var products = result.products;
        if (previous != null) { previous.Pairs.CopyTo(pairs); previous.products.CopyTo(products, 0); }
        else
        {
            Array.Clear(pairs); Array.Clear(products);
            var matrices = MemoryMarshal.Cast<byte, float>(shadow);
            for (int i = 0; i < count; i++) matrices.Slice(i * 16, 16).CopyTo(pairs.AsSpan(i * 32, 16));
        }
        prefix.Pairs.CopyTo(pairs); prefix.products.CopyTo(products, 0);
        result.Pack(); return result;
    }

    private void Pack()
    {
        var bytes = payload;
        for (int i = 0; i < products.Length; i++)
        {
            MemoryMarshal.AsBytes(pairs.AsSpan(i * 32, 32)).CopyTo(bytes.AsSpan(i * 144, 128));
            bytes[i * 144 + 128] = products[i] ? (byte)1 : (byte)0;
        }
        var code = new HashCode(); code.AddBytes(Bytes); hash = code.ToHashCode();
    }

    public static void Multiply(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> output)
    {
        for (int column = 0; column < 4; column++)
            for (int row = 0; row < 4; row++)
                output[column * 4 + row] = ((a[row] * b[column * 4] + a[4 + row] * b[column * 4 + 1]) +
                    a[8 + row] * b[column * 4 + 2]) + a[12 + row] * b[column * 4 + 3];
    }
}
