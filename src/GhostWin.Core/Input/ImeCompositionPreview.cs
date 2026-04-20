namespace GhostWin.Core.Input;

/// <summary>
/// Renderer-facing IME preview state. This is separate from finalized text input.
/// </summary>
public readonly record struct ImeCompositionPreview(string Text, int CaretOffset, bool IsActive)
{
    public static ImeCompositionPreview None => new(string.Empty, 0, false);

    public static ImeCompositionPreview Active(string? text, int caretOffset)
    {
        var value = text ?? string.Empty;
        if (value.Length == 0) return None;

        return new ImeCompositionPreview(
            value,
            Math.Clamp(caretOffset, 0, value.Length),
            true);
    }

    public static ImeCompositionPreview Active(string? text)
    {
        var value = text ?? string.Empty;
        return Active(value, value.Length);
    }
}

/// <summary>
/// Decides whether WPF preview data should become a renderer composition preview.
/// </summary>
public static class ImeCompositionPreviewPolicy
{
    public static ImeCompositionPreview FromPreviewEvent(
        string? compositionText,
        string? fallbackText,
        bool hasActivePreview)
    {
        if (!string.IsNullOrEmpty(compositionText))
            return ImeCompositionPreview.Active(compositionText);

        if (hasActivePreview && !string.IsNullOrEmpty(fallbackText))
            return ImeCompositionPreview.Active(fallbackText);

        return ImeCompositionPreview.None;
    }

    public static bool ShouldClearOnBackspace(ImeCompositionPreview current)
    {
        if (!current.IsActive || current.Text.Length == 0) return false;

        foreach (var ch in current.Text)
        {
            if (!IsHangulJamo(ch))
                return false;
        }

        return true;
    }

    private static bool IsHangulJamo(char ch)
        => (ch >= '\u1100' && ch <= '\u11FF')
           || (ch >= '\u3130' && ch <= '\u318F')
           || (ch >= '\uA960' && ch <= '\uA97F')
           || (ch >= '\uD7B0' && ch <= '\uD7FF');
}
