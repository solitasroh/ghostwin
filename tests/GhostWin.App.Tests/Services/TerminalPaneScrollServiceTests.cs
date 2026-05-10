using FluentAssertions;
using GhostWin.App.Services;
using GhostWin.App.Tests.Fakes;
using GhostWin.Core.Interfaces;
using Xunit;

namespace GhostWin.App.Tests.Services;

public class TerminalPaneScrollServiceTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void GetState_WhenPolicyNever_HidesWithoutQueryingScrollback()
    {
        var engine = new CountingScrollbackEngine();
        var settings = new FakeSettingsService();
        settings.Current.Terminal.Scrollbar = "never";
        var service = new TerminalPaneScrollService(engine, settings);

        var state = service.GetState(sessionId: 7);

        state.IsVisible.Should().BeFalse();
        engine.GetScrollbackInfoCalls.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetState_WhenPolicyAlwaysAndNoScrollback_ShowsZeroRange()
    {
        var engine = new FakeEngineService();
        engine.ScrollbackBySession[7] = new ScrollbackInfo(24, 24, 0, 0);
        var settings = new FakeSettingsService();
        settings.Current.Terminal.Scrollbar = "always";
        var service = new TerminalPaneScrollService(engine, settings);

        var state = service.GetState(sessionId: 7);

        state.IsVisible.Should().BeTrue();
        state.Maximum.Should().Be(0);
        state.Value.Should().Be(0);
        state.ViewportSize.Should().Be(24);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetState_ProjectsScrollbarValueFromViewportOffset()
    {
        var engine = new FakeEngineService();
        engine.ScrollbackBySession[7] = new ScrollbackInfo(
            TotalRows: 124,
            ViewportRows: 24,
            ScrollbackRows: 100,
            ViewportOffsetFromBottom: 25);
        var service = new TerminalPaneScrollService(engine, new FakeSettingsService());

        var state = service.GetState(sessionId: 7);

        state.IsVisible.Should().BeTrue();
        state.Maximum.Should().Be(100);
        state.Value.Should().Be(75);
        state.LargeChange.Should().Be(24);
        state.ViewportSize.Should().Be(24);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ScrollTo_ConvertsBarValueToEngineDelta()
    {
        var engine = new FakeEngineService();
        engine.ScrollbackBySession[7] = new ScrollbackInfo(
            TotalRows: 124,
            ViewportRows: 24,
            ScrollbackRows: 100,
            ViewportOffsetFromBottom: 25);
        var service = new TerminalPaneScrollService(engine, new FakeSettingsService());

        service.ScrollTo(sessionId: 7, maximum: 100, newValue: 90);

        engine.ScrollViewportCalls.Should().ContainSingle()
            .Which.Should().Be((7u, 15));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ForceContextMenu_ReturnsSettingsPolicy()
    {
        var settings = new FakeSettingsService();
        settings.Current.Terminal.ForceContextMenu = true;
        var service = new TerminalPaneScrollService(new FakeEngineService(), settings);

        service.ForceContextMenu.Should().BeTrue();
    }

    private sealed class CountingScrollbackEngine : FakeEngineService
    {
        public int GetScrollbackInfoCalls { get; private set; }

        public override ScrollbackInfo? GetScrollbackInfo(uint sessionId)
        {
            GetScrollbackInfoCalls++;
            return base.GetScrollbackInfo(sessionId);
        }
    }
}
