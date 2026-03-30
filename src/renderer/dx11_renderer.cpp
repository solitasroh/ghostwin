/// @file dx11_renderer.cpp
/// D3D11 renderer implementation.
/// S5: Device + swapchain + clear.
/// S6: Shaders + input layout + instanced quad draw.

#include "dx11_renderer.h"
#include "quad_builder.h"
#include "common/log.h"

#include <d3d11_1.h>
#include <dxgi1_3.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <cstring>
#include <vector>

#pragma comment(lib, "d3dcompiler.lib")

using Microsoft::WRL::ComPtr;

namespace ghostwin {

// ─── Impl ───

struct DX11Renderer::Impl {
    ComPtr<ID3D11Device>           device;
    ComPtr<ID3D11DeviceContext>    context;
    ComPtr<IDXGISwapChain2>        swapchain;
    ComPtr<ID3D11RenderTargetView> rtv;
    HANDLE                         frame_latency_waitable = nullptr;

    // S6: Pipeline objects
    ComPtr<ID3D11VertexShader>  vs;
    ComPtr<ID3D11PixelShader>   ps;
    ComPtr<ID3D11Buffer>        index_buffer;
    ComPtr<ID3D11Buffer>        instance_buffer;
    ComPtr<ID3D11ShaderResourceView> instance_srv;  // StructuredBuffer SRV
    ComPtr<ID3D11Buffer>        constant_buffer;
    ComPtr<ID3D11BlendState>    blend_state;
    ComPtr<ID3D11SamplerState>  point_sampler;
    ComPtr<ID3D11ShaderResourceView> atlas_srv;  // set by GlyphAtlas

    uint32_t instance_capacity = 0;
    uint32_t bb_width = 0;
    uint32_t bb_height = 0;
    uint32_t atlas_w = 1024;
    uint32_t atlas_h = 1024;

    // Performance counters (Design 7.2)
    struct {
        uint64_t frame_count = 0;
        uint32_t instance_count = 0;
        uint32_t present_skip_count = 0;
    } stats;

    bool create_device(Error* out_error);
    bool create_swapchain(HWND hwnd, Error* out_error);
    bool create_rtv(Error* out_error);
    bool create_pipeline(Error* out_error);
    bool create_instance_srv();
    void update_constant_buffer();
    void draw_instances(uint32_t count);
};

// ─── Device creation ───

bool DX11Renderer::Impl::create_device(Error* out_error) {
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
#if defined(_DEBUG) || defined(DEBUG)
    flags |= D3D11_CREATE_DEVICE_DEBUG;
#endif

    D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_0 };
    D3D_FEATURE_LEVEL achieved = {};

    HRESULT hr = D3D11CreateDevice(
        nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
        flags, levels, _countof(levels),
        D3D11_SDK_VERSION,
        &device, &achieved, &context);

    if (FAILED(hr)) {
        LOG_W("renderer", "Hardware device failed (0x%08lX), trying WARP", (unsigned long)hr);
        hr = D3D11CreateDevice(
            nullptr, D3D_DRIVER_TYPE_WARP, nullptr,
            flags, levels, _countof(levels),
            D3D11_SDK_VERSION,
            &device, &achieved, &context);
    }

    if (FAILED(hr)) {
        LOG_E("renderer", "D3D11CreateDevice failed: 0x%08lX", (unsigned long)hr);
        if (out_error) *out_error = { ErrorCode::DeviceCreationFailed, "D3D11CreateDevice failed" };
        return false;
    }

    LOG_I("renderer", "D3D11 device created (FL %X)", (unsigned)achieved);
    return true;
}

// ─── Swapchain creation ───

