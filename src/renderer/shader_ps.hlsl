// shader_ps.hlsl -- ClearType Dual Source Blending pixel shader.
// CreateAlphaTexture provides gamma-baked (~1.8) ClearType 3x1 coverage.
// Dual Source Blending (INV_SRC1_COLOR) handles per-channel hardware blend.

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
};

struct DualOutput {
    float4 color   : SV_Target0;  // premultiplied color
    float4 weights : SV_Target1;  // per-channel blend weights
};

DualOutput main(PSInput input) {
    DualOutput o;

    // Background: opaque, fully overwrite dest
    if (input.shadingType == 0) {
        o.color = float4(input.bgColor.rgb, 1.0);
        o.weights = float4(1, 1, 1, 1);
        return o;
    }

    // ClearType text: gamma-baked coverage + Dual Source per-channel blend
    if (input.shadingType == 1) {
        float4 glyph = glyphAtlas.Sample(pointSamp, input.uv);
        float3 coverage = glyph.rgb;

        o.weights = float4(coverage * input.fgColor.a, 1);
        o.color = o.weights * input.fgColor;
        return o;
    }

    // Cursor/underline
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
