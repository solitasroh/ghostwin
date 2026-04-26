using GhostWin.MeasurementDriver.Contracts;

namespace GhostWin.MeasurementDriver.Verification;

public static class PaneCountVerifier
{
    public static DriverResult Evaluate(int expected, int observed)
    {
        return observed == expected
            ? DriverResult.Success("resize", "4pane", observed)
            : DriverResult.Failure(
                "resize",
                "4pane",
                $"pane count mismatch (expected {expected}, observed {observed})",
                observed);
    }
}
