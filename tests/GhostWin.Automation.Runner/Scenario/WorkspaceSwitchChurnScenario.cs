using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using GhostWin.Automation.Runner.Measurement.Contracts;
using GhostWin.Automation.Runner.Measurement.Infrastructure;

namespace GhostWin.Automation.Runner.Measurement.Scenario;

internal static class WorkspaceSwitchChurnScenario
{
    private const int ExpectedActions = 7;
    private static readonly string[] ArtifactNames =
    [
        "workspace-events.csv",
        "workspace-geometry.json",
    ];

    public static DriverResult Execute(GhostWinController controller, string artifactDir)
    {
        Directory.CreateDirectory(artifactDir);

        using var automation = new UIA3Automation();
        var window = automation.FromHandle(controller.MainWindowHandle).AsWindow();
        var stopwatch = Stopwatch.StartNew();
        var events = new List<DriverEvent>();
        var geometry = new List<PaneGeometrySnapshot>();

        controller.BringToForeground();
        GhostWinController.Settle();
        Capture("initial", "none");

        RunAction("split-active-workspace", controller.SplitVertical);
        RunAction("new-workspace-1", controller.NewWorkspace);
        RunAction("new-workspace-2", controller.NewWorkspace);
        RunAction("next-workspace-1", controller.NextWorkspace);
        RunAction("next-workspace-2", controller.NextWorkspace);
        RunAction("next-workspace-3", controller.NextWorkspace);
        RunAction("next-workspace-4", controller.NextWorkspace);

        WriteEvents(Path.Combine(artifactDir, ArtifactNames[0]), events);
        File.WriteAllText(
            Path.Combine(artifactDir, ArtifactNames[1]),
            JsonSerializer.Serialize(geometry, new JsonSerializerOptions { WriteIndented = true }));

        var maxPanes = events.Count == 0 ? 0 : events.Max(e => e.PaneCount);
        var activePaneVisible = events.All(e => e.PaneCount > 0);
        return activePaneVisible && maxPanes >= 2
            ? DriverResult.Success(
                "workspace-switch-churn",
                "workspace",
                maxPanes,
                observedActions: ExpectedActions,
                artifacts: ArtifactNames)
            : DriverResult.Failure(
                "workspace-switch-churn",
                "workspace",
                $"workspace switch did not keep visible panes (max={maxPanes})",
                maxPanes,
                observedActions: ExpectedActions,
                artifacts: ArtifactNames);

        void RunAction(string step, Action action)
        {
            action();
            GhostWinController.Settle();
            Capture(step, step);
        }

        void Capture(string step, string action)
        {
            var panes = FindTerminalHosts(window)
                .Select((host, index) => PaneRect.From(host, index))
                .ToArray();
            events.Add(new DriverEvent(
                step,
                action,
                stopwatch.Elapsed.TotalMilliseconds,
                panes.Length));
            geometry.Add(new PaneGeometrySnapshot(step, panes.Length, panes));
        }
    }

    private static AutomationElement[] FindTerminalHosts(Window window)
        => window.FindAllDescendants(cf => cf.ByAutomationId("E2E_TerminalHost"));

    private static void WriteEvents(string path, IReadOnlyList<DriverEvent> events)
    {
        var sb = new StringBuilder();
        sb.AppendLine("step,action,elapsed_ms,pane_count");
        foreach (var evt in events)
        {
            sb.Append(evt.Step).Append(',')
              .Append(evt.Action).Append(',')
              .Append(evt.ElapsedMs.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
              .Append(evt.PaneCount.ToString(CultureInfo.InvariantCulture))
              .AppendLine();
        }

        File.WriteAllText(path, sb.ToString());
    }

    private sealed record DriverEvent(
        string Step,
        string Action,
        double ElapsedMs,
        int PaneCount);

    private sealed record PaneGeometrySnapshot(
        string Step,
        int PaneCount,
        IReadOnlyList<PaneRect> Panes);

    private sealed record PaneRect(
        int Index,
        double X,
        double Y,
        double Width,
        double Height)
    {
        public static PaneRect From(AutomationElement element, int index)
        {
            var rect = element.BoundingRectangle;
            return new PaneRect(index, rect.X, rect.Y, rect.Width, rect.Height);
        }
    }
}
