// BC-06 spike — minimal test to confirm the WinExe + UseWPF project can be
// referenced from a test library and that referenced types resolve at compile
// time. If this builds and xunit discovers the test, the WinExe restriction
// documented in Backlog follow-up #5 does not actually block regression tests.

using FluentAssertions;
using GhostWin.App.ViewModels;
using Xunit;

namespace GhostWin.App.Tests;

public class SmokeTest
{
    [Fact]
    public void PublicViewModel_is_reachable_from_test_assembly()
    {
        // WinExe + UseWPF project CAN be referenced from a test library on
        // .NET 10 — the only caveat is that `internal` types require
        // InternalsVisibleTo (standard .NET idiom). See
        // `docs/00-research/wpf-winexe-test-reference.md` for full findings.
        typeof(TerminalTabViewModel).FullName
            .Should().Be("GhostWin.App.ViewModels.TerminalTabViewModel");
    }
}
