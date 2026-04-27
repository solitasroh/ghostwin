using System;
using System.Collections.Generic;
using System.Text;
using FluentAssertions;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;
using Xunit;

namespace GhostWin.E2E.Tests.Stubs;

public class OscInjectorTests
{
    [Fact]
    public void InjectOsc22_WritesExpectedEscapeSequence()
    {
        var manager = new RecordingSessionManager();

        OscInjector.InjectOsc22(manager, 7, "text");

        manager.LastSessionId.Should().Be(7);
        Encoding.UTF8.GetString(manager.LastPayload!).Should().Be("\x1b]22;text\x1b\\");
    }

    private sealed class RecordingSessionManager : ISessionManager
    {
        public IReadOnlyList<SessionInfo> Sessions => Array.Empty<SessionInfo>();
        public uint? ActiveSessionId => null;
        public uint? LastSessionId { get; private set; }
        public byte[]? LastPayload { get; private set; }

        public uint CreateSession(ushort cols = 80, ushort rows = 24) => 0;
        public uint CreateSession(string? cwd, ushort cols = 80, ushort rows = 24) => 0;
        public void CloseSession(uint id) { }
        public void ActivateSession(uint id) { }
        public void UpdateTitle(uint id, string title) { }
        public void UpdateCwd(uint id, string cwd) { }
        public void UpdateMouseCursorShape(uint id, int mouseCursorShape) { }

#pragma warning disable CS0618
        public void TestOnlyInjectBytes(uint sessionId, byte[] data)
#pragma warning restore CS0618
        {
            LastSessionId = sessionId;
            LastPayload = data;
        }
    }
}
