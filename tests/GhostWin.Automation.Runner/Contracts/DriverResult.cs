namespace GhostWin.Automation.Runner.Measurement.Contracts;

public sealed record DriverResult(
    string Scenario,
    string Mode,
    bool Valid,
    int? ObservedPanes,
    string? Reason,
    int? ObservedActions = null,
    IReadOnlyList<string>? Artifacts = null)
{
    public static DriverResult Success(
        string scenario,
        string mode,
        int? observedPanes = null,
        int? observedActions = null,
        IReadOnlyList<string>? artifacts = null)
        => new(scenario, mode, true, observedPanes, null, observedActions, artifacts);

    public static DriverResult Failure(
        string scenario,
        string mode,
        string reason,
        int? observedPanes = null,
        int? observedActions = null,
        IReadOnlyList<string>? artifacts = null)
        => new(scenario, mode, false, observedPanes, reason, observedActions, artifacts);
}
