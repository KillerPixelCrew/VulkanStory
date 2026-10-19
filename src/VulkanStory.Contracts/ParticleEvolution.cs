using System.Runtime.InteropServices;

namespace VulkanStory.Contracts;

/// <summary>One captured, deterministic particle property evaluation. Layout is shared with the compute kernel.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct ParticleEvolution(float first, float factor, float phase, float maximum, int function, bool limited)
{
    public readonly float First = first, Factor = factor, Phase = phase, Maximum = maximum;
    public readonly int Function = function, Limited = limited ? 1 : 0;
    private readonly int reserved0 = 0, reserved1 = 0;
    public static ParticleEvolution Constant(float value) => new(value, 0, 0, 0, -1, false);

    /// <summary>CPU materialization for an explicit fallback or a transition away from GPU evolution.</summary>
    public float Evaluate(ReadOnlySpan<float> table)
    {
        float x = Factor * Phase;
        float value = Function switch
        {
            -1 or 0 => First,
            -2 => 0,
            1 => First + x,
            2 => Factor > 0 ? Math.Min(0, First + x) : Math.Max(0, First + x),
            3 => First - First / Math.Abs(First) * Factor * Phase,
            4 => First + First / Math.Abs(First) * Factor * Phase,
            5 => First + Math.Sign(Factor) * x * x,
            6 => First + 1f / (1 + x),
            7 => First + (float)Math.Sqrt(x),
            8 => First + table[(int)(x * table[8192]) & 4095],
            9 => First * Math.Min(5 * Math.Abs(table[(int)(x * table[8192]) & 4095]), 1),
            10 => First + table[4096 + ((int)(x * table[8192]) & 4095)],
            11 => First + x * x * (3 - 2 * x),
            _ => throw new ArgumentOutOfRangeException(nameof(Function)),
        };
        return Function >= 0 && Limited != 0 ? Math.Min(Maximum, value) : value;
    }
}
