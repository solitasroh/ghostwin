using System.Runtime.InteropServices;
using System.Windows;
using GhostWinPoC.Interop;
using Wpf.Ui.Appearance;

namespace GhostWinPoC;

public partial class App : Application
{
    internal nint EngineHandle { get; private set; }
    private GCHandle _pinHandle;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ghostwin_csharp_debug.log"), "OnStartup entered\n");

        // Apply dark theme
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);

        // Pin this App instance for native callbacks
        _pinHandle = GCHandle.Alloc(this);

        // Create engine with callbacks
        var callbacks = new GwCallbacks
        {
            Context = GCHandle.ToIntPtr(_pinHandle),
            // Callbacks will be wired up when MainWindow loads
        };
        System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ghostwin_csharp_debug.log"), "calling gw_engine_create...\n");
        EngineHandle = NativeEngine.gw_engine_create(in callbacks);
        System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ghostwin_csharp_debug.log"), $"engine handle: 0x{EngineHandle:X}\n");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (EngineHandle != IntPtr.Zero)
        {
            NativeEngine.gw_render_stop(EngineHandle);
            NativeEngine.gw_engine_destroy(EngineHandle);
            EngineHandle = IntPtr.Zero;
        }

        if (_pinHandle.IsAllocated)
            _pinHandle.Free();

        base.OnExit(e);
    }

    // Callback targets (called from native I/O thread → dispatch to UI)
    internal void OnSessionTitleChanged(uint sessionId, string title)
    {
        // PoC: just update window title
        Dispatcher.BeginInvoke(() =>
        {
            if (MainWindow is MainWindow mw)
                mw.Title = $"GhostWin PoC — {title}";
        });
    }

    internal void OnSessionExited(uint sessionId, uint exitCode)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // PoC: close app when last session exits
            MainWindow?.Close();
        });
    }
}
