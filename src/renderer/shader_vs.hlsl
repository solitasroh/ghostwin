// shader_vs.hlsl -- Vertex shader for GPU-instanced quad rendering.

cbuffer ConstBuffer : register(b0) {
    float2 positionScale;
    float2 atlasScale;
};

struct PSInput {
    float4 pos         : SV_POSITION;
    float2 uv          : TEXCOORD0;
    float4 fgColor     : COLOR0;
    float4 bgColor     : COLOR1;
    nointerpolation uint shadingType : BLENDINDICES0;
};

struct VSInput {
    uint   shadingType : BLENDINDICES0;
    float2 position    : POSITION;        // R16G16_SINT -> float via hardware
    float2 size        : TEXCOORD1;       // R16G16_UINT -> float via hardware
    float2 texcoord    : TEXCOORD2;       // R16G16_UINT -> float
    float2 texsize     : TEXCOORD3;       // R16G16_UINT -> float
    float4 fgColor     : COLOR0;          // R8G8B8A8_UNORM -> float4
    float4 bgColor     : COLOR1;          // R8G8B8A8_UNORM -> float4
    uint   vertexId    : SV_VertexID;
};

PSInput main(VSInput input) {
    PSInput output;

    float2 corner = float2(
        (input.vertexId == 1 || input.vertexId == 2) ? 1.0 : 0.0,
        (input.vertexId == 2 || input.vertexId == 3) ? 1.0 : 0.0);

    float2 pixelPos = input.position + corner * input.size;
    output.pos = float4(pixelPos * positionScale + float2(-1.0, 1.0), 0.0, 1.0);

    float2 texOffset = corner * input.texsize;
    output.uv = (input.texcoord + texOffset) * atlasScale;

    output.fgColor = input.fgColor;
    output.bgColor = input.bgColor;
    output.shadingType = input.shadingType;

    return output;
}
