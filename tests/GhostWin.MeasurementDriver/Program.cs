using System.Text.Json;
using GhostWin.MeasurementDriver.Contracts;
using GhostWin.MeasurementDriver.Infrastructure;

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
controller.BringToForeground();

var result = DriverResult.Success(options.Scenario, "driver");
await File.WriteAllTextAsync(options.OutputJsonPath, JsonSerializer.Serialize(result));
return 0;
