using System.Text.Json;
using GhostWin.Automation.Runner.Measurement.Contracts;
using GhostWin.Automation.Runner.Measurement.Infrastructure;
using GhostWin.Automation.Runner.Measurement.Scenario;

var options = DriverOptions.Parse(args);
Directory.CreateDirectory(Path.GetDirectoryName(options.OutputJsonPath)!);
var artifactDir = options.ArtifactDir ?? Path.GetDirectoryName(options.OutputJsonPath)!;
Directory.CreateDirectory(artifactDir);

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
    "pane-split-churn" => PaneSplitChurnScenario.Execute(controller, artifactDir),
    "workspace-switch-churn" => WorkspaceSwitchChurnScenario.Execute(controller, artifactDir),
    "load" => LoadScenario.Execute(controller, options.Workload!),
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
