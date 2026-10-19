// Stock EvolvingNatFloat operations shared by particle visual and fixed-step compute.
float evaluate(Evolution evolution) {
    precise float first = evolution.values.x, factor = evolution.values.y, phase = evolution.values.z;
    precise float x = factor * phase;
    precise float value = 0.0;
    int mode = evolution.control.x;
    switch (mode) {
        case -1: return first;
        case -2: return 0.0;
        case 0: value = first; break;
        case 1: value = first + x; break;
        case 2: value = factor > 0.0 ? min(0.0, first + x) : max(0.0, first + x); break;
        case 3: value = first - ((first / abs(first)) * factor) * phase; break;
        case 4: value = first + ((first / abs(first)) * factor) * phase; break;
        case 5: value = first + (sign(factor) * x) * x; break;
        case 6: value = first + 1.0 / (1.0 + x); break;
        case 7: value = first + sqrt(x); break;
        case 8: value = first + table.values[uint(int(x * table.values[8192])) & 4095u]; break;
        case 9: value = first * min(5.0 * abs(table.values[uint(int(x * table.values[8192])) & 4095u]), 1.0); break;
        case 10: value = first + table.values[4096u + (uint(int(x * table.values[8192])) & 4095u)]; break;
        case 11: value = first + (x * x) * (3.0 - 2.0 * x); break;
    }
    if (evolution.control.y != 0) value = min(evolution.values.w, value);
    return value;
}
