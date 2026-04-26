namespace GhostWin.MeasurementDriver.Contracts;

public sealed record DriverOptions(
    string Scenario,
    int GhostWinPid,
    string OutputJsonPath,
    string? Workload = null)
{
    public static DriverOptions Parse(string[] args)
    {
        string? scenario = null;
        string? outputJson = null;
        int? pid = null;
        string? workload = null;

        for (var i = 0; i < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--scenario":
                    scenario = args[i + 1];
                    break;
                case "--pid":
                    pid = int.Parse(args[i + 1]);
                    break;
                case "--output-json":
                    outputJson = args[i + 1];
                    break;
                case "--workload":
                    workload = args[i + 1];
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(scenario) || pid is null || string.IsNullOrWhiteSpace(outputJson))
        {
            throw new ArgumentException("Missing required measurement driver arguments.");
        }

        // M-15 Stage A: load scenario without an explicit workload uses a
        // recursive listing of System32 as a representative output-bound load
        // (matches the Plan §Task 5 default). Specify --workload to override.
        if (string.Equals(scenario, "load", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(workload))
        {
            workload = @"Get-ChildItem -Recurse C:\Windows\System32 | Format-List";
        }

        return new DriverOptions(scenario, pid.Value, outputJson, workload);
    }
}
