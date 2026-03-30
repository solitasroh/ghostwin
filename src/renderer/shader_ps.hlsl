// shader_ps.hlsl -- Grayscale AA text rendering for composition swapchain.
// No RGB fringing. Clean edges. DWrite gamma correction.

struct PSInput {
    float4 pos         : SV_POSITION;
    float2 uv          : TEXCOORD0;
    float4 fgColor     : COLOR0;
    float4 bgColor     : COLOR1;
    nointerpolation uint shadingType : BLENDINDICES0;
};

Texture2D<float4> glyphAtlas : register(t0);
SamplerState      pointSamp  : register(s0);

cbuffer ConstBuffer : register(b0) {
    float2 positionScale;
    float2 atlasScale;
    float  enhancedContrast;
    float  _pad0;
    float4 gammaRatios;
};

// DWrite gamma correction (lhecker/dwrite-hlsl)
float DWrite_EnhanceContrast(float alpha, float k) {
    return alpha * (k + 1.0) / (alpha * k + 1.0);
}

float DWrite_ApplyAlphaCorrection(float a, float f, float4 g) {
    return a + a * (1.0 - a) * ((g.x * f + g.y) * a + (g.z * f + g.w));
}

float DWrite_ApplyLightOnDarkContrastAdjustment(float k, float3 color) {
    return k * saturate(dot(color, float3(0.30, 0.59, 0.11) * -4.0) + 3.0);
}

float DWrite_CalcColorIntensity(float3 color) {
    return dot(color, float3(0.25, 0.5, 0.25));
}

float4 main(PSInput input) : SV_Target {
    // Background: opaque
    if (input.shadingType == 0)
        return float4(input.bgColor.rgb, 1.0);

    // Grayscale AA text: clean edges, no RGB fringing
    if (input.shadingType == 1) {
        float4 glyph = glyphAtlas.Sample(pointSamp, input.uv);
        float alpha = glyph.a;  // Grayscale: single channel coverage

        // DWrite contrast enhancement + gamma correction
        float k = DWrite_ApplyLightOnDarkContrastAdjustment(
            enhancedContrast, input.fgColor.rgb);
        float contrasted = DWrite_EnhanceContrast(alpha, k);
        float intensity = DWrite_CalcColorIntensity(input.fgColor.rgb);
        float corrected = saturate(DWrite_ApplyAlphaCorrection(
            contrasted, intensity, gammaRatios));

        // Premultiplied alpha output
        float3 color = input.fgColor.rgb * corrected;
        return float4(color, corrected);
    }

    // Cursor/underline
    if (input.shadingType == 2 || input.shadingType == 3)
        return float4(input.fgColor.rgb * input.fgColor.a, input.fgColor.a);

    return float4(1, 0, 1, 1);
}
