using Xunit;

namespace GhostWin.E2E.Tests.Infrastructure;

public sealed class InteractiveFactAttribute : FactAttribute
{
    public InteractiveFactAttribute()
    {
        if (!InteractiveTestGate.IsEnabled)
            Skip = "Set GHOSTWIN_INTERACTIVE_AUTOMATION=1 to run foreground-dependent interactive E2E tests.";
    }
}

public sealed class InteractiveTheoryAttribute : TheoryAttribute
{
    public InteractiveTheoryAttribute()
    {
        if (!InteractiveTestGate.IsEnabled)
            Skip = "Set GHOSTWIN_INTERACTIVE_AUTOMATION=1 to run foreground-dependent interactive E2E tests.";
    }
}
