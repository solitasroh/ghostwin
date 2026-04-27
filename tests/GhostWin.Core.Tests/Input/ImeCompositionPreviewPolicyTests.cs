using FluentAssertions;
using GhostWin.Core.Input;
using Xunit;

namespace GhostWin.Core.Tests.Input;

public class ImeCompositionPreviewPolicyTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void FromPreviewEvent_WithCompositionText_ReturnsActivePreview()
    {
        var preview = ImeCompositionPreviewPolicy.FromPreviewEvent(
            compositionText: "한",
            fallbackText: "ignored",
            hasActivePreview: false);

        preview.IsActive.Should().BeTrue();
        preview.Text.Should().Be("한");
        preview.CaretOffset.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FromPreviewEvent_WithOnlyFallbackTextAndNoActivePreview_IgnoresEnglishInput()
    {
        var preview = ImeCompositionPreviewPolicy.FromPreviewEvent(
            compositionText: "",
            fallbackText: "a",
            hasActivePreview: false);

        preview.IsActive.Should().BeFalse();
        preview.Text.Should().BeEmpty();
        preview.CaretOffset.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FromPreviewEvent_WithOnlyFallbackTextAndActivePreview_KeepsCompositionAlive()
    {
        var preview = ImeCompositionPreviewPolicy.FromPreviewEvent(
            compositionText: "",
            fallbackText: "하",
            hasActivePreview: true);

        preview.IsActive.Should().BeTrue();
        preview.Text.Should().Be("하");
        preview.CaretOffset.Should().Be(1);
    }

    [Theory]
    [InlineData("abc", -1, 0)]
    [InlineData("abc", 2, 2)]
    [InlineData("abc", 99, 3)]
    [Trait("Category", "Unit")]
    public void Active_ClampsCaretOffset(string text, int caretOffset, int expected)
    {
        var preview = ImeCompositionPreview.Active(text, caretOffset);

        preview.CaretOffset.Should().Be(expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void None_ReturnsInactiveEmptyPreview()
    {
        var preview = ImeCompositionPreview.None;

        preview.IsActive.Should().BeFalse();
        preview.Text.Should().BeEmpty();
        preview.CaretOffset.Should().Be(0);
    }

    [Theory]
    [InlineData("ㅎ", true)]
    [InlineData("ㄴ", true)]
    [InlineData("한", false)]
    [InlineData("a", false)]
    [Trait("Category", "Unit")]
    public void ShouldClearOnBackspace_OnlyClearsActiveHangulJamo(string text, bool expected)
    {
        var preview = ImeCompositionPreview.Active(text);

        ImeCompositionPreviewPolicy.ShouldClearOnBackspace(preview)
            .Should().Be(expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ShouldClearOnBackspace_DoesNotClearInactivePreview()
    {
        ImeCompositionPreviewPolicy.ShouldClearOnBackspace(ImeCompositionPreview.None)
            .Should().BeFalse();
    }
}
