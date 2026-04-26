namespace GhostWin.MeasurementDriver.Contracts;

public sealed record DriverResult(
    string Scenario,
    string Mode,
    bool Valid,
    int? ObservedPanes,
    string? Reason)
{
    public static DriverResult Success(string scenario, string mode, int? observedPanes = null)
        => new(scenario, mode, true, observedPanes, null);

    public static DriverResult Failure(string scenario, string mode, string reason, int? observedPanes = null)
        => new(scenario, mode, false, observedPanes, reason);
}
