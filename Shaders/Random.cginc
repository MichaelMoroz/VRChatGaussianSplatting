uint pcg(uint v) {
    uint state = v * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

float rand(inout uint seed) {
    seed = pcg(seed);
    return seed / float(0xFFFFFFFFu);
}

float2 rand2(inout uint seed) {
    return float2(rand(seed), rand(seed));
}

float3 rand3(inout uint seed) {
    return float3(rand(seed), rand(seed), rand(seed));
}