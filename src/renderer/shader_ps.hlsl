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

// WT pattern: takes color (float3), internally computes intensity
float3 DWrite_ApplyAlphaCorrection3(float3 a, float3 color, float4 g) {
    float f = DWrite_CalcColorIntensity(color);
    return a + a * (1.0 - a) * ((g.x * f + g.y) * a + (g.z * f + g.w));
}

float3 DWrite_ApplyLightOnDarkContrastAdjustment3(float k, float3 color) {
    float adj = k * saturate(dot(color, float3(0.30, 0.59, 0.11) * -4.0) + 3.0);
    return float3(adj, adj, adj);
}

// ─── Dual Source Output (WT pattern) ───

struct DualOutput {
    float4 color   : SV_Target0;  // premultiplied color
    float4 weights : SV_Target1;  // per-channel blend weights
};

DualOutput main(PSInput input) {
    DualOutput o;

    // Background: opaque, weights=1 (fully overwrite dest)
    if (input.shadingType == 0) {
        o.color = float4(input.bgColor.rgb, 1.0);
        o.weights = float4(1, 1, 1, 1);
        return o;
    }

    // ClearType text: Dual Source per-channel blend + DWrite gamma correction
    // D2D linearParams (gamma=1.0) produces LINEAR coverage.
    // WT pattern: shader applies EnhanceContrast + AlphaCorrection to compensate.
    // Previous "gamma=soft" was from DOUBLE gamma (CreateAlphaTexture gamma=1.8 + shader).
    // Now: single gamma (linear + shader) = correct. Same as WT shader_ps.hlsl:59-69.
    if (input.shadingType == 1) {
        float4 glyph = glyphAtlas.Sample(pointSamp, input.uv);

        // DWrite gamma correction (WT pattern)
        float blendK = DWrite_ApplyLightOnDarkContrastAdjustment(
            enhancedContrast, input.fgColor.rgb);
        float3 contrasted = DWrite_EnhanceContrast3(glyph.rgb, blendK);
        float3 alphaCorrected = DWrite_ApplyAlphaCorrection3(
            contrasted, input.fgColor.rgb, gammaRatios);

        o.weights = float4(alphaCorrected * input.fgColor.a, 1);
        o.color = o.weights * input.fgColor;
        return o;
    }

    // Cursor/underline: standard premultiplied
    if (input.shadingType == 2 || input.shadingType == 3) {
        float a = input.fgColor.a;
        o.color = float4(input.fgColor.rgb * a, a);
        o.weights = o.color.aaaa;
        return o;
    }

    o.color = float4(1, 0, 1, 1);
    o.weights = float4(1, 1, 1, 1);
    return o;
}