bool DX11Renderer::Impl::create_swapchain(HWND hwnd, Error* out_error) {
    ComPtr<IDXGIDevice1> dxgi_device;
    device.As(&dxgi_device);
    ComPtr<IDXGIAdapter> adapter;
    dxgi_device->GetAdapter(&adapter);
    ComPtr<IDXGIFactory2> factory;
    adapter->GetParent(IID_PPV_ARGS(&factory));

    RECT rc;
    GetClientRect(hwnd, &rc);
    bb_width  = static_cast<uint32_t>(rc.right - rc.left);
    bb_height = static_cast<uint32_t>(rc.bottom - rc.top);
    if (bb_width == 0) bb_width = 1;
    if (bb_height == 0) bb_height = 1;

    DXGI_SWAP_CHAIN_DESC1 desc = {};
    desc.Width  = bb_width;
    desc.Height = bb_height;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    desc.BufferCount = constants::kSwapchainBufferCount;
    desc.SwapEffect  = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
    desc.Flags       = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;

    ComPtr<IDXGISwapChain1> sc1;
    HRESULT hr = factory->CreateSwapChainForHwnd(
        device.Get(), hwnd, &desc, nullptr, nullptr, &sc1);
    if (FAILED(hr)) {
        LOG_E("renderer", "CreateSwapChainForHwnd failed: 0x%08lX", (unsigned long)hr);
        if (out_error) *out_error = { ErrorCode::SwapchainCreationFailed, "CreateSwapChainForHwnd failed" };
        return false;
    }

    hr = sc1.As(&swapchain);
    if (FAILED(hr)) {
        if (out_error) *out_error = { ErrorCode::SwapchainCreationFailed, "IDXGISwapChain2 not available" };
        return false;
    }

    swapchain->SetMaximumFrameLatency(1);
    frame_latency_waitable = swapchain->GetFrameLatencyWaitableObject();
    factory->MakeWindowAssociation(hwnd, DXGI_MWA_NO_ALT_ENTER);

    LOG_I("renderer", "Swapchain created (%ux%u)", bb_width, bb_height);
    return true;
}

bool DX11Renderer::Impl::create_rtv(Error* out_error) {
    ComPtr<ID3D11Texture2D> backbuffer;
    HRESULT hr = swapchain->GetBuffer(0, IID_PPV_ARGS(&backbuffer));
    if (FAILED(hr)) {
        if (out_error) *out_error = { ErrorCode::SwapchainCreationFailed, "GetBuffer(0) failed" };
        return false;
    }
    hr = device->CreateRenderTargetView(backbuffer.Get(), nullptr, &rtv);
    if (FAILED(hr)) {
        if (out_error) *out_error = { ErrorCode::SwapchainCreationFailed, "CreateRenderTargetView failed" };
        return false;
    }
    return true;
}

// ─── S6: Pipeline (shaders, input layout, buffers, blend state) ───

static ComPtr<ID3DBlob> compile_shader(const char* source, size_t len,
                                        const char* entry, const char* target,
                                        const char* filename) {
    UINT flags = 0;
#if defined(_DEBUG) || defined(DEBUG)
    flags = D3DCOMPILE_DEBUG | D3DCOMPILE_SKIP_OPTIMIZATION;
#endif

    ComPtr<ID3DBlob> blob, errors;
    // Use D3DCompile with includes for shader_common.hlsl
    D3D_SHADER_MACRO macros[] = { {nullptr, nullptr} };

    // Custom include handler for shader_common.hlsl
    class ShaderInclude : public ID3DInclude {
    public:
        std::string base_dir;
        ShaderInclude(const char* dir) : base_dir(dir) {}

        HRESULT __stdcall Open(D3D_INCLUDE_TYPE, LPCSTR pFileName,
                              LPCVOID, LPCVOID* ppData, UINT* pBytes) override {
            std::string path = base_dir + "/" + pFileName;
            FILE* f = fopen(path.c_str(), "rb");
            if (!f) return E_FAIL;
            fseek(f, 0, SEEK_END);
            long sz = ftell(f);
            fseek(f, 0, SEEK_SET);
            char* buf = new char[sz];
            fread(buf, 1, sz, f);
            fclose(f);
            *ppData = buf;
            *pBytes = (UINT)sz;
            return S_OK;
        }
        HRESULT __stdcall Close(LPCVOID pData) override {
            delete[] (char*)pData;
            return S_OK;
        }
    };

    // Derive base dir from filename
    std::string fn(filename);
    std::string dir = ".";
    auto sep = fn.find_last_of("/\\");
    if (sep != std::string::npos) dir = fn.substr(0, sep);

    ShaderInclude inc(dir.c_str());
    HRESULT hr = D3DCompile(source, len, filename, macros, &inc,
                            entry, target, flags, 0, &blob, &errors);
    if (FAILED(hr)) {
        if (errors) {
            LOG_E("shader", "Compile error: %s", (const char*)errors->GetBufferPointer());
        }
        return nullptr;
    }
    return blob;
}

