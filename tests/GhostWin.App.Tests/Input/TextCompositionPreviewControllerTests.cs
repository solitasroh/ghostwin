using FluentAssertions;
using GhostWin.App.Input;
using GhostWin.Core.Input;
using Xunit;

namespace GhostWin.App.Tests.Input;

public class TextCompositionPreviewControllerTests
{
    [Fact]
    public void UpdateFromPreviewEvent_WithOnlyFallbackTextAndNoActivePreview_DoesNotApply()
    {
        var applied = new List<ImeCompositionPreview>();
        var controller = new TextCompositionPreviewController(
            getActiveSessionId: () => 7,
            applyPreview: (_, preview) => applied.Add(preview));

        controller.UpdateFromPreviewEvent(compositionText: "", fallbackText: "a");

        applied.Should().BeEmpty();
    }

    [Fact]
    public void UpdateFromPreviewEvent_WithCompositionText_AppliesActivePreview()
    {
        var applied = new List<(uint SessionId, ImeCompositionPreview Preview)>();
        var controller = new TextCompositionPreviewController(
            getActiveSessionId: () => 7,
            applyPreview: (sessionId, preview) => applied.Add((sessionId, preview)));

        controller.UpdateFromPreviewEvent(compositionText: "한", fallbackText: "");

        applied.Should().ContainSingle();
        applied[0].SessionId.Should().Be(7);
        applied[0].Preview.Should().Be(ImeCompositionPreview.Active("한"));
    }

    [Fact]
    public void Clear_AfterActivePreview_AppliesInactivePreview()
    {
        var applied = new List<ImeCompositionPreview>();
        var controller = new TextCompositionPreviewController(
            getActiveSessionId: () => 7,
            applyPreview: (_, preview) => applied.Add(preview));

        controller.UpdateFromPreviewEvent(compositionText: "한", fallbackText: "");
        controller.Clear();

        applied.Should().HaveCount(2);
        applied[1].Should().Be(ImeCompositionPreview.None);
    }

    [Fact]
    public void Clear_WhenNoActivePreview_DoesNotApply()
    {
        var applied = new List<ImeCompositionPreview>();
        var controller = new TextCompositionPreviewController(
            getActiveSessionId: () => 7,
            applyPreview: (_, preview) => applied.Add(preview));

        controller.Clear();

        applied.Should().BeEmpty();
    }

    [Fact]
    public void ReconcileBackspace_WhenJamoPreviewDidNotChange_AppliesInactivePreview()
    {
        var applied = new List<ImeCompositionPreview>();
        var controller = new TextCompositionPreviewController(
            getActiveSessionId: () => 7,
            applyPreview: (_, preview) => applied.Add(preview));

        controller.UpdateFromPreviewEvent(compositionText: "ㅎ", fallbackText: "");
        var checkpoint = controller.BeginBackspace();

        controller.ReconcileBackspace(checkpoint);

        applied.Should().HaveCount(2);
        applied[1].Should().Be(ImeCompositionPreview.None);
    }

    [Fact]
    public void ReconcileBackspace_WhenPreviewChanged_DoesNotClear()
    {
        var applied = new List<ImeCompositionPreview>();
        var controller = new TextCompositionPreviewController(
            getActiveSessionId: () => 7,
            applyPreview: (_, preview) => applied.Add(preview));

        controller.UpdateFromPreviewEvent(compositionText: "하", fallbackText: "");
        var checkpoint = controller.BeginBackspace();
        controller.UpdateFromPreviewEvent(compositionText: "ㅎ", fallbackText: "");

        controller.ReconcileBackspace(checkpoint);

        applied.Should().HaveCount(2);
        applied[1].Should().Be(ImeCompositionPreview.Active("ㅎ"));
    }

    [Fact]
    public void BeginBackspace_WhenNoActivePreview_ReturnsNull()
    {
        var applied = new List<ImeCompositionPreview>();
        var controller = new TextCompositionPreviewController(
            getActiveSessionId: () => 7,
            applyPreview: (_, preview) => applied.Add(preview));

        var checkpoint = controller.BeginBackspace();

        checkpoint.Should().BeNull();
        applied.Should().BeEmpty();
    }

    [Fact]
    public void UpdateFromPreviewEvent_WhenStaleCompositionReplaysAfterFinalBackspace_DoesNotReactivatePreview()
    {
        var applied = new List<ImeCompositionPreview>();
        var controller = new TextCompositionPreviewController(
            getActiveSessionId: () => 7,
            applyPreview: (_, preview) => applied.Add(preview));

        controller.UpdateFromPreviewEvent(compositionText: "ㅎ", fallbackText: "");
        var checkpoint = controller.BeginBackspace();
        controller.ReconcileBackspace(checkpoint);

        controller.UpdateFromPreviewEvent(compositionText: "ㅎ", fallbackText: "");

        applied.Should().HaveCount(2);
        applied[0].Should().Be(ImeCompositionPreview.Active("ㅎ"));
        applied[1].Should().Be(ImeCompositionPreview.None);
    }

    [Fact]
    public void ResetBackspaceSuppression_AfterFinalBackspace_AllowsNewMatchingComposition()
    {
        var applied = new List<ImeCompositionPreview>();
        var controller = new TextCompositionPreviewController(
            getActiveSessionId: () => 7,
            applyPreview: (_, preview) => applied.Add(preview));

        controller.UpdateFromPreviewEvent(compositionText: "ㅎ", fallbackText: "");
        var checkpoint = controller.BeginBackspace();
        controller.ReconcileBackspace(checkpoint);

        controller.ResetBackspaceSuppression();
        controller.UpdateFromPreviewEvent(compositionText: "ㅎ", fallbackText: "");

        applied.Should().HaveCount(3);
        applied[2].Should().Be(ImeCompositionPreview.Active("ㅎ"));
    }
}
