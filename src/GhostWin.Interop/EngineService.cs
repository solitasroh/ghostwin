using System.Runtime.InteropServices;
using System.Windows.Threading;
using GhostWin.Core.Interfaces;

namespace GhostWin.Interop;

public class EngineService : IEngineService
{
    private nint _engine;
    private GCHandle _pinHandle;

    public bool IsInitialized => _engine != IntPtr.Zero;
    public uint SessionCount => _engine != IntPtr.Zero ? NativeEngine.gw_session_count(_engine) : 0;
    public uint ActiveSessionId => _engine != IntPtr.Zero ? NativeEngine.gw_active_session_id(_engine) : 0;
    public nint Handle => _engine;

    public unsafe void Initialize(GwCallbackContext callbackContext)
    {
        if (_engine != IntPtr.Zero) return;

        NativeCallbacks.Initialize(callbackContext, Dispatcher.CurrentDispatcher);

        _pinHandle = GCHandle.Alloc(this);

        var callbacks = new GwCallbacks
        {
            Context = GCHandle.ToIntPtr(_pinHandle),
            OnCreated = (nint)(delegate* unmanaged[Cdecl]<nint, uint, void>)
                        &NativeCallbacks.OnCreated,
            OnClosed = (nint)(delegate* unmanaged[Cdecl]<nint, uint, void>)
                       &NativeCallbacks.OnClosed,
            OnActivated = (nint)(delegate* unmanaged[Cdecl]<nint, uint, void>)
                          &NativeCallbacks.OnActivated,
            OnTitleChanged = (nint)(delegate* unmanaged[Cdecl]<nint, uint, nint, uint, void>)
                             &NativeCallbacks.OnTitleChanged,
            OnCwdChanged = (nint)(delegate* unmanaged[Cdecl]<nint, uint, nint, uint, void>)
                           &NativeCallbacks.OnCwdChanged,
            OnChildExit = (nint)(delegate* unmanaged[Cdecl]<nint, uint, uint, void>)
                          &NativeCallbacks.OnChildExit,
            OnRenderDone = (nint)(delegate* unmanaged[Cdecl]<nint, void>)
                           &NativeCallbacks.OnRenderDone,
        };

        _engine = NativeEngine.gw_engine_create(in callbacks);
    }

    public void Shutdown()
    {
        if (_engine == IntPtr.Zero) return;
        NativeEngine.gw_render_stop(_engine);
        NativeEngine.gw_engine_destroy(_engine);
        _engine = IntPtr.Zero;
        NativeCallbacks.Cleanup();
        if (_pinHandle.IsAllocated) _pinHandle.Free();
    }

    public void Dispose()
    {
        Shutdown();
        GC.SuppressFinalize(this);
    }

    public int RenderInit(nint hwnd, uint widthPx, uint heightPx, float fontSizePt, string fontFamily)
        => NativeEngine.gw_render_init(_engine, hwnd, widthPx, heightPx, fontSizePt, fontFamily);

    /// <remarks>
    /// Deprecated (2026-04-07): see IEngineService.RenderResize. Native side
    /// is now a no-op; kept for ABI compatibility.
    /// </remarks>
    public int RenderResize(uint widthPx, uint heightPx)
        => NativeEngine.gw_render_resize(_engine, widthPx, heightPx);

    public int RenderSetClearColor(uint rgb)
        => NativeEngine.gw_render_set_clear_color(_engine, rgb);

    public void RenderStart()
        => NativeEngine.gw_render_start(_engine);

    public void RenderStop()
        => NativeEngine.gw_render_stop(_engine);

    public uint CreateSession(string? shellPath, string? initialDir, ushort cols, ushort rows)
        => NativeEngine.gw_session_create(_engine, shellPath, initialDir, cols, rows);

    public int CloseSession(uint id)
        => NativeEngine.gw_session_close(_engine, id);

    public void ActivateSession(uint id)
        => NativeEngine.gw_session_activate(_engine, id);

    public int WriteSession(uint id, ReadOnlySpan<byte> data)
    {
        unsafe
        {
            fixed (byte* ptr = data)
                return NativeEngine.gw_session_write(_engine, id, (nint)ptr, (uint)data.Length);
        }
    }

    public int WriteMouseEvent(uint sessionId, float xPx, float yPx,
                               uint button, uint action, uint mods)
        => NativeEngine.gw_session_write_mouse(_engine, sessionId, xPx, yPx, button, action, mods);

    public int ScrollViewport(uint sessionId, int deltaRows)
        => NativeEngine.gw_scroll_viewport(_engine, sessionId, deltaRows);

    public int ResizeSession(uint id, ushort cols, ushort rows)
        => NativeEngine.gw_session_resize(_engine, id, cols, rows);

    public int TsfAttach(nint hiddenHwnd)
        => NativeEngine.gw_tsf_attach(_engine, hiddenHwnd);

    public int TsfFocus(uint sessionId)
        => NativeEngine.gw_tsf_focus(_engine, sessionId);

    public int TsfUnfocus()
        => NativeEngine.gw_tsf_unfocus(_engine);

    public int TsfSendPending()
        => NativeEngine.gw_tsf_send_pending(_engine);

    public void PollTitles()
        => NativeEngine.gw_poll_titles(_engine);

    public uint SurfaceCreate(nint hwnd, uint sessionId, uint widthPx, uint heightPx)
        => NativeEngine.gw_surface_create(_engine, hwnd, sessionId, widthPx, heightPx);

    public int SurfaceDestroy(uint id)
        => NativeEngine.gw_surface_destroy(_engine, id);

    public int SurfaceResize(uint id, uint widthPx, uint heightPx)
        => NativeEngine.gw_surface_resize(_engine, id, widthPx, heightPx);

    public int SurfaceFocus(uint id)
        => NativeEngine.gw_surface_focus(_engine, id);
}
