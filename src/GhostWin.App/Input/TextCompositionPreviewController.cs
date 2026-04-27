using GhostWin.App.Diagnostics;
using GhostWin.Core.Input;

namespace GhostWin.App.Input;

/// <summary>
/// Coordinates WPF composition preview events with the renderer-facing engine port.
/// Finalized text input remains owned by MainWindow's TextInput handler.
/// </summary>
public sealed class TextCompositionPreviewController
{
    private readonly Func<uint?> _getActiveSessionId;
    private readonly Action<uint, ImeCompositionPreview> _applyPreview;
    private ImeCompositionPreview _current = ImeCompositionPreview.None;
    private ImeCompositionPreview? _suppressedBackspaceReplay;
    private int _revision;

    public TextCompositionPreviewController(
        Func<uint?> getActiveSessionId,
        Action<uint, ImeCompositionPreview> applyPreview)
    {
        _getActiveSessionId = getActiveSessionId;
        _applyPreview = applyPreview;
    }

    public bool HasActivePreview => _current.IsActive;

    public void UpdateFromPreviewEvent(string? compositionText, string? fallbackText)
    {
        var next = ImeCompositionPreviewPolicy.FromPreviewEvent(
            compositionText,
            fallbackText,
            _current.IsActive);

        ImeDiag.Log("UpdateFromPreviewEvent",
            $"comp={ImeDiag.Escape(compositionText)} fb={ImeDiag.Escape(fallbackText)} " +
            $"hasActive={_current.IsActive} -> next={ImeDiag.Escape(next.Text)} active={next.IsActive} caret={next.CaretOffset} " +
            $"current={ImeDiag.Escape(_current.Text)} suppressed={(_suppressedBackspaceReplay.HasValue ? ImeDiag.Escape(_suppressedBackspaceReplay.Value.Text) : "<none>")}");

        if (_suppressedBackspaceReplay is { } suppressed)
        {
            if (next.Equals(suppressed))
            {
                ImeDiag.Log("UpdateFromPreviewEvent.SUPPRESS_MATCH",
                    $"next == suppressed {ImeDiag.Escape(suppressed.Text)} -> ignore + clear suppression");
                _suppressedBackspaceReplay = null;
                return;
            }

            ImeDiag.Log("UpdateFromPreviewEvent.SUPPRESS_MISMATCH",
                $"next={ImeDiag.Escape(next.Text)} != suppressed={ImeDiag.Escape(suppressed.Text)} -> clear suppression + apply");
            _suppressedBackspaceReplay = null;
        }

        Apply(next);
    }

    public void Clear()
    {
        ImeDiag.Log("Clear", $"current={ImeDiag.Escape(_current.Text)} active={_current.IsActive}");
        _suppressedBackspaceReplay = null;
        if (!_current.IsActive) return;
        Apply(ImeCompositionPreview.None);
    }

    public void ResetBackspaceSuppression()
    {
        if (_suppressedBackspaceReplay is { } s)
            ImeDiag.Log("ResetBackspaceSuppression", $"cleared suppressed={ImeDiag.Escape(s.Text)}");
        _suppressedBackspaceReplay = null;
    }

    public TextCompositionBackspaceCheckpoint? BeginBackspace()
    {
        var result = _current.IsActive
            ? new TextCompositionBackspaceCheckpoint(_revision, _current)
            : (TextCompositionBackspaceCheckpoint?)null;
        ImeDiag.Log("BeginBackspace",
            $"hasActive={_current.IsActive} current={ImeDiag.Escape(_current.Text)} rev={_revision} -> checkpoint={(result.HasValue ? "yes" : "null")}");
        return result;
    }

    public void ReconcileBackspace(TextCompositionBackspaceCheckpoint? checkpoint)
    {
        if (checkpoint is not { } value)
        {
            ImeDiag.Log("ReconcileBackspace.NO_CHECKPOINT");
            return;
        }
        if (!_current.IsActive)
        {
            ImeDiag.Log("ReconcileBackspace.SKIP_INACTIVE",
                $"checkpoint={ImeDiag.Escape(value.Preview.Text)} rev={value.Revision}");
            return;
        }
        if (_revision != value.Revision)
        {
            ImeDiag.Log("ReconcileBackspace.SKIP_REV_CHANGED",
                $"checkpoint.rev={value.Revision} current.rev={_revision} current={ImeDiag.Escape(_current.Text)}");
            return;
        }
        if (!_current.Equals(value.Preview))
        {
            ImeDiag.Log("ReconcileBackspace.SKIP_PREVIEW_CHANGED",
                $"checkpoint={ImeDiag.Escape(value.Preview.Text)} current={ImeDiag.Escape(_current.Text)}");
            return;
        }

        var shouldClear = ImeCompositionPreviewPolicy.ShouldClearOnBackspace(_current);
        ImeDiag.Log("ReconcileBackspace.POLICY",
            $"current={ImeDiag.Escape(_current.Text)} ShouldClearOnBackspace={shouldClear}");

        if (shouldClear)
        {
            ImeDiag.Log("ReconcileBackspace.CLEAR",
                $"suppressing replay of {ImeDiag.Escape(_current.Text)}");
            _suppressedBackspaceReplay = _current;
            Apply(ImeCompositionPreview.None);
        }
    }

    private void Apply(ImeCompositionPreview next)
    {
        if (next.Equals(_current)) return;
        if (_getActiveSessionId() is not { } sessionId)
        {
            ImeDiag.Log("Apply.NO_ACTIVE_SESSION", $"next={ImeDiag.Escape(next.Text)}");
            return;
        }

        ImeDiag.Log("Apply",
            $"sid={sessionId} {ImeDiag.Escape(_current.Text)} -> {ImeDiag.Escape(next.Text)} active={next.IsActive} caret={next.CaretOffset} rev {_revision}->{_revision + 1}");
        _applyPreview(sessionId, next);
        _current = next;
        _revision++;
    }
}

public readonly record struct TextCompositionBackspaceCheckpoint(
    int Revision,
    ImeCompositionPreview Preview);
