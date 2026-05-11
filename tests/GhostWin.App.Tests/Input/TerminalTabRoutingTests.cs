using FluentAssertions;
using GhostWin.App.Input;
using Xunit;

namespace GhostWin.App.Tests.Input;

public class TerminalTabRoutingTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ShouldLetWpfHandlePlainTab_ReturnsFalse_WhenTerminalChildHasWin32Focus()
    {
        var result = TerminalTabRouting.ShouldLetWpfHandlePlainTab(
            hasFocusedTerminalHost: true,
            isPaneTreeFocused: false,
            isTerminalChildFocused: true,
            isTerminalInputActive: false,
            hasWpfChromeFocus: true);

        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ShouldLetWpfHandlePlainTab_ReturnsTrue_WhenChromeHasFocus()
    {
        var result = TerminalTabRouting.ShouldLetWpfHandlePlainTab(
            hasFocusedTerminalHost: true,
            isPaneTreeFocused: false,
            isTerminalChildFocused: false,
            isTerminalInputActive: false,
            hasWpfChromeFocus: true);

        result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ShouldLetWpfHandlePlainTab_ReturnsFalse_WhenPaneTreeHasWpfFocus()
    {
        var result = TerminalTabRouting.ShouldLetWpfHandlePlainTab(
            hasFocusedTerminalHost: true,
            isPaneTreeFocused: true,
            isTerminalChildFocused: false,
            isTerminalInputActive: false,
            hasWpfChromeFocus: false);

        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ShouldLetWpfHandlePlainTab_ReturnsTrue_WhenNoTerminalFocusExists()
    {
        var result = TerminalTabRouting.ShouldLetWpfHandlePlainTab(
            hasFocusedTerminalHost: false,
            isPaneTreeFocused: false,
            isTerminalChildFocused: false,
            isTerminalInputActive: false,
            hasWpfChromeFocus: false);

        result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ShouldLetWpfHandlePlainTab_ReturnsFalse_WhenTerminalInputIsActiveEvenIfWpfFocusLooksLikeChrome()
    {
        var result = TerminalTabRouting.ShouldLetWpfHandlePlainTab(
            hasFocusedTerminalHost: true,
            isPaneTreeFocused: false,
            isTerminalChildFocused: false,
            isTerminalInputActive: true,
            hasWpfChromeFocus: true);

        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ShouldRoutePlainSpaceToTerminal_ReturnsTrue_WhenTerminalInputIsActive()
    {
        var result = TerminalTabRouting.ShouldRoutePlainSpaceToTerminal(
            isPaneTreeFocused: false,
            isTerminalChildFocused: false,
            isTerminalInputActive: true);

        result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ShouldRoutePlainSpaceToTerminal_ReturnsFalse_WhenOnlyChromeHasFocus()
    {
        var result = TerminalTabRouting.ShouldRoutePlainSpaceToTerminal(
            isPaneTreeFocused: false,
            isTerminalChildFocused: false,
            isTerminalInputActive: false);

        result.Should().BeFalse();
    }
}