bool DX11Renderer::Impl::create_pipeline(Error* out_error) {
    // Load shader source files
    auto load_file = [](const char* path, std::vector<char>& out) -> bool {
        FILE* f = fopen(path, "rb");
        if (!f) return false;
        fseek(f, 0, SEEK_END);
        long sz = ftell(f);
        fseek(f, 0, SEEK_SET);
        out.resize(sz);
        fread(out.data(), 1, sz, f);
        fclose(f);
        return true;
    };

    // Try relative paths (from build dir and source dir)
    const char* vs_paths[] = {
        "../src/renderer/shader_vs.hlsl",
        "src/renderer/shader_vs.hlsl",
        nullptr
    };
    const char* ps_paths[] = {
        "../src/renderer/shader_ps.hlsl",
        "src/renderer/shader_ps.hlsl",
        nullptr
    };

    std::vector<char> vs_src, ps_src;
    const char* vs_path = nullptr;
    const char* ps_path = nullptr;

    for (auto p = vs_paths; *p; ++p) {
        if (load_file(*p, vs_src)) { vs_path = *p; break; }
    }
    for (auto p = ps_paths; *p; ++p) {
        if (load_file(*p, ps_src)) { ps_path = *p; break; }
    }

    if (!vs_path || !ps_path) {
        LOG_E("renderer", "Shader files not found");
        if (out_error) *out_error = { ErrorCode::ShaderCompilationFailed, "Shader files not found" };
        return false;
    }

    // Compile VS
    auto vs_blob = compile_shader(vs_src.data(), vs_src.size(), "main", "vs_4_0", vs_path);
    if (!vs_blob) {
        if (out_error) *out_error = { ErrorCode::ShaderCompilationFailed, "VS compilation failed" };
        return false;
    }

    HRESULT hr = device->CreateVertexShader(
        vs_blob->GetBufferPointer(), vs_blob->GetBufferSize(), nullptr, &vs);
    if (FAILED(hr)) {
        if (out_error) *out_error = { ErrorCode::ShaderCompilationFailed, "CreateVertexShader failed" };
        return false;
    }

    // Compile PS
    auto ps_blob = compile_shader(ps_src.data(), ps_src.size(), "main", "ps_4_0", ps_path);
    if (!ps_blob) {
        if (out_error) *out_error = { ErrorCode::ShaderCompilationFailed, "PS compilation failed" };
        return false;
    }

    hr = device->CreatePixelShader(
        ps_blob->GetBufferPointer(), ps_blob->GetBufferSize(), nullptr, &ps);
    if (FAILED(hr)) {
        if (out_error) *out_error = { ErrorCode::ShaderCompilationFailed, "CreatePixelShader failed" };
        return false;
    }

    // No Input Layout needed — VS reads from StructuredBuffer via SV_InstanceID

    // Index buffer [0,1,2, 0,2,3]
    uint16_t indices[] = { 0, 1, 2, 0, 2, 3 };
    D3D11_BUFFER_DESC ib_desc = {};
    ib_desc.ByteWidth = sizeof(indices);
    ib_desc.Usage = D3D11_USAGE_IMMUTABLE;
    ib_desc.BindFlags = D3D11_BIND_INDEX_BUFFER;
    D3D11_SUBRESOURCE_DATA ib_data = { indices };
    hr = device->CreateBuffer(&ib_desc, &ib_data, &index_buffer);
    if (FAILED(hr)) return false;

    // Instance buffer (StructuredBuffer, dynamic, initial 1024 instances)
    instance_capacity = 1024;
    D3D11_BUFFER_DESC inst_desc = {};
    inst_desc.ByteWidth = instance_capacity * sizeof(QuadInstance);
    inst_desc.Usage = D3D11_USAGE_DYNAMIC;
    inst_desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    inst_desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    inst_desc.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
    inst_desc.StructureByteStride = sizeof(QuadInstance);
    hr = device->CreateBuffer(&inst_desc, nullptr, &instance_buffer);
    if (FAILED(hr)) return false;
    if (!create_instance_srv()) return false;

    // Constant buffer (VS cbuffer)
    D3D11_BUFFER_DESC cb_desc = {};
    cb_desc.ByteWidth = 16;  // float2 positionScale + float2 atlasScale = 16 bytes
    cb_desc.Usage = D3D11_USAGE_DYNAMIC;
    cb_desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    cb_desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    hr = device->CreateBuffer(&cb_desc, nullptr, &constant_buffer);
    if (FAILED(hr)) return false;

    // Blend state (premultiplied alpha)
    D3D11_BLEND_DESC blend_desc = {};
    blend_desc.RenderTarget[0].BlendEnable = TRUE;
    blend_desc.RenderTarget[0].SrcBlend = D3D11_BLEND_ONE;
    blend_desc.RenderTarget[0].DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
    blend_desc.RenderTarget[0].BlendOp = D3D11_BLEND_OP_ADD;
    blend_desc.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND_ONE;
    blend_desc.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
    blend_desc.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP_ADD;
    blend_desc.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
    hr = device->CreateBlendState(&blend_desc, &blend_state);
    if (FAILED(hr)) return false;

    // Point sampler for glyph atlas
    D3D11_SAMPLER_DESC samp_desc = {};
    samp_desc.Filter = D3D11_FILTER_MIN_MAG_MIP_POINT;
    samp_desc.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
    samp_desc.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
    samp_desc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    hr = device->CreateSamplerState(&samp_desc, &point_sampler);
    if (FAILED(hr)) return false;

    update_constant_buffer();

    // Debug names (Design 7.1 FR-12)
#if defined(_DEBUG) || defined(DEBUG)
    auto set_name = [](ID3D11DeviceChild* obj, const char* name) {
        if (obj) obj->SetPrivateData(WKPDID_D3DDebugObjectName, (UINT)strlen(name), name);
    };
    set_name(index_buffer.Get(), "IndexBuffer");
    set_name(instance_buffer.Get(), "InstanceBuffer");
    set_name(constant_buffer.Get(), "ConstantBuffer");
    set_name(vs.Get(), "VertexShader");
    set_name(ps.Get(), "PixelShader");
    set_name(blend_state.Get(), "BlendState");
    set_name(point_sampler.Get(), "PointSampler");
#endif

    LOG_I("renderer", "Pipeline created (shaders + StructuredBuffer + blend)");
    return true;
}

