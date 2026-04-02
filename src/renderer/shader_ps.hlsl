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

        // Coverage gamma: steepen edge transitions for sharper perceived edges
        // sRGB-like pow(x, 0.5) makes partially-covered pixels more opaque
        // This mimics Alacritty's GL_FRAMEBUFFER_SRGB effect on glyph edges
        // sRGB gamma on coverage + single-pass premultiplied alpha (no bgTexture)
        // Removes 3-pass CopyResource overhead. ClearType per-channel in source color only.
        // Per-channel lerp in LINEAR space (GL_FRAMEBUFFER_SRGB equivalent)
        // 1. Per-channel: fixes max(R,G,B) over-suppression (stem 7px → 3.5px)
        // 2. Linear space: fringes blend closer to background color
        float3 bgL = pow(max(input.bgColor.rgb, 0.001), 2.2);
        float3 fgL = pow(max(input.fgColor.rgb, 0.001), 2.2);
        float3 resultL = lerp(bgL, fgL, glyph.rgb);
        float3 result = pow(max(resultL, 0.0), 1.0 / 2.2);
        return float4(result, 1.0);
    }

    // Cursor/underline
    if (input.shadingType == 2 || input.shadingType == 3)
        return float4(input.fgColor.rgb * input.fgColor.a, input.fgColor.a);

    return float4(1, 0, 1, 1);
}
