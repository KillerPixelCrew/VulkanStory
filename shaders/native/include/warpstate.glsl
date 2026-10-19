// Shared raster and compute warp mathematics. Ported from vertexwarp.vsh; see vertexwarp.glsl.
struct WarpState {
	float warpTimeCounter;
	float warpWindWaveCounter;
	float warpWindWaveCounterHighFreq;
	float warpWaterWaveCounter;
	float warpWindSpeed;
	vec3 warpPlayerpos;
	float warpGlobalWarpIntensity;
	float warpGlitchWaviness;
	float warpWindWaveIntensity;
	float warpWaterWaveIntensity;
	int warpPerceptionEffectId;
	float warpPerceptionEffectIntensity;
};

vec3 applyPerceptionWarpingState(WarpState st, vec3 worldPos) {

	if (st.warpPerceptionEffectId == 2 && st.warpPerceptionEffectIntensity > 0) { // Drunk
		float pci = st.warpPerceptionEffectIntensity * clamp(length(worldPos)/2 - 2, 0.0, 2.0);
		float xf = (worldPos.x + st.warpPlayerpos.x) / 10;
		float zf = (worldPos.z + st.warpPlayerpos.z) / 10;
		worldPos.x += pci * gnoise(vec3(xf, zf, st.warpTimeCounter/6)) / 2;
		worldPos.y += pci * gnoise(vec3(xf, zf, st.warpTimeCounter/10)) / 2;
		worldPos.z += pci * gnoise(vec3(xf, zf, st.warpTimeCounter/3.5)) / 2;
	}

	return worldPos;
}


vec4 applyLiquidWarpingState(WarpState st, bool windAffected, vec4 worldPos, float div) {
	if (OPTIMUM_WAVINGSTUFF == 1) {
	vec3 noisepos = vec3((worldPos.x + st.warpPlayerpos.x) / 3, (worldPos.z + st.warpPlayerpos.z) / 3, st.warpWaterWaveCounter / 8 + (windAffected ? st.warpWindWaveCounter / 4 : 0));
	worldPos.y += st.warpWaterWaveIntensity * gnoise(noisepos) / div;

	if (windAffected) worldPos.y += st.warpWindWaveIntensity * gnoise(noisepos * 3.5) / (div * 4);

	worldPos.xyz = applyPerceptionWarpingState(st, worldPos.xyz);

	}

	return worldPos;
}