bool DX11Renderer::Impl::create_instance_srv() {
    instance_srv.Reset();
    D3D11_SHADER_RESOURCE_VIEW_DESC srv_desc = {};
    srv_desc.Format = DXGI_FORMAT_UNKNOWN;
    srv_desc.ViewDimension = D3D11_SRV_DIMENSION_BUFFER;
    srv_desc.Buffer.NumElements = instance_capacity;
    HRESULT hr = device->CreateShaderResourceView(
        instance_buffer.Get(), &srv_desc, &instance_srv);
    if (FAILED(hr)) {
        LOG_E("renderer", "Instance SRV creation failed: 0x%08lX", (unsigned long)hr);
        return false;
    }
    return true;
}

void DX11Renderer::Impl::update_constant_buffer() {
    struct {
        float pos_scale_x, pos_scale_y;
        float atlas_scale_x, atlas_scale_y;
    } data = {
        2.0f / bb_width, -2.0f / bb_height,
        1.0f / atlas_w, 1.0f / atlas_h
    };

    D3D11_MAPPED_SUBRESOURCE mapped;
    if (SUCCEEDED(context->Map(constant_buffer.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped))) {
        memcpy(mapped.pData, &data, sizeof(data));
        context->Unmap(constant_buffer.Get(), 0);
    }
}

