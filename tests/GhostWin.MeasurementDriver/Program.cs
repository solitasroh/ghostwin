using System.Text.Json;
using GhostWin.MeasurementDriver.Contracts;
using GhostWin.MeasurementDriver.Infrastructure;
using GhostWin.MeasurementDriver.Scenario;

var options = DriverOptions.Parse(args);
Directory.CreateDirectory(Path.GetDirectoryName(options.OutputJsonPath)!);

var hwnd = MainWindowFinder.WaitForMainWindow(options.GhostWinPid, TimeSpan.FromSeconds(10));
if (hwnd == nint.Zero)
{
    var fail = DriverResult.Failure(options.Scenario, "driver", "main window not found");
    await File.WriteAllTextAsync(options.OutputJsonPath, JsonSerializer.Serialize(fail));
    return 1;
}

var controller = new GhostWinController(hwnd);

DriverResult result = options.Scenario switch
{
    "idle" => IdleSuccess(controller),
    "resize-4pane" => ResizeFourPaneScenario.Execute(controller),
    _ => DriverResult.Failure(options.Scenario, "driver", $"unsupported scenario: {options.Scenario}")
};

await File.WriteAllTextAsync(options.OutputJsonPath, JsonSerializer.Serialize(result));
return result.Valid ? 0 : 1;

// Local helper: idle just needs the window foregrounded (CPU/render samples
// come from the PowerShell launcher's typeperf + GHOSTWIN_RENDER_PERF logs).
static DriverResult IdleSuccess(GhostWinController controller)
{
    controller.BringToForeground();
    return DriverResult.Success("idle", "1pane");
}
