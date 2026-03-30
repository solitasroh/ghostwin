// shader_vs.hlsl -- Vertex shader for GPU-instanced quad rendering.
// Phase 4-E: StructuredBuffer 32B packed format (was 68B Input Layout).

struct PackedQuad {
    uint2  pos_size;       // x: lo16=pos_x hi16=pos_y, y: lo16=size_x hi16=size_y
    uint2  tex_pos_size;   // x: lo16=tex_u hi16=tex_v, y: lo16=tex_w hi16=tex_h
    uint   fg_packed;      // RGBA8
    uint   bg_packed;      // RGBA8
    uint   shading_type;
    uint   reserved;
};

StructuredBuffer<PackedQuad> g_instances : register(t1);

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

float4 unpackColor(uint packed) {
    return float4(
        (packed & 0xFF) / 255.0,
        ((packed >> 8) & 0xFF) / 255.0,
        ((packed >> 16) & 0xFF) / 255.0,
        ((packed >> 24) & 0xFF) / 255.0
    );
}

PSInput main(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID) {
    PackedQuad q = g_instances[instanceId];

    // Unpack uint16 pairs from uint32
    float2 position = float2(q.pos_size.x & 0xFFFF, q.pos_size.x >> 16);
    float2 size     = float2(q.pos_size.y & 0xFFFF, q.pos_size.y >> 16);
    float2 texcoord = float2(q.tex_pos_size.x & 0xFFFF, q.tex_pos_size.x >> 16);
    float2 texsize  = float2(q.tex_pos_size.y & 0xFFFF, q.tex_pos_size.y >> 16);

    float2 corner = float2(
        (vertexId == 1 || vertexId == 2) ? 1.0 : 0.0,
        (vertexId == 2 || vertexId == 3) ? 1.0 : 0.0);

    float2 pixelPos = position + corner * size;

    PSInput output;
    output.pos = float4(pixelPos * positionScale + float2(-1.0, 1.0), 0.0, 1.0);
    output.uv = (texcoord + corner * texsize) * atlasScale;
    output.fgColor = unpackColor(q.fg_packed);
    output.bgColor = unpackColor(q.bg_packed);
    output.shadingType = q.shading_type;

    return output;
}
