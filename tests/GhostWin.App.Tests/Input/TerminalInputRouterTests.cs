using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using GhostWin.App.Input;
using GhostWin.App.Tests.Fakes;
using GhostWin.Core.Events;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;
using Xunit;

namespace GhostWin.App.Tests.Input;

public class TerminalInputRouterTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void HandleCtrlWheel_IncreasesFontSizeSavesAndPublishesSettingsChanged()
    {
        var settings = new FakeSettingsService();
        settings.Current.Terminal.Font.Size = 14;
        var messenger = new WeakReferenceMessenger();
        var receiver = new SettingsReceiver();
        messenger.Register<SettingsChangedMessage>(receiver, (_, message) => receiver.Message = message);
        var router = new TerminalInputRouter(
            new FakeEngineService(),
            settings,
            new EmptySessionManager(),
            messenger);

        router.HandleCtrlWheel(delta: 120);

        settings.Current.Terminal.Font.Size.Should().Be(15);
        settings.SaveCalls.Should().Be(1);
        receiver.Message.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void HandleCtrlWheel_ClampsFontSizeAndDoesNotSaveWhenUnchanged()
    {
        var settings = new FakeSettingsService();
        settings.Current.Terminal.Font.Size = 32;
        var router = new TerminalInputRouter(
            new FakeEngineService(),
            settings,
            new EmptySessionManager(),
            new WeakReferenceMessenger());

        router.HandleCtrlWheel(delta: 120);

        settings.Current.Terminal.Font.Size.Should().Be(32);
        settings.SaveCalls.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void HandleShiftWheel_ScrollsViewportAndDoesNotWriteMouseEvent()
    {
        var engine = new FakeEngineService();
        var router = new TerminalInputRouter(
            engine,
            new FakeSettingsService(),
            new EmptySessionManager(),
            new WeakReferenceMessenger());

        router.HandleShiftWheel(sessionId: 7, delta: -120);

        engine.ScrollViewportCalls.Should().ContainSingle()
            .Which.Should().Be((7u, 3));
        engine.MouseEventCalls.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void HandleUnreportedWheel_ScrollsViewport()
    {
        var engine = new FakeEngineService();
        var router = new TerminalInputRouter(
            engine,
            new FakeSettingsService(),
            new EmptySessionManager(),
            new WeakReferenceMessenger());

        router.HandleUnreportedWheel(sessionId: 7, delta: 120);

        engine.ScrollViewportCalls.Should().ContainSingle()
            .Which.Should().Be((7u, -3));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void WriteMouseEvent_ForwardsToEngineAndReturnsEncoderResult()
    {
        var engine = new FakeEngineService { MouseEventResult = 2 };
        var router = new TerminalInputRouter(
            engine,
            new FakeSettingsService(),
            new EmptySessionManager(),
            new WeakReferenceMessenger());

        var result = router.WriteMouseEvent(7, 10, 20, 4, 0, 1);

        result.Should().Be(2);
        engine.MouseEventCalls.Should().ContainSingle()
            .Which.Should().Be((7u, 10f, 20f, 4u, 0u, 1u));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void WriteInput_WritesBytesAndScrollsToBottom()
    {
        var engine = new FakeEngineService();
        var router = new TerminalInputRouter(
            engine,
            new FakeSettingsService(),
            new EmptySessionManager(),
            new WeakReferenceMessenger());

        router.WriteInput(7, [0x1b, (byte)'[', (byte)'A']);

        engine.WriteSessionCalls.Should().ContainSingle()
            .Which.Payload.ToArray().Should().Equal([0x1b, (byte)'[', (byte)'A']);
        engine.ScrollViewportCalls.Should().ContainSingle()
            .Which.Should().Be((7u, int.MaxValue));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FilterForPaste_RemovesDelAndControlCharactersExceptTabLfCr()
    {
        var filtered = TerminalInputRouter.FilterForPaste("a\u007fb\u0001c\t\n\r");

        filtered.Should().Be("abc\t\n\r");
    }

    private sealed class SettingsReceiver
    {
        public SettingsChangedMessage? Message { get; set; }
    }

    private sealed class EmptySessionManager : ISessionManager
    {
        public IReadOnlyList<SessionInfo> Sessions => [];
        public uint? ActiveSessionId => null;
        public uint CreateSession(ushort cols = 80, ushort rows = 24) => 0;
        public uint CreateSession(string? cwd, ushort cols = 80, ushort rows = 24) => 0;
        public void CloseSession(uint id) { }
        public void ActivateSession(uint id) { }
        public void UpdateTitle(uint id, string title) { }
        public void UpdateCwd(uint id, string cwd) { }
        public void UpdateMouseCursorShape(uint id, int mouseCursorShape) { }
        public void TestOnlyInjectBytes(uint sessionId, byte[] data) { }
    }
}
