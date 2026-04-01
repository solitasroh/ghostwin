// shader_ps.hlsl -- ClearType + Grayscale AA with Dual Source Blending.
// ClearType: per-channel RGB weights for subpixel rendering.
// Grayscale: single alpha fallback for transparent/RDP environments.
// DWrite gamma correction from lhecker/dwrite-hlsl (MIT).

struct PSInput {
    float4 pos         : SV_POSITION;
    float2 uv          : TEXCOORD0;
    float4 fgColor     : COLOR0;
    float4 bgColor     : COLOR1;
    nointerpolation uint shadingType : BLENDINDICES0;
};

Texture2D<float4> glyphAtlas : register(t0);
Texture2D<float4> bgTexture  : register(t1);  // RT copy for ClearType shader lerp
SamplerState      pointSamp  : register(s0);

cbuffer ConstBuffer : register(b0) {
    float2 positionScale;
    float2 atlasScale;
    float  enhancedContrast;
    float  _pad0;
    float4 gammaRatios;
};

// ─── DWrite gamma correction: single-channel (Grayscale) ───

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

// ─── DWrite gamma correction: per-channel (ClearType) ───

float3 DWrite_EnhanceContrast3(float3 alpha, float3 k) {
    return alpha * (k + 1.0) / (alpha * k + 1.0);
}

float3 DWrite_ApplyAlphaCorrection3(float3 a, float f, float4 g) {
    return a + a * (1.0 - a) * ((g.x * f + g.y) * a + (g.z * f + g.w));
}

float3 DWrite_ApplyLightOnDarkContrastAdjustment3(float k, float3 color) {
    float adj = k * saturate(dot(color, float3(0.30, 0.59, 0.11) * -4.0) + 3.0);
    return float3(adj, adj, adj);
}

// ─── Main ───

float4 main(PSInput input) : SV_Target {
    // Background: opaque
    if (input.shadingType == 0)
        return float4(input.bgColor.rgb, 1.0);

    // ClearType text: per-channel lerp with background from bgTexture
    if (input.shadingType == 1) {
        float4 glyph = glyphAtlas.Sample(pointSamp, input.uv);

        // Per-channel contrast + gamma correction
        float3 k3 = DWrite_ApplyLightOnDarkContrastAdjustment3(
            enhancedContrast, input.fgColor.rgb);
        float3 contrasted = DWrite_EnhanceContrast3(glyph.rgb, k3);
        float intensity = DWrite_CalcColorIntensity(input.fgColor.rgb);
        float3 corrected = saturate(DWrite_ApplyAlphaCorrection3(
            contrasted, intensity, gammaRatios));

        // Read actual background pixel from RT copy (ClearType shader-lerp)
        float3 bg = bgTexture.Load(int3(input.pos.xy, 0)).rgb;

        // Per-channel ClearType blending: lerp(bg, fg, corrected_rgb)
        float3 blended = lerp(bg, input.fgColor.rgb, corrected * input.fgColor.a);
        return float4(blended, 1.0);
    }

    // Cursor/underline
    if (input.shadingType == 2 || input.shadingType == 3)
        return float4(input.fgColor.rgb * input.fgColor.a, input.fgColor.a);

    return float4(1, 0, 1, 1);
}
