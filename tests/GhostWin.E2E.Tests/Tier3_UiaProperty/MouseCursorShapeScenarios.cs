using System.Threading;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using GhostWin.E2E.Tests.Infrastructure;
using GhostWin.E2E.Tests.Stubs;
using Xunit;

namespace GhostWin.E2E.Tests.Tier3_UiaProperty;

[Trait("Tier", "3")]
[Trait("Category", "E2E")]
[Collection("GhostWin-App")]
public class MouseCursorShapeScenarios : IClassFixture<GhostWinAppFixture>
{
    private readonly GhostWinAppFixture _fixture;

    public MouseCursorShapeScenarios(GhostWinAppFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("text", "shape=8 (TEXT)", "cursorId=32513 (IDC_IBEAM)")]
    [InlineData("pointer", "shape=3 (POINTER)", "cursorId=32649 (IDC_HAND)")]
    [InlineData("ew-resize", "shape=28 (EW_RESIZE)", "cursorId=32644 (IDC_SIZEWE)")]
    [InlineData("default", "shape=0 (DEFAULT)", "cursorId=32512 (IDC_ARROW)")]
    public void InjectOsc22_UpdatesCursorOracle(string value, string expectedShape, string expectedCursorId)
    {
        var sessionId = WaitForSessionProbe();
        OscInjector.InjectOsc22ToRunningApp(value, sessionId);

        WaitForProbeContains(AutomationIds.MouseCursorShape, expectedShape);
        WaitForProbeContains(AutomationIds.MouseCursorId, expectedCursorId);
        WaitForProbeContains(AutomationIds.MouseCursorSession, $"sessionId={sessionId}");
    }

    [Fact]
    public void RepeatedSameShapeInjection_KeepsStableProbeValue()
    {
        var sessionId = WaitForSessionProbe();
        OscInjector.InjectOsc22ToRunningApp("text", sessionId);
        WaitForProbeContains(AutomationIds.MouseCursorShape, "shape=8 (TEXT)");

        var firstShape = ReadProbeValue(AutomationIds.MouseCursorShape);
        var firstCursor = ReadProbeValue(AutomationIds.MouseCursorId);
        var firstSession = ReadProbeValue(AutomationIds.MouseCursorSession);

        OscInjector.InjectOsc22ToRunningApp("text", sessionId);

        WaitForProbeContains(AutomationIds.MouseCursorShape, firstShape);
        ReadProbeValue(AutomationIds.MouseCursorId).Should().Be(firstCursor);
        ReadProbeValue(AutomationIds.MouseCursorSession).Should().Be(firstSession);
    }

    [Fact]
    public void NewWorkspaceInjection_ChangesProbeSession()
    {
        var firstSessionId = WaitForSessionProbe();
        var firstSession = $"sessionId={firstSessionId}";

        var newWorkspaceButton = _fixture.MainWindow.FindFirstDescendant(
            cf => cf.ByAutomationId(AutomationIds.NewWorkspace));
        newWorkspaceButton.Should().NotBeNull();
        newWorkspaceButton!.Patterns.Invoke.Pattern.Invoke();

        var secondSessionId = WaitForSessionProbeChange(firstSession);
        OscInjector.InjectOsc22ToRunningApp("pointer", secondSessionId);

        WaitForProbeContains(AutomationIds.MouseCursorShape, "shape=3 (POINTER)");
        var secondSession = ReadProbeValue(AutomationIds.MouseCursorSession);
        secondSession.Should().NotBe(firstSession);
    }

    private void WaitForProbeContains(string automationId, string expectedFragment)
    {
        string currentValue = string.Empty;
        var matched = SpinWait.SpinUntil(() =>
        {
            currentValue = ReadProbeValue(automationId);
            return currentValue.Contains(expectedFragment, StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(8));

        matched.Should().BeTrue(
            $"probe {automationId} should contain '{expectedFragment}', current='{currentValue}'");
    }

    private string ReadProbeValue(string automationId)
    {
        var element = _fixture.MainWindow.FindFirstDescendant(
            cf => cf.ByAutomationId(automationId));
        element.Should().NotBeNull($"AutomationId '{automationId}' should exist");
        return ReadElementText(element!);
    }

    private static string ReadElementText(AutomationElement element)
    {
        var helpText = element.Properties.HelpText.ValueOrDefault;
        if (!string.IsNullOrEmpty(helpText))
            return helpText;

        var name = element.Properties.Name.ValueOrDefault;
        if (!string.IsNullOrEmpty(name))
            return name;

        var valuePattern = element.Patterns.Value.PatternOrDefault;
        if (valuePattern != null)
            return valuePattern.Value.Value ?? string.Empty;

        return string.Empty;
    }

    private uint WaitForSessionProbe()
    {
        WaitForProbeContains(AutomationIds.MouseCursorSession, "sessionId=");
        return ParseSessionId(ReadProbeValue(AutomationIds.MouseCursorSession));
    }

    private uint WaitForSessionProbeChange(string previousSessionText)
    {
        string currentValue = previousSessionText;
        var matched = SpinWait.SpinUntil(() =>
        {
            currentValue = ReadProbeValue(AutomationIds.MouseCursorSession);
            return currentValue.StartsWith("sessionId=", StringComparison.Ordinal) &&
                   !string.Equals(currentValue, previousSessionText, StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(8));

        matched.Should().BeTrue(
            $"session probe should change from '{previousSessionText}', current='{currentValue}'");
        return ParseSessionId(currentValue);
    }

    private static uint ParseSessionId(string sessionText)
    {
        sessionText.Should().StartWith("sessionId=");
        return uint.Parse(sessionText["sessionId=".Length..]);
    }
}
