// Use the existing integer binary32 rounding for add/subtract/multiply. Check
// BEFORE native float conversion: flushing could hide an underflow as zero.
// Keep a wide margin above FLT_MIN and reject nonfinite operands/results.
// Rejected steps never update residents or reach the particle/render facade.
#include "../worldgen/terrain-binary32.glsl"
bool arithmeticUnsafe = false;
float stockValue(float value) {
    uint magnitude = floatBitsToUint(value) & 0x7fffffffu;
    if ((magnitude != 0u && magnitude < 0x0d800000u) || magnitude >= 0x7f800000u) arithmeticUnsafe = true; // 2^-100
    return value;
}
vec3 stockValue(vec3 value) { return vec3(stockValue(value.x), stockValue(value.y), stockValue(value.z)); }
// World positions remain binary64. Reject tiny/nonfinite input coordinates by
// their bits too, so optional double denormal handling cannot affect clipping.
dvec3 stockPosition(dvec3 value) {
    for (int axis = 0; axis < 3; ++axis) {
        uvec2 bits = unpackDouble2x32(value[axis]);
        uint high = bits.y & 0x7fffffffu;
        if (((high != 0u || bits.x != 0u) && high < 0x39b00000u) || high >= 0x7ff00000u) arithmeticUnsafe = true;
    }
    return value;
}
double stockDouble(float value) { return double(stockValue(value)); }
void stockMidpoints(float value, out double lower, out double upper, out bool even);
float stockRound(double value) {
    if (isnan(value) || isinf(value) || (value != 0.0LF && abs(value) < 7.888609052210118e-31LF) ||
        abs(value) > 3.4028234663852886e38LF) arithmeticUnsafe = true;
    precise float rounded = float(value);
    stockValue(rounded);
    if (arithmeticUnsafe || value == 0.0LF) return rounded;
    double lower, upper; bool even;
    stockMidpoints(rounded, lower, upper, even);
    if (abs(value) < lower || abs(value) > upper || (!even && (abs(value) == lower || abs(value) == upper))) arithmeticUnsafe = true;
    return stockValue(rounded);
}
float stockAdd(float a, float b) {
    stockValue(a); stockValue(b);
    if (arithmeticUnsafe) return 0.0;
    return stockValue(uintBitsToFloat(B32Add(floatBitsToUint(a), floatBitsToUint(b))));
}
float stockSub(float a, float b) { return stockAdd(a, uintBitsToFloat(floatBitsToUint(b) ^ B32_SIGN)); }
float stockMul(float a, float b) {
    stockValue(a); stockValue(b);
    if (arithmeticUnsafe) return 0.0;
    uint bits = B32Mul(floatBitsToUint(a), floatBitsToUint(b));
    // The integer result cannot flush. Exact nonzero products rounded to zero
    // must also leave the GPU path, even though no small result bits remain.
    if ((bits & 0x7fffffffu) == 0u && a != 0.0 && b != 0.0) arithmeticUnsafe = true;
    return stockValue(uintBitsToFloat(bits));
}
vec3 stockAdd(vec3 a, vec3 b) { return vec3(stockAdd(a.x, b.x), stockAdd(a.y, b.y), stockAdd(a.z, b.z)); }
vec3 stockSub(vec3 a, vec3 b) { return vec3(stockSub(a.x, b.x), stockSub(a.y, b.y), stockSub(a.z, b.z)); }
vec3 stockMul(vec3 a, vec3 b) { return vec3(stockMul(a.x, b.x), stockMul(a.y, b.y), stockMul(a.z, b.z)); }
vec3 stockMul(vec3 a, float b) { return stockMul(a, vec3(b)); }

// Exact midpoints between adjacent NORMAL binary32 values, represented in
// binary64. Products of a midpoint (25 bits) and a float (24 bits) are exact.
// This proves the final float rounding even if binary64 division/sqrt uses an
// approximation or lands on a double-rounding boundary. Ambiguity falls back.
void stockMidpoints(float value, out double lower, out double upper, out bool even) {
    uint bits = floatBitsToUint(abs(value));
    lower = (double(uintBitsToFloat(bits - 1u)) + double(abs(value))) * 0.5LF;
    upper = (double(uintBitsToFloat(bits + 1u)) + double(abs(value))) * 0.5LF;
    even = (bits & 1u) == 0u;
}
float stockDivide(float a, float b) {
    precise double numerator = stockDouble(a), denominator = stockDouble(b);
    if (denominator == 0.0LF) { arithmeticUnsafe = true; return 0.0; }
    precise double quotient = numerator / denominator;
    float value = stockRound(quotient);
    if (arithmeticUnsafe || a == 0.0) return value;
    double lower, upper; bool even;
    stockMidpoints(value, lower, upper, even);
    precise double lo = lower * abs(denominator), hi = upper * abs(denominator);
    if (abs(numerator) < lo || abs(numerator) > hi || (!even && (abs(numerator) == lo || abs(numerator) == hi))) arithmeticUnsafe = true;
    return value;
}
float stockSqrt(float a) {
    precise double inputValue = stockDouble(a);
    if (inputValue < 0.0LF) { arithmeticUnsafe = true; return 0.0; }
    float value = stockRound(sqrt(inputValue));
    if (arithmeticUnsafe || a == 0.0) return value;
    double lower, upper; bool even;
    stockMidpoints(value, lower, upper, even);
    precise double lo = lower * lower, hi = upper * upper;
    if (inputValue < lo || inputValue > hi || (!even && (inputValue == lo || inputValue == hi))) arithmeticUnsafe = true;
    return value;
}
