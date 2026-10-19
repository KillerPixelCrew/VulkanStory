/*
 * Integer-only binary64, RNE with gradual underflow, for terrain density.
 * Representation: uvec2(low32, high32). Include terrain-binary32.glsl first:
 * B32Unpack and B32RoundPack are the sole external helper dependencies.
 * No native floating-point or native 64-bit integer operations are used.
 * No exception/status flags or alternate rounding modes are exposed.
 *
 * Normalization, alignment/jamming and round/pack are adapted from Berkeley
 * SoftFloat Release 3e by John R. Hauser, with word-pair arithmetic, three GRS
 * bits, a full 106-bit product and exact restoring division in place of its
 * native integer/reciprocal implementation.
 * Primary source: https://github.com/ucb-bar/berkeley-softfloat-3
 * Revision: a0c6494cdc11865811dec815d5c0049fba9d82a8
 * References: source/s_roundPackToF64.c, source/s_addMagsF64.c,
 * source/s_subMagsF64.c, source/f64_mul.c, source/f64_div.c.
 * License: COPYING.txt at that revision; exact copy shipped in
 * packaging/notices/source-provenance/LICENSE-BERKELEY-SOFTFLOAT.txt.
 * COPYING.txt SHA256:
 * 145ea96b4a4a04a1a7738d2a2bf9e830f861971e69606187b018d9e8fc0b95c7
 *
 * Copyright 2011, 2012, 2013, 2014, 2015, 2016, 2017, 2018 The Regents of the
 * University of California.  All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *
 *  1. Redistributions of source code must retain the above copyright notice,
 *     this list of conditions, and the following disclaimer.
 *
 *  2. Redistributions in binary form must reproduce the above copyright
 *     notice, this list of conditions, and the following disclaimer in the
 *     documentation and/or other materials provided with the distribution.
 *
 *  3. Neither the name of the University nor the names of its contributors
 *     may be used to endorse or promote products derived from this software
 *     without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE REGENTS AND CONTRIBUTORS "AS IS", AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE, ARE
 * DISCLAIMED.  IN NO EVENT SHALL THE REGENTS OR CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF
 * THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

#ifndef B64_TERRAIN_BINARY64_INCLUDED
#define B64_TERRAIN_BINARY64_INCLUDED

const uvec2 B64_ZERO = uvec2(0u, 0u);
const uvec2 B64_ONE = uvec2(0u, 0x3ff00000u);
const uvec2 B64_NEG_ONE = uvec2(0u, 0xbff00000u);
const uvec2 B64_QUARTER = uvec2(0u, 0x3fd00000u);
const uvec2 B64_TWO = uvec2(0u, 0x40000000u);
const uvec2 B64_FIVE = uvec2(0u, 0x40140000u);
const uvec2 B64_QNAN = uvec2(0u, 0x7ff80000u);
const uint B64_SIGN = 0x80000000u;

bool B64UZero(uvec2 v) { return (v.x | v.y) == 0u; }
bool B64UEq(uvec2 a, uvec2 b) { return all(equal(a, b)); }
bool B64ULt(uvec2 a, uvec2 b) {
    return a.y < b.y || (a.y == b.y && a.x < b.x);
}
uvec2 B64UAdd(uvec2 a, uvec2 b) {
    uint low = a.x + b.x;
    return uvec2(low, a.y + b.y + uint(low < a.x));
}
uvec2 B64USub(uvec2 a, uvec2 b) {
    return uvec2(a.x - b.x, a.y - b.y - uint(a.x < b.x));
}

// All distances, including 0, 32 and >=64, have defined 32-bit shifts.
uvec2 B64Shl(uvec2 v, int distance) {
    if (distance <= 0) return v;
    if (distance >= 64) return B64_ZERO;
    if (distance >= 32) return uvec2(0u, v.x << (distance - 32));
    return uvec2(v.x << distance, (v.y << distance) | (v.x >> (32 - distance)));
}
uvec2 B64Shr(uvec2 v, int distance) {
    if (distance <= 0) return v;
    if (distance >= 64) return B64_ZERO;
    if (distance >= 32) return uvec2(v.y >> (distance - 32), 0u);
    return uvec2((v.x >> distance) | (v.y << (32 - distance)), v.y >> distance);
}
uvec2 B64ShiftJam(uvec2 v, int distance) {
    if (distance <= 0) return v;
    if (distance >= 64) return uvec2(uint(!B64UZero(v)), 0u);
    if (distance == 32) return uvec2(v.y | uint(v.x != 0u), 0u);
    if (distance > 32) {
        int highDistance = distance - 32;
        uint sticky = uint(v.x != 0u || (v.y << (32 - highDistance)) != 0u);
        return uvec2((v.y >> highDistance) | sticky, 0u);
    }
    return uvec2((v.x >> distance) | (v.y << (32 - distance))
        | uint((v.x << (32 - distance)) != 0u), v.y >> distance);
}

// Returns -1 for zero, without invoking findMSB on zero.
int B64MSB(uvec2 v) {
    if (v.y != 0u) return 32 + findMSB(v.y);
    if (v.x != 0u) return findMSB(v.x);
    return -1;
}

bool B64IsNaN(uvec2 v) {
    return (v.y & 0x7ff00000u) == 0x7ff00000u
        && ((v.y & 0x000fffffu) | v.x) != 0u;
}
bool B64IsInf(uvec2 v) {
    return (v.y & 0x7fffffffu) == 0x7ff00000u && v.x == 0u;
}
bool B64IsZero(uvec2 v) { return ((v.y & 0x7fffffffu) | v.x) == 0u; }
uvec2 B64QuietNaN(uvec2 v) { return uvec2(v.x, v.y | 0x00080000u); }
uvec2 B64NaN(uvec2 a, uvec2 b) {
    if (B64IsNaN(a)) return B64QuietNaN(a);
    return B64QuietNaN(b);
}
uvec2 B64Infinity(uint sign) { return uvec2(0u, sign | 0x7ff00000u); }
uvec2 B64Neg(uvec2 v) {
    v.y ^= B64_SIGN;
    return B64IsNaN(v) ? B64QuietNaN(v) : v;
}
uvec2 B64Abs(uvec2 v) {
    v.y &= 0x7fffffffu;
    return B64IsNaN(v) ? B64QuietNaN(v) : v;
}

bool B64Eq(uvec2 a, uvec2 b) {
    if (B64IsNaN(a) || B64IsNaN(b)) return false;
    return B64UEq(a, b) || (B64IsZero(a) && B64IsZero(b));
}
bool B64Lt(uvec2 a, uvec2 b) {
    if (B64IsNaN(a) || B64IsNaN(b)) return false;
    if (B64IsZero(a) && B64IsZero(b)) return false;
    bool negativeA = (a.y & B64_SIGN) != 0u;
    bool negativeB = (b.y & B64_SIGN) != 0u;
    if (negativeA != negativeB) return negativeA;
    return negativeA ? B64ULt(b, a) : B64ULt(a, b);
}
bool B64Le(uvec2 a, uvec2 b) { return B64Eq(a, b) || B64Lt(a, b); }
bool B64Gt(uvec2 a, uvec2 b) { return B64Lt(b, a); }
bool B64Ge(uvec2 a, uvec2 b) { return B64Le(b, a); }
uvec2 B64Min(uvec2 a, uvec2 b) {
    if (B64IsNaN(a) || B64IsNaN(b)) return B64NaN(a, b);
    // .NET ties choose -0 for Min and +0 for Max, regardless of order.
    if (B64IsZero(a) && B64IsZero(b)) return uvec2(0u, (a.y | b.y) & B64_SIGN);
    return B64Lt(b, a) ? b : a;
}
uvec2 B64Max(uvec2 a, uvec2 b) {
    if (B64IsNaN(a) || B64IsNaN(b)) return B64NaN(a, b);
    if (B64IsZero(a) && B64IsZero(b)) return uvec2(0u, (a.y & b.y) & B64_SIGN);
    return B64Lt(a, b) ? b : a;
}

// Finite nonzero input only. Normalized significand leads at bit 52;
// exponent is biased by 1023 and can be <=0 for a subnormal input.
void B64Unpack(uvec2 v, out int exponent, out uvec2 significand) {
    exponent = int((v.y >> 20) & 2047u);
    significand = uvec2(v.x, v.y & 0x000fffffu);
    if (exponent != 0) { significand.y |= 0x00100000u; return; }
    int distance = 52 - B64MSB(significand);
    significand = B64Shl(significand, distance);
    exponent = 1 - distance;
}

// Nonzero significand normally leads at bit 55, followed by G/R/S in 2:0.
// Shift into the subnormal range BEFORE the only rounding step. The same
// step handles halfway ties, promotion to normal, and carry into infinity.
uvec2 B64RoundPack(uint sign, int exponent, uvec2 significand) {
    if (exponent < 1) {
        significand = B64ShiftJam(significand, 1 - exponent);
        exponent = 1;
    }
    uint tail = significand.x & 7u;
    bool increment = tail > 4u || (tail == 4u && (significand.x & 8u) != 0u);
    uvec2 rounded = B64UAdd(B64Shr(significand, 3), uvec2(uint(increment), 0u));
    if (B64UZero(rounded)) return uvec2(0u, sign);
    if ((rounded.y & 0x00200000u) != 0u) {
        rounded = B64Shr(rounded, 1);
        exponent++;
    }
    if (exponent >= 2047) return B64Infinity(sign);
    if ((rounded.y & 0x00100000u) == 0u) exponent = 0;
    return uvec2(rounded.x, sign | (uint(exponent) << 20) | (rounded.y & 0x000fffffu));
}

uvec2 B64Add(uvec2 a, uvec2 b) {
    if (B64IsNaN(a) || B64IsNaN(b)) return B64NaN(a, b);
    if (B64IsInf(a)) {
        if (B64IsInf(b) && ((a.y ^ b.y) & B64_SIGN) != 0u) return B64_QNAN;
        return a;
    }
    if (B64IsInf(b)) return b;
    if (B64IsZero(a) && B64IsZero(b)) return uvec2(0u, (a.y & b.y) & B64_SIGN);
    if (B64IsZero(a)) return b;
    if (B64IsZero(b)) return a;
    // Larger magnitude first, including all subnormal encodings.
    if (B64ULt(uvec2(a.x, a.y & 0x7fffffffu), uvec2(b.x, b.y & 0x7fffffffu))) {
        uvec2 swapValue = a; a = b; b = swapValue;
    }
    uint sign = a.y & B64_SIGN;
    int exponentA, exponentB;
    uvec2 significandA, significandB;
    B64Unpack(a, exponentA, significandA);
    B64Unpack(b, exponentB, significandB);
    significandA = B64Shl(significandA, 3);
    significandB = B64ShiftJam(B64Shl(significandB, 3), exponentA - exponentB);
    uvec2 significand;
    if (((a.y ^ b.y) & B64_SIGN) == 0u) {
        significand = B64UAdd(significandA, significandB);
        if ((significand.y & 0x01000000u) != 0u) {
            significand = B64ShiftJam(significand, 1);
            exponentA++;
        }
    } else {
        significand = B64USub(significandA, significandB);
        if (B64UZero(significand)) return B64_ZERO;
        // Deep cancellation occurs only for equal/adjacent exponents, where
        // alignment is exact. Larger separations need at most one left shift;
        // three GRS bits preserve rounding after subtracting a jammed operand.
        int distance = 55 - B64MSB(significand);
        significand = B64Shl(significand, distance);
        exponentA -= distance;
    }
    return B64RoundPack(sign, exponentA, significand);
}
uvec2 B64Sub(uvec2 a, uvec2 b) {
    if (B64IsNaN(a) || B64IsNaN(b)) return B64NaN(a, b);
    b.y ^= B64_SIGN;
    return B64Add(a, b);
}

// Little-endian 128-bit word sum; carries are explicit unsigned operations.
uvec4 B64AddWords4(uvec4 a, uvec4 b) {
    uvec4 result;
    uint carry = 0u;
    for (int i = 0; i < 4; i++) {
        uint sum = a[i] + b[i];
        uint next = sum + carry;
        carry = uint(sum < a[i] || next < sum);
        result[i] = next;
    }
    return result;
}
uvec4 B64Product(uvec2 a, uvec2 b) {
    uint high00, low00, high01, low01, high10, low10, high11, low11;
    umulExtended(a.x, b.x, high00, low00);
    umulExtended(a.x, b.y, high01, low01);
    umulExtended(a.y, b.x, high10, low10);
    umulExtended(a.y, b.y, high11, low11);
    uvec4 result = uvec4(low00, high00, 0u, 0u);
    result = B64AddWords4(result, uvec4(0u, low01, high01, 0u));
    result = B64AddWords4(result, uvec4(0u, low10, high10, 0u));
    return B64AddWords4(result, uvec4(0u, 0u, low11, high11));
}
uvec2 B64Mul(uvec2 a, uvec2 b) {
    if (B64IsNaN(a) || B64IsNaN(b)) return B64NaN(a, b);
    uint sign = (a.y ^ b.y) & B64_SIGN;
    if (B64IsInf(a) || B64IsInf(b)) {
        if (B64IsZero(a) || B64IsZero(b)) return B64_QNAN;
        return B64Infinity(sign);
    }
    if (B64IsZero(a) || B64IsZero(b)) return uvec2(0u, sign);
    int exponentA, exponentB;
    uvec2 significandA, significandB;
    B64Unpack(a, exponentA, significandA);
    B64Unpack(b, exponentB, significandB);
    uvec4 product = B64Product(significandA, significandB);
    // Exact product leads at bit 104 or 105. Shift 49 or 50 to bit 55,
    // preserving every discarded bit in S; no intermediate rounding.
    int adjustment = (product.w & 0x00000200u) != 0u ? 1 : 0;
    int distance = 17 + adjustment; // remaining distance after the first word
    uvec2 significand = uvec2(
        (product.y >> distance) | (product.z << (32 - distance)),
        (product.z >> distance) | (product.w << (32 - distance)));
    significand.x |= uint(product.x != 0u || (product.y << (32 - distance)) != 0u);
    return B64RoundPack(sign, exponentA + exponentB - 1023 + adjustment, significand);
}

uvec2 B64Div(uvec2 a, uvec2 b) {
    if (B64IsNaN(a) || B64IsNaN(b)) return B64NaN(a, b);
    uint sign = (a.y ^ b.y) & B64_SIGN;
    if (B64IsInf(a)) return B64IsInf(b) ? B64_QNAN : B64Infinity(sign);
    if (B64IsInf(b)) return uvec2(0u, sign);
    if (B64IsZero(b)) return B64IsZero(a) ? B64_QNAN : B64Infinity(sign);
    if (B64IsZero(a)) return uvec2(0u, sign);
    int exponentA, exponentB;
    uvec2 numerator, denominator;
    B64Unpack(a, exponentA, numerator);
    B64Unpack(b, exponentB, denominator);
    int exponent = exponentA - exponentB + 1023;
    if (B64ULt(numerator, denominator)) {
        numerator = B64Shl(numerator, 1);
        exponent--;
    }
    // Ratio is [1,2). Produce 53 significant bits plus G/R/S by long
    // division. At every step remainder < denominator, so its next doubling
    // needs at most 54 bits. The final nonzero remainder jams into S.
    uvec2 remainder = B64USub(numerator, denominator);
    uvec2 quotient = uvec2(1u, 0u);
    for (int i = 0; i < 55; i++) {
        remainder = B64Shl(remainder, 1);
        quotient = B64Shl(quotient, 1);
        if (!B64ULt(remainder, denominator)) {
            remainder = B64USub(remainder, denominator);
            quotient.x |= 1u;
        }
    }
    quotient.x |= uint(!B64UZero(remainder));
    return B64RoundPack(sign, exponent, quotient);
}

uvec2 B64Floor(uvec2 v) {
    if (B64IsNaN(v)) return B64QuietNaN(v);
    int exponent = int((v.y >> 20) & 2047u) - 1023;
    if (exponent >= 52 || B64IsZero(v)) return v; // includes infinity and -0
    bool negative = (v.y & B64_SIGN) != 0u;
    if (exponent < 0) return negative ? B64_NEG_ONE : B64_ZERO;
    // Clear fraction bits; a negative non-integer needs one extra integer
    // unit. Adding that unit to the encoding also carries across powers of 2.
    uvec2 unit = B64Shl(uvec2(1u, 0u), 52 - exponent);
    uvec2 mask = B64USub(unit, uvec2(1u, 0u));
    bool fractional = !B64UZero(v & mask);
    uvec2 result = v & ~mask;
    if (negative && fractional) result = B64UAdd(result, unit);
    return result;
}

uvec2 B64FromI32(int value) {
    if (value == 0) return B64_ZERO;
    uint sign = value < 0 ? B64_SIGN : 0u;
    // Unsigned subtraction defines abs(INT_MIN) without signed overflow.
    uint magnitude = value < 0 ? 0u - uint(value) : uint(value);
    int top = findMSB(magnitude); // magnitude is nonzero
    return B64RoundPack(sign, 1023 + top, B64Shl(uvec2(magnitude, 0u), 55 - top));
}

// Truncates toward zero. Caller guarantees finite input whose truncated value
// is representable in int32, including INT_MIN. No out-of-range conversion API.
int B64ToI32(uvec2 v) {
    if (B64IsZero(v)) return 0;
    int exponent;
    uvec2 significand;
    B64Unpack(v, exponent, significand);
    if (exponent < 1023) return 0;
    uint magnitude = B64Shr(significand, 52 - (exponent - 1023)).x;
    if ((v.y & B64_SIGN) == 0u) return int(magnitude);
    if (magnitude == 0x80000000u) return -2147483647 - 1;
    return -int(magnitude);
}

uvec2 B64FromB32(uint bits) {
    uint sign = bits & 0x80000000u;
    uint magnitude = bits & 0x7fffffffu;
    if (magnitude == 0u) return uvec2(0u, sign);
    if (magnitude >= 0x7f800000u) {
        if (magnitude == 0x7f800000u) return B64Infinity(sign);
        uvec2 payload = B64Shl(uvec2(bits & 0x007fffffu, 0u), 29);
        return uvec2(payload.x, payload.y | sign | 0x7ff80000u);
    }
    int exponent;
    uint significand;
    B32Unpack(bits, exponent, significand);
    uvec2 widened = B64Shl(uvec2(significand, 0u), 29);
    return uvec2(widened.x, sign | (uint(exponent + 896) << 20) | (widened.y & 0x000fffffu));
}

uint B64ToB32(uvec2 v) {
    uint sign = v.y & B64_SIGN;
    if (B64IsNaN(v)) {
        uvec2 payload = B64Shr(uvec2(v.x, v.y & 0x000fffffu), 29);
        return sign | 0x7fc00000u | payload.x; // retain high payload, quiet sNaN
    }
    if (B64IsInf(v)) return sign | 0x7f800000u;
    if (B64IsZero(v)) return sign;
    int exponent;
    uvec2 significand;
    B64Unpack(v, exponent, significand);
    // Normalized 53-bit -> 24-bit plus G/R/S. B32RoundPack performs the
    // single rounding, including binary32 gradual underflow and overflow.
    return B32RoundPack(sign, exponent - 896, B64ShiftJam(significand, 26).x);
}

#endif
