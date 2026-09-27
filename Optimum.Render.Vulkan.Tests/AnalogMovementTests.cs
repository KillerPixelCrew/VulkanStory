using System;
using Optimum;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

public sealed class AnalogMovementTests
{
    [Fact]
    public void MagnitudeRequiresServerCapabilityAndPhysicalKeysStayDigital()
    {
        Assert.Equal(0.45f, OptimumAnalogMovement.EffectiveFactor(0.45f, false, true));
        Assert.Equal(1f, OptimumAnalogMovement.EffectiveFactor(0.45f, true, true));
        Assert.Equal(1f, OptimumAnalogMovement.EffectiveFactor(0.45f, false, false));
        Assert.Equal(1f, OptimumAnalogMovement.EffectiveFactor(float.NaN, false, true));
        Assert.Equal(1f, OptimumAnalogMovement.EffectiveFactor(2f, false, true));
    }

    [Fact]
    public void EntityPacketMagnitudeRoundTripsWithinOneByteResolution()
    {
        byte encoded = OptimumAnalogMovement.Encode(0.37f);
        Assert.InRange(OptimumAnalogMovement.Decode(encoded), 0.37f - 1f / 255f, 0.37f + 1f / 255f);
        Assert.Equal(0f, OptimumAnalogMovement.Decode(OptimumAnalogMovement.Encode(-1f)));
        Assert.Equal(1f, OptimumAnalogMovement.Decode(OptimumAnalogMovement.Encode(3f)));
        Assert.InRange(OptimumAnalogMovement.DecodeAxis(OptimumAnalogMovement.EncodeAxis(-0.8f)),
            -0.8f - 1f / 127f, -0.8f + 1f / 127f);
    }

    [Fact]
    public void ActiveStickReplacesEightWayVectorWithoutChangingKeyboardFallback()
    {
        var controls = new EntityControls { MovespeedMultiplier = 0.5f, Forward = true, Right = true };
        var position = new EntityPos();
        controls.CalcMovementVectors(position, 1f);
        double digitalRatio = Math.Abs(controls.WalkVector.X / controls.WalkVector.Z);
        Assert.InRange(digitalRatio, 0.99, 1.01);

        OptimumAnalogMovement.SetAxes(controls, 0.8f, -0.6f);
        OptimumAnalogMovement.ApplyDirection(controls, position, 1f);
        double analogRatio = Math.Abs(controls.WalkVector.X / controls.WalkVector.Z);
        Assert.InRange(analogRatio, 1.32, 1.34);

        OptimumAnalogMovement.ClearAxes(controls);
        controls.CalcMovementVectors(position, 1f);
        OptimumAnalogMovement.ApplyDirection(controls, position, 1f);
        Assert.InRange(Math.Abs(controls.WalkVector.X / controls.WalkVector.Z), 0.99, 1.01);
    }
}
