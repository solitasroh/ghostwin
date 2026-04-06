using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using GhostWin.Core.Interfaces;

namespace GhostWin.Interop;

internal static class NativeCallbacks
{
    private static GwCallbackContext? _context;
    private static Dispatcher? _dispatcher;

    internal static void Initialize(GwCallbackContext context, Dispatcher dispatcher)
    {
        _context = context;
        _dispatcher = dispatcher;
    }

    internal static void Cleanup()
    {
        _context = null;
        _dispatcher = null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnCreated(nint ctx, uint sessionId)
    {
        var c = _context; var d = _dispatcher;
        if (c?.OnSessionCreated == null || d == null) return;
        d.BeginInvoke(() => c.OnSessionCreated(sessionId));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnClosed(nint ctx, uint sessionId)
    {
        var c = _context; var d = _dispatcher;
        if (c?.OnSessionClosed == null || d == null) return;
        d.BeginInvoke(() => c.OnSessionClosed(sessionId));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnActivated(nint ctx, uint sessionId)
    {
        var c = _context; var d = _dispatcher;
        if (c?.OnSessionActivated == null || d == null) return;
        d.BeginInvoke(() => c.OnSessionActivated(sessionId));
    }

    // len = wchar_t 문자 수 (not bytes). ghostwin_engine.cpp: title.size()
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe void OnTitleChanged(nint ctx, uint sessionId, nint titlePtr, uint len)
    {
        var title = new string((char*)titlePtr, 0, (int)len);
        var c = _context; var d = _dispatcher;
        if (c?.OnTitleChanged == null || d == null) return;
        d.BeginInvoke(() => c.OnTitleChanged(sessionId, title));
    }

    // len = wchar_t 문자 수 (not bytes). ghostwin_engine.cpp: cwd.size()
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe void OnCwdChanged(nint ctx, uint sessionId, nint cwdPtr, uint len)
    {
        var cwd = new string((char*)cwdPtr, 0, (int)len);
        var c = _context; var d = _dispatcher;
        if (c?.OnCwdChanged == null || d == null) return;
        d.BeginInvoke(() => c.OnCwdChanged(sessionId, cwd));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnChildExit(nint ctx, uint sessionId, uint exitCode)
    {
        var c = _context; var d = _dispatcher;
        if (c?.OnChildExit == null || d == null) return;
        d.BeginInvoke(() => c.OnChildExit(sessionId, exitCode));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnRenderDone(nint ctx)
    {
        _context?.OnRenderDone?.Invoke();
    }
}
