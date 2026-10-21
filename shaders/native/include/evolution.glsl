// Stock EvolvingNatFloat operations for particle visual evolution.
#define EV_VALUE(a) (a)
#define EV_ADD(a,b) ((a)+(b))
#define EV_SUB(a,b) ((a)-(b))
#define EV_MUL(a,b) ((a)*(b))
#define EV_DIV(a,b) ((a)/(b))
#define EV_SQRT(a) sqrt(a)
float evaluate(Evolution evolution) {
    precise float first = EV_VALUE(evolution.values.x), factor = EV_VALUE(evolution.values.y), phase = EV_VALUE(evolution.values.z);
    precise float x = EV_MUL(factor, phase);
    precise float value = 0.0;
    int mode = evolution.control.x;
    switch (mode) {
        case -1: return first;
        case -2: return 0.0;
        case 0: value = first; break;
        case 1: value = EV_ADD(first, x); break;
        case 2: value = factor > 0.0 ? min(0.0, EV_ADD(first, x)) : max(0.0, EV_ADD(first, x)); break;
        case 3: value = EV_SUB(first, EV_MUL(EV_MUL(EV_DIV(first, abs(first)), factor), phase)); break;
        case 4: value = EV_ADD(first, EV_MUL(EV_MUL(EV_DIV(first, abs(first)), factor), phase)); break;
        case 5: value = EV_ADD(first, EV_MUL(EV_MUL(sign(factor), x), x)); break;
        case 6: value = EV_ADD(first, EV_DIV(1.0, EV_ADD(1.0, x))); break;
        case 7: value = EV_ADD(first, EV_SQRT(x)); break;
        case 8: value = EV_ADD(first, EV_VALUE(table.values[uint(int(EV_MUL(x, table.values[8192]))) & 4095u])); break;
        case 9: value = EV_MUL(first, min(EV_MUL(5.0, abs(EV_VALUE(table.values[uint(int(EV_MUL(x, table.values[8192]))) & 4095u]))), 1.0)); break;
        case 10: value = EV_ADD(first, EV_VALUE(table.values[4096u + (uint(int(EV_MUL(x, table.values[8192]))) & 4095u)])); break;
        case 11: value = EV_ADD(first, EV_MUL(EV_MUL(x, x), EV_SUB(3.0, EV_MUL(2.0, x)))); break;
    }
    if (evolution.control.y != 0) value = min(EV_VALUE(evolution.values.w), value);
    return value;
}
#undef EV_VALUE
#undef EV_ADD
#undef EV_SUB
#undef EV_MUL
#undef EV_DIV
#undef EV_SQRT
