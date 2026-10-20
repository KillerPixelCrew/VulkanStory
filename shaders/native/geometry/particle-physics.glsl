// Stock ParticlePhysics arithmetic and Y/X/Z clipping order; world and box
// observations are produced by the game adapter, without game types here.
void buoyancy(dvec3 position, inout vec3 inputVelocity, float gravity, float dt, float height, World world) {
    precise vec3 velocity = inputVelocity;
    if (world.liquid.w > 0.5) {
        if (world.liquid.x > 0.5) {
            precise float submerged = clamp(stockAdd(stockSub(stockAdd(stockAdd(stockRound(double(int(position.y))), stockDivide(world.liquid.z, 8.0)),
                world.liquid.y > 0.5 ? 1.125 : 0.0), stockRound(position.y)), height), 0.0, 1.0);
            precise float lift = clamp(stockMul(9.0, submerged), -1.25, 1.25);
            velocity.y = stockAdd(velocity.y, stockMul(stockMul(gravity, dt), lift));
            precise float resistance = clamp(stockSub(stockMul(30.0, abs(velocity.y)), 0.02), 1.0, 1.25);
            velocity.y = stockDivide(velocity.y, resistance);
            velocity.x = stockMul(velocity.x, 0.99); velocity.z = stockMul(velocity.z, 0.99);
            if (world.flow.w > 0.5 && submerged >= 0.0) {
                precise float scale = stockMul(7.5, stockDivide(dt, 0.033));
                velocity = stockAdd(velocity, stockMul(world.flow.xyz, scale));
            }
        }
    } else if (world.flow.w > 0.5) velocity = stockAdd(velocity, stockMul(world.flow.xyz, stockMul(30.0, dt)));
    inputVelocity = velocity;
}

bool intersectsOther(dvec3 lo, dvec3 hi, dvec3 blockLo, dvec3 blockHi, int axis) {
    int a = (axis + 1) % 3, b = (axis + 2) % 3;
    return hi[a] > blockLo[a] && lo[a] < blockHi[a] && hi[b] > blockLo[b] && lo[b] < blockHi[b];
}
Query prepareQuery(dvec3 position, inout vec3 inputMotion, float height, World world, out uint queryBounds) {
    precise float halfSize = stockMul(height, 0.5);
    precise dvec3 lo = position - dvec3(double(halfSize), 0.0LF, double(halfSize));
    precise dvec3 hi = position + dvec3(double(halfSize));
    precise float cap = stockValue(world.motionCap);
    precise vec3 motion = clamp(stockValue(inputMotion), vec3(-cap), vec3(cap));
    ivec3 minimum = ivec3(lo + dvec3(min(vec3(0), motion)) - dvec3(0, 1, 0));
    ivec3 maximum = ivec3(hi + dvec3(max(vec3(0), motion)));
    // SetAndCorrectDimension in the stock producer, then its double remainder.
    minimum.y %= 32768; maximum.y %= 32768;
    ivec3 cell = ivec3(position); cell.y %= 32768;
    queryBounds = 0;
    for (int axis = 0; axis < 3; ++axis) {
        queryBounds |= uint(minimum[axis] - cell[axis] + 8) << (axis * 4);
        queryBounds |= uint(maximum[axis] - cell[axis] + 8) << ((axis + 3) * 4);
    }
    uint selected = 0;
    bool custom = false;
    for (uint j = 0; j < world.boxCount; ++j) {
        Box box = boxes.values[world.boxStart + j];
        ivec3 cell = box.cell.xyz;
        if (all(greaterThanEqual(cell, minimum)) && all(lessThanEqual(cell, maximum))) {
            if (box.minimum.w > 0.5) custom = true;
            else ++selected;
        }
    }
    inputMotion = motion;
    return Query(ivec4(minimum, int(selected | (custom ? 0x80000000u : 0u))), ivec4(maximum, 0));
}
uint collide(dvec3 position, inout vec3 inputMotion, float height, World world, Query query) {
    precise float halfSize = stockMul(height, 0.5);
    precise dvec3 lo = position - dvec3(double(halfSize), 0.0LF, double(halfSize));
    precise dvec3 hi = position + dvec3(double(halfSize));
    precise vec3 motion = inputMotion;
    lo.y -= trunc(lo.y / 32768.0LF) * 32768.0LF;
    hi.y -= trunc(hi.y / 32768.0LF) * 32768.0LF;
    uint flags = 0;
    for (int pass = 0; pass < 3; ++pass) {
        uint ordinal = 0;
        int axis = pass == 0 ? 1 : (pass == 1 ? 0 : 2);
        for (uint j = 0; j < world.boxCount; ++j) {
            Box box = boxes.values[world.boxStart + j];
            if (box.minimum.w > 0.5) continue;
            if (any(lessThan(box.cell.xyz, query.minimum.xyz)) || any(greaterThan(box.cell.xyz, query.maximum.xyz))) continue;
            precise dvec3 offset = dvec3(box.cell.x, box.cell.y % 32768, box.cell.z);
            precise dvec3 blockLo = dvec3(stockValue(box.minimum.xyz)) + offset;
            precise dvec3 blockHi = dvec3(stockValue(box.maximum.xyz)) + offset;
            if (ordinal++ < uint(query.maximum.w)) {
                // Reused Cuboidd slots evaluate float + int before widening.
                vec3 roundedOffset = vec3(stockRound(offset.x), stockRound(offset.y), stockRound(offset.z));
                precise vec3 reusedLo = stockAdd(box.minimum.xyz, roundedOffset);
                precise vec3 reusedHi = stockAdd(box.maximum.xyz, roundedOffset);
                blockLo = dvec3(reusedLo); blockHi = dvec3(reusedHi);
            }
            if (!intersectsOther(lo, hi, blockLo, blockHi, axis)) continue;
            precise double delta = double(motion[axis]);
            if (delta > 0.0LF && hi[axis] <= blockLo[axis] && blockLo[axis] - hi[axis] < delta) {
                motion[axis] = stockRound(blockLo[axis] - hi[axis]); flags |= 1u << axis;
            } else if (delta < 0.0LF && lo[axis] >= blockHi[axis] && blockHi[axis] - lo[axis] > delta) {
                motion[axis] = stockRound(blockHi[axis] - lo[axis]); flags |= 1u << axis;
            }
        }
        if (pass < 2) { lo[axis] += double(motion[axis]); hi[axis] += double(motion[axis]); }
    }
    inputMotion = motion;
    return flags;
}
// Stock updatePositionWithCollision response after a collision, in X/Y/Z order.
vec3 respond(vec3 inputVelocity, uint flags, float drag, float bounciness) {
    precise vec3 velocity = inputVelocity;
    precise float bounce = stockMul(stockSub(0.0, bounciness), 0.65);
    precise float lift = min(1.0, stockMul(drag, 5.0));
    if ((flags & 1u) != 0u) { velocity.x = stockMul(velocity.x, bounce); velocity.y = stockMul(velocity.y, lift); velocity.z = stockMul(velocity.z, drag); }
    if ((flags & 2u) != 0u) { velocity.x = stockMul(velocity.x, drag); velocity.y = stockMul(velocity.y, bounce); velocity.z = stockMul(velocity.z, drag); }
    if ((flags & 4u) != 0u) { velocity.x = stockMul(velocity.x, drag); velocity.y = stockMul(velocity.y, lift); velocity.z = stockMul(velocity.z, bounce); }
    return velocity;
}
