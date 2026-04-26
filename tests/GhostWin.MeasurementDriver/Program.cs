using System.Text.Json;
using GhostWin.MeasurementDriver.Contracts;

var options = DriverOptions.Parse(args);
var result = DriverResult.Success(options.Scenario, "pending");

Directory.CreateDirectory(Path.GetDirectoryName(options.OutputJsonPath)!);
await File.WriteAllTextAsync(
    options.OutputJsonPath,
    JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

return 0;
