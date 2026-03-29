// shader_ps.hlsl -- Pixel shader for terminal rendering.

struct PSInput {
    float4 pos         : SV_POSITION;
    float2 uv          : TEXCOORD0;
    float4 fgColor     : COLOR0;
    float4 bgColor     : COLOR1;
    nointerpolation uint shadingType : BLENDINDICES0;
};

Texture2D<float> glyphAtlas : register(t0);
SamplerState     pointSamp  : register(s0);

float4 main(PSInput input) : SV_Target {
    if (input.shadingType == 0)
        return input.bgColor;
    if (input.shadingType == 1) {
        float alpha = glyphAtlas.Sample(pointSamp, input.uv);
        return float4(input.fgColor.rgb * alpha, alpha);
    }
    if (input.shadingType == 2)
        return input.fgColor;
    if (input.shadingType == 3)
        return input.fgColor;
    return float4(1.0, 0.0, 1.0, 1.0);
}
