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

        return new DriverOptions(scenario, pid.Value, outputJson, workload);
    }
}