vec4 applyVertexWarpingState(WarpState st, int renderFlags, vec4 worldPos) {
	if (OPTIMUM_WAVINGSTUFF == 1) {

	if ((renderFlags & WindModeBitMask) > 0) {

		int windMode = (renderFlags >> WindModePosition) & 0xF;

		if (windMode==12) {
			return applyLiquidWarpingState(st, true, worldPos, 5);
		}

		int windData =  (renderFlags >> WindDataPosition) & 0x7;

		float x = worldPos.x + st.warpPlayerpos.x;    // See also code in PlayerCamera.cs how this is derived from ShaderUniforms.playerReferencePos
		float z = worldPos.z + st.warpPlayerpos.z;

		if (windMode != 6) {
			float y = worldPos.y + st.warpPlayerpos.y;

			// Fixes jitter due to float rounding errors
			y = ceil(y * 10000) / 10000.0;

			float heightBend = 0;

			float strength = st.warpWindWaveIntensity * (1 + st.warpWindSpeed) / 30.0;
			float bendCounter = st.warpWindWaveCounter;
			float vbendMul = 1.3/5.0;
			float wwaveHighFreq = st.warpWindWaveCounterHighFreq * 1.2;
			float strengthFactorY = 1;
			float bendNoiseFactor = 1.4;
			float bendConstant = 0.8;


			switch (windMode) {
				case 1: // Weak Wind
				case 13: // Weak Wind + reduced AlphaTest
					strength = 0.005 + 0.015 * st.warpWindSpeed;
					heightBend = (fract(y) + windData) / 7.0 * 1.3;
					break;
				case 2: // Normal wind
					strength = 0.005 + 0.015 * st.warpWindSpeed;
					heightBend = (fract(y) + windData) / 4 * 1.3;
					break;
				case 3: // Leaves
					strength *= 0.5;
					heightBend = (fract(y) + windData) / 12.0 * 1.3;
					heightBend = heightBend / 2 + pow(heightBend, 1.5) / 2; // the pow makes the bend neatly rounded
					break;
				case 4: // Bend (for small stems)
					strength = 0;
					heightBend = (fract(y) + windData) / 7.0 * 1.3;
					break;
				case 5: // Tall Bend (for thick and/or tall stems)
					strength = 0;
					heightBend = (fract(y) + windData) / 14.0 * 1.3;
					heightBend = heightBend / 2 + pow(heightBend, 1.5) / 2; // the pow makes the bend neatly rounded
					vbendMul = 0.0;
					break;
				// case 6: Water
				case 7: // Extra Weak Wind
					strength = 0.01;
					heightBend = (fract(y) + windData) / 7.0 * 0.6;
					break;
				case 8: // Fruit
					strength *= 0.15;
					if (windData == 0) windData = -1;   // Slight fudge for very tall fruit such as pears
					y += (windData + 4) / 32.0;    // All vertices on the whole fruit should have the same y - or close to it - if windData was set correctly
					strengthFactorY = 3;
					break;
				case 9: // Weak Wind No Bend (for foliage with non bending stems)
					strength *= 0.2;
					heightBend = 0;
					break;
				case 10: // Weak Wind, Inverse Bend (for vines)
					strength *= 0.5;
					//strength = 0.02; // Not sure actually why this looks better and seems to scale just fine with the windspeed
					heightBend = ((1 - fract(y)) + windData) / 14.0 * 1.5;
					break;
				case 11:  // WaterPlant for Seaweed
					strength = windData * (0.013 + 0.002 * st.warpWindSpeed);
					wwaveHighFreq /= 5;
					heightBend = windData / 7.0 * 1.3;
					bendNoiseFactor = 2.4;
					bendConstant = 0.1;
					bendCounter /= 1.8;
					break;
			}


			// 1. Determine bend
			float bend = st.warpWindSpeed * heightBend * st.warpWindWaveIntensity;
			if (bend != 0)
			{
				float bendNoise = st.warpWindSpeed * 0.2 + bendNoiseFactor * gnoise(vec3(x * 0.1, z * 0.1, mod(bendCounter, 1024.0) * 0.25));
				bend *= (bendConstant + bendNoise);
				bend = min(4, bend);
			}

			// 2. Add more noise

			x += wwaveHighFreq;
			y += wwaveHighFreq;
			z += wwaveHighFreq;

			// 3. Generate wiggle from a set of curves
			// Visualized: https://pfortuny.net/fooplot.com/#W3sidHlwZSI6MCwiZXEiOiIyKnNpbih4LzgpK3Npbih4LzIpK3NpbigwLjUrMip4KStzaW4oMSszKngpIiwiY29sb3IiOiIjMDAwMDAwIn0seyJ0eXBlIjoxMDAwLCJ3aW5kb3ciOlsiLTI0Ljc5NTUzMjIyNjU2MjQ4NiIsIjI0Ljc5NTUzMjIyNjU2MjQ4NiIsIi0xNS4yNTg3ODkwNjI0OTk5OTEiLCIxNS4yNTg3ODkwNjI0OTk5OTEiXX1d
			worldPos.x += bend + strength * (2 * sin(x * 0.5) + sin(x + y) + sin(0.5 + 4*x + 2*y) + sin(1 + 6*x + 3*y)/3);


			// This might need to be a new mode. It makes sunflower leaves nicely wiggly
			if (windMode == 1) worldPos.x += sin(x*20)*strength * 0.2 * st.warpWindSpeed;

			worldPos.y += -bend * vbendMul + strength * strengthFactorY * (sin(5*y)/15 + cos(10*x/strengthFactorY) / 10 + sin(3*z/strengthFactorY)/2 + cos(x/strengthFactorY*2)/2.2);
			worldPos.z += strength * (2 * sin(z * 0.25) + sin(z + 3 * y) + sin(0.5 + 4*z + 2*y) + sin(1 + 6*z + y)/3);

		}
		else {
			// Water wave
			vec3 noisepos = vec3(x / 3, z / 3, st.warpWaterWaveCounter / 8 + st.warpWindWaveCounter / 4);
			worldPos.y += gnoise(noisepos) / 10;
		}
	}

	}

	return worldPos;
}

vec4 applyGlobalWarpingState(WarpState st, vec4 worldPos) {
	if (OPTIMUM_WAVINGSTUFF == 1) {

	if (st.warpGlitchWaviness > 0.1) {
		float str = max(0.0, st.warpGlitchWaviness - 0.1);
		str *= clamp(1.5 * length(worldPos) * st.warpGlitchWaviness - 1, 0.0, 250.0);

		float xf = (worldPos.x + st.warpPlayerpos.x) / 10;
		float zf = (worldPos.z + st.warpPlayerpos.z) / 10;
		worldPos.x += str * gnoise(vec3(xf, zf, st.warpWindWaveCounter/6)) / 5;
		worldPos.y += str * gnoise(vec3(xf, zf, st.warpWindWaveCounter/10)) / 5;
		worldPos.z += str * gnoise(vec3(xf, zf, st.warpWindWaveCounter/3.5)) / 5;
	}

	if (st.warpGlobalWarpIntensity > 0) {
		float x = max(0.0, (mod(20*st.warpWindWaveCounter, 30)) + (worldPos.x + st.warpPlayerpos.x) * 0.2 + (worldPos.y + st.warpPlayerpos.y) * 0.125 - 40);
		worldPos.x += (sin(x / 2) + sin(0.5 + 2*x) + sin(1 + 3*x)/3) / 30.0 * st.warpGlobalWarpIntensity;
		worldPos.z += (cos(x / 3) + cos(0.2 + 2.2*x) + cos(1 + 4*x)/3) / 30.0 * st.warpGlobalWarpIntensity;
	}


	worldPos.xyz = applyPerceptionWarpingState(st, worldPos.xyz);

	}

	return worldPos;
}
