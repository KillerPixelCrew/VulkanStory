// Finite binary32 operations for stock terrain noise. Values are IEEE bit patterns,
// never native floats: neither denormal handling nor contraction depends on the GPU.
// Each operation rounds once to nearest, ties to even, with gradual underflow.
// Scope is the bounded, finite stock-noise expressions (no NaN/infinity operands).
const uint B32_SIGN = 0x80000000u;
const uint B32_HALF = 0x3f000000u;
const uint B32_ONE = 0x3f800000u;
const uint B32_TWO = 0x40000000u;
const uint B32_THREE_QUARTERS = 0x3f400000u;

// Keep every discarded nonzero bit in bit zero; avoid undefined shifts by 32.
uint B32ShiftJam(uint value, int distance) {
    if (distance <= 0) return value;
    if (distance >= 32) return uint(value != 0u);
    return (value >> distance) | uint((value << (32 - distance)) != 0u);
}

// A nonzero significand is normalized to bit 23, including subnormal inputs.
// exponent is the biased normal exponent and may be negative for subnormals.
void B32Unpack(uint bits, out int exponent, out uint significand) {
    exponent = int((bits >> 23) & 255u);
    significand = bits & 0x007fffffu;
    if (exponent != 0) { significand |= 0x00800000u; return; }
    int shift = 23 - findMSB(significand);
    significand <<= shift;
    exponent = 1 - shift;
}

// significand has a leading bit at 26 and three guard/round/sticky bits.
// Underflow shifts BEFORE rounding, so halfway subnormal ties and a carry into
// the smallest normal both use the same single rounding step.
uint B32RoundPack(uint sign, int exponent, uint significand) {
    if (exponent < 1) {
        significand = B32ShiftJam(significand, 1 - exponent);
        exponent = 1;
    }
    uint tail = significand & 7u;
    uint rounded = (significand >> 3) + uint(tail > 4u || (tail == 4u && (significand & 8u) != 0u));
    if (rounded == 0u) return sign;
    if (exponent > 254) return sign | 0x7f800000u;
    // Addition intentionally carries the leading significand bit into the
    // exponent, including rounding overflow and subnormal -> normal promotion.
    return sign | ((uint(exponent - 1) << 23) + rounded);
}

uint B32Add(uint a, uint b) {
    uint magnitudeA = a & 0x7fffffffu, magnitudeB = b & 0x7fffffffu;
    // Under RNE only -0 + -0 is -0; exact opposite-sign cancellation is +0.
    if (magnitudeA == 0u && magnitudeB == 0u) return (a & b) & B32_SIGN;
    if (magnitudeA == 0u) return b;
    if (magnitudeB == 0u) return a;
    // Sort by magnitude so subtraction is nonnegative and keeps the larger sign.
    if (magnitudeA < magnitudeB) { uint swap = a; a = b; b = swap; }
    uint sign = a & B32_SIGN;
    int exponentA, exponentB;
    uint significandA, significandB;
    B32Unpack(a, exponentA, significandA);
    B32Unpack(b, exponentB, significandB);
    significandA <<= 3;
    significandB = B32ShiftJam(significandB << 3, exponentA - exponentB);
    uint significand;
    if (((a ^ b) & B32_SIGN) == 0u) {
        significand = significandA + significandB;
        if ((significand & 0x08000000u) != 0u) {
            significand = B32ShiftJam(significand, 1);
            exponentA++;
        }
    } else {
        significand = significandA - significandB;
        if (significand == 0u) return 0u;
        // Close exponents retain their exact low bits before cancellation;
        // a jammed far operand cannot cause deep cancellation.
        int shift = 26 - findMSB(significand);
        significand <<= shift;
        exponentA -= shift;
    }
    return B32RoundPack(sign, exponentA, significand);
}

uint B32Sub(uint a, uint b) { return B32Add(a, b ^ B32_SIGN); }

uint B32Mul(uint a, uint b) {
    uint sign = (a ^ b) & B32_SIGN;
    if ((a & 0x7fffffffu) == 0u || (b & 0x7fffffffu) == 0u) return sign;
    int exponentA, exponentB;
    uint significandA, significandB;
    B32Unpack(a, exponentA, significandA);
    B32Unpack(b, exponentB, significandB);
    uint high, low;
    umulExtended(significandA, significandB, high, low); // exact 24 x 24 -> 48 bits
    // Product leading bit is 46 or 47. Reduce to bit 26 plus G/R/S without
    // an intermediate rounding; high contains product bits 32..47.
    int shift = (high & 0x00008000u) != 0u ? 21 : 20;
    uint significand = (high << (32 - shift)) | (low >> shift);
    significand |= uint((low << (32 - shift)) != 0u);
    return B32RoundPack(sign, exponentA + exponentB - 127 + (shift - 20), significand);
}

bool B32Positive(uint bits) { return (bits & B32_SIGN) == 0u && bits != 0u; }

// Stock fractions are finite [0,1], hence the rounded subtraction is [-1.5,-.5].
// Truncation of THAT binary32 result gives 0 or -1. Comparing xi with .5 would
// miss values just below .5 whose subtraction rounds to exactly -1.
int B32NoiseMask(uint fraction) {
    uint rounded = B32Sub(B32_HALF | B32_SIGN, fraction);
    return (rounded & 0x7fffffffu) >= B32_ONE ? -1 : 0;
}
uint B32MaskValue(int mask) { return mask == 0 ? 0u : B32_ONE | B32_SIGN; }
uint B32MaskOrOne(int mask) { return mask == 0 ? B32_ONE : B32_ONE | B32_SIGN; }
uint B32Flip0(int mask) { return mask == 0 ? B32_TWO : B32_TWO | B32_SIGN; }
uint B32Flip1(int mask) { return mask == 0 ? B32_TWO | B32_SIGN : B32_TWO; }