void DX11Renderer::Impl::draw_instances(uint32_t count) {
    if (count == 0) return;

    float clear_color[4] = { 0.1f, 0.1f, 0.15f, 1.0f };
    context->ClearRenderTargetView(rtv.Get(), clear_color);

    context->OMSetRenderTargets(1, rtv.GetAddressOf(), nullptr);
    context->OMSetBlendState(blend_state.Get(), nullptr, 0xFFFFFFFF);

    D3D11_VIEWPORT vp = { 0, 0, (float)bb_width, (float)bb_height, 0, 1 };
    context->RSSetViewports(1, &vp);

    context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    context->IASetInputLayout(nullptr);  // no input layout (StructuredBuffer)
    context->IASetIndexBuffer(index_buffer.Get(), DXGI_FORMAT_R16_UINT, 0);

    context->VSSetShader(vs.Get(), nullptr, 0);
    context->VSSetConstantBuffers(0, 1, constant_buffer.GetAddressOf());
    // Bind instance StructuredBuffer to VS t1
    context->VSSetShaderResources(1, 1, instance_srv.GetAddressOf());

    context->PSSetShader(ps.Get(), nullptr, 0);
    context->PSSetSamplers(0, 1, point_sampler.GetAddressOf());

    // Bind glyph atlas texture to PS t0
    if (atlas_srv) {
        context->PSSetShaderResources(0, 1, atlas_srv.GetAddressOf());
    }

    context->DrawIndexedInstanced(constants::kIndexCount, count, 0, 0, 0);
    swapchain->Present(1, 0);

    stats.frame_count++;
    stats.instance_count = count;
}

// ─── Public API ───

DX11Renderer::DX11Renderer() : impl_(std::make_unique<Impl>()) {}
DX11Renderer::~DX11Renderer() {
    report_live_objects();
    if (impl_->frame_latency_waitable) {
        CloseHandle(impl_->frame_latency_waitable);
    }
}

std::unique_ptr<DX11Renderer> DX11Renderer::create(const RendererConfig& config, Error* out_error) {
    if (!config.hwnd) {
        if (out_error) *out_error = { ErrorCode::InvalidArgument, "hwnd is NULL" };
        return nullptr;
    }

    auto r = std::unique_ptr<DX11Renderer>(new DX11Renderer());

    if (!r->impl_->create_device(out_error)) return nullptr;
    if (!r->impl_->create_swapchain(config.hwnd, out_error)) return nullptr;
    if (!r->impl_->create_rtv(out_error)) return nullptr;
    if (!r->impl_->create_pipeline(out_error)) return nullptr;

    return r;
}

void DX11Renderer::clear_and_present(float r, float g, float b) {
    float color[4] = { r, g, b, 1.0f };
    impl_->context->ClearRenderTargetView(impl_->rtv.Get(), color);
    impl_->swapchain->Present(1, 0);
}

