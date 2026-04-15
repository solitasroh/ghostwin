using System.Runtime.InteropServices;
using System.Windows.Threading;
using GhostWin.Core.Interfaces;

namespace GhostWin.Interop;

public class EngineService : IEngineService
{
    private nint _engine;
    private GCHandle _pinHandle;

    public bool IsInitialized => _engine != IntPtr.Zero;
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

    public void DetachCallbacks()
    {
        if (_engine == IntPtr.Zero) return;
        // ★ C++ 측: GwCallbacks struct의 모든 function pointer를 NULL로.
        // I/O 스레드가 fire_exit_event → events_.on_child_exit 호출 시
        // NULL check에서 skip → C# 코드에 도달하지 않음.
        NativeEngine.gw_engine_detach_callbacks(_engine);
        // ★ C# 측: _context/_dispatcher를 null로.
        // Cleanup()보다 먼저 detach해야 하는 이유:
        // NativeCallbacks.OnChildExit에서 로컬 변수에 _context를 캡처한 후
        // Cleanup()이 실행되면, 이미 캡처된 참조로 BeginInvoke가 큐에 들어간다.
        // C++ 측에서 먼저 차단하면 이 race window가 발생하지 않는다.
        NativeCallbacks.Cleanup();
    }

    public void Shutdown()
    {
        if (_engine == IntPtr.Zero) return;

        // Defensive: ensure callbacks detached even if caller skipped DetachCallbacks.
        DetachCallbacks();

        NativeEngine.gw_render_stop(_engine);
        NativeEngine.gw_engine_destroy(_engine);
        _engine = IntPtr.Zero;
        if (_pinHandle.IsAllocated) _pinHandle.Free();
    }

    public void Dispose()
    {
        Shutdown();
        GC.SuppressFinalize(this);
    }

    public int RenderInit(nint hwnd, uint widthPx, uint heightPx, float fontSizePt, string fontFamily, float dpiScale = 1.0f)
        => NativeEngine.gw_render_init(_engine, hwnd, widthPx, heightPx, fontSizePt, fontFamily, dpiScale);

    public int UpdateCellMetrics(float fontSizePt, string fontFamily, float dpiScale,
                                   float cellWidthScale, float cellHeightScale, float zoom)
        => NativeEngine.gw_update_cell_metrics(_engine, fontSizePt, fontFamily, dpiScale,
                                                cellWidthScale, cellHeightScale, zoom);

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

    public void SetSelection(uint sessionId, int startRow, int startCol,
                             int endRow, int endCol, bool active)
    {
        if (_engine == IntPtr.Zero) return;
        NativeEngine.gw_session_set_selection(
            _engine, sessionId, startRow, startCol, endRow, endCol, active ? 1 : 0);
    }

    public void GetCellSize(out uint cellWidth, out uint cellHeight)
    {
        cellWidth = 0;
        cellHeight = 0;
        if (_engine == IntPtr.Zero) return;
        NativeEngine.gw_get_cell_size(_engine, out cellWidth, out cellHeight);
    }

    public unsafe string GetCellText(uint sessionId, int row, int col)
    {
        if (_engine == IntPtr.Zero) return string.Empty;
        const int bufSize = 32; // max 4 codepoints * 4 bytes UTF-8 + null
        byte* buf = stackalloc byte[bufSize];
        int written = NativeEngine.gw_session_get_cell_text(
            _engine, sessionId, row, col, (nint)buf, (uint)bufSize);
        if (written <= 0) return string.Empty;
        return System.Text.Encoding.UTF8.GetString(buf, written);
    }

    public unsafe string GetSelectedText(uint sessionId, int startRow, int startCol,
                                         int endRow, int endCol)
    {
        if (_engine == IntPtr.Zero) return string.Empty;
        const int bufSize = 65536; // 64KB — adequate for most selections
        var buffer = new byte[bufSize];
        fixed (byte* ptr = buffer)
        {
            int result = NativeEngine.gw_session_get_selected_text(
                _engine, sessionId, startRow, startCol, endRow, endCol,
                (nint)ptr, (uint)bufSize, out uint written);
            if (result != 0 || written == 0) return string.Empty;
            return System.Text.Encoding.UTF8.GetString(ptr, (int)written);
        }
    }

    public bool GetMode(uint sessionId, ushort mode)
    {
        if (_engine == IntPtr.Zero) return false;
        return NativeEngine.gw_session_mode_get(_engine, sessionId, mode);
    }

    public (int startCol, int endCol) FindWordBounds(uint sessionId, int row, int col)
    {
        NativeEngine.gw_session_find_word_bounds(_engine, sessionId, row, col,
            out int s, out int e);
        return (s, e);
    }

    public (int startCol, int endCol) FindLineBounds(uint sessionId, int row)
    {
        NativeEngine.gw_session_find_line_bounds(_engine, sessionId, row,
            out int s, out int e);
        return (s, e);
    }
}
