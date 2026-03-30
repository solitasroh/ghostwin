// shader_ps.hlsl -- Pixel shader for terminal rendering.
// ClearType subpixel antialiasing: per-channel alpha blending.

struct PSInput {
    float4 pos         : SV_POSITION;
    float2 uv          : TEXCOORD0;
    float4 fgColor     : COLOR0;
    float4 bgColor     : COLOR1;
    nointerpolation uint shadingType : BLENDINDICES0;
};

Texture2D<float4> glyphAtlas : register(t0);
SamplerState      pointSamp  : register(s0);

float4 main(PSInput input) : SV_Target {
    if (input.shadingType == 0)
        return input.bgColor;
    if (input.shadingType == 1) {
        float4 tex = glyphAtlas.Sample(pointSamp, input.uv);

        // Gamma-correct subpixel blending (linear space to reduce color fringing)
        float3 fg_linear = pow(input.fgColor.rgb, 2.2);
        float3 bg_linear = pow(input.bgColor.rgb, 2.2);
        float3 blended_linear = lerp(bg_linear, fg_linear, tex.rgb);
        float3 blended = pow(blended_linear, 1.0 / 2.2);

        return float4(blended * tex.a, tex.a);
    }
    if (input.shadingType == 2)
        return input.fgColor;
    if (input.shadingType == 3)
        return input.fgColor;
    return float4(1.0, 0.0, 1.0, 1.0);
}