void DX11Renderer::draw_test_quad(int16_t x, int16_t y, uint16_t w, uint16_t h,
                                   uint8_t r, uint8_t g, uint8_t b) {
    auto* ctx = impl_->context.Get();

    // Build one QuadInstance (background rectangle)
    QuadInstance q = {};
    q.shading_type = 0;  // TextBackground
    q.pos_x = (uint16_t)x;
    q.pos_y = (uint16_t)y;
    q.size_x = w;
    q.size_y = h;
    uint32_t color = r | (g << 8) | (b << 16) | (0xFF << 24);
    q.fg_packed = color;
    q.bg_packed = color;
    q.reserved = 0;

    // Upload instance
    D3D11_MAPPED_SUBRESOURCE mapped;
    ctx->Map(impl_->instance_buffer.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
    memcpy(mapped.pData, &q, sizeof(q));
    ctx->Unmap(impl_->instance_buffer.Get(), 0);

    impl_->draw_instances(1);
}

void DX11Renderer::resize_swapchain(uint32_t width_px, uint32_t height_px) {
    if (width_px == 0 || height_px == 0) return;

    impl_->rtv.Reset();
    impl_->context->ClearState();

    HRESULT hr = impl_->swapchain->ResizeBuffers(
        0, width_px, height_px,
        DXGI_FORMAT_UNKNOWN,
        DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT);

    if (FAILED(hr)) {
        LOG_E("renderer", "ResizeBuffers failed: 0x%08lX", (unsigned long)hr);
        return;
    }

    impl_->bb_width = width_px;
    impl_->bb_height = height_px;

    Error err{};
    impl_->create_rtv(&err);
    impl_->update_constant_buffer();
}

void DX11Renderer::report_live_objects() {
#if defined(_DEBUG) || defined(DEBUG)
    if (!impl_->device) return;
    ComPtr<ID3D11Debug> debug;
    impl_->device.As(&debug);
    if (debug) {
        debug->ReportLiveDeviceObjects(D3D11_RLDO_SUMMARY);
    }
#endif
}

void DX11Renderer::upload_and_draw(const void* instances, uint32_t count) {
    if (count == 0) return;
    auto* ctx = impl_->context.Get();

    // Ensure instance buffer is large enough
    if (count > impl_->instance_capacity) {
        impl_->instance_capacity = count * 2;
        D3D11_BUFFER_DESC desc = {};
        desc.ByteWidth = impl_->instance_capacity * sizeof(QuadInstance);
        desc.Usage = D3D11_USAGE_DYNAMIC;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        desc.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
        desc.StructureByteStride = sizeof(QuadInstance);
        impl_->instance_buffer.Reset();
        impl_->device->CreateBuffer(&desc, nullptr, &impl_->instance_buffer);
        impl_->create_instance_srv();
    }

    // Upload
    D3D11_MAPPED_SUBRESOURCE mapped;
    ctx->Map(impl_->instance_buffer.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
    memcpy(mapped.pData, instances, count * sizeof(QuadInstance));
    ctx->Unmap(impl_->instance_buffer.Get(), 0);

    impl_->draw_instances(count);
}

void DX11Renderer::set_atlas_srv(ID3D11ShaderResourceView* srv) {
    impl_->atlas_srv = srv;
    // Query atlas texture size for constant buffer
    if (srv) {
        ComPtr<ID3D11Resource> res;
        srv->GetResource(&res);
        ComPtr<ID3D11Texture2D> tex;
        if (SUCCEEDED(res.As(&tex))) {
            D3D11_TEXTURE2D_DESC desc;
            tex->GetDesc(&desc);
            impl_->atlas_w = desc.Width;
            impl_->atlas_h = desc.Height;
            impl_->update_constant_buffer();
        }
    }
}

uint32_t DX11Renderer::backbuffer_width() const { return impl_->bb_width; }
uint32_t DX11Renderer::backbuffer_height() const { return impl_->bb_height; }

ID3D11Device* DX11Renderer::device() const { return impl_->device.Get(); }
ID3D11DeviceContext* DX11Renderer::context() const { return impl_->context.Get(); }

} // namespace ghostwin
