using System.IO;
using System.Text;
using System.Threading;
using CommunityToolkit.Mvvm.Messaging;
using GhostWin.App.Helpers;
using GhostWin.Core.Events;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;

namespace GhostWin.App.Input;

public enum TerminalExternalTarget
{
    VsCode,
    Cursor,
    Explorer,
}

public readonly record struct TerminalContextMenuState(
    bool CanCopy,
    bool CanOpenVsCode,
    bool CanOpenCursor,
    bool CanOpenExplorer);

public interface ITerminalInputRouter
{
    int WriteMouseEvent(uint sessionId, float xPx, float yPx, uint button, uint action, uint mods);
    void WriteInput(uint sessionId, ReadOnlySpan<byte> data);
    void WriteTextInput(uint sessionId, string text);
    void WriteDroppedFilePaths(uint sessionId, IReadOnlyList<string> paths);
    void HandleCtrlWheel(short delta);
    void HandleShiftWheel(uint sessionId, short delta);
    void HandleUnreportedWheel(uint sessionId, short delta);
    TerminalContextMenuState GetContextMenuState(uint sessionId, bool hasSelection);
    bool CopySelection(uint sessionId, SelectionRange? range);
    bool PasteClipboard(uint sessionId);
    void SelectAll(uint sessionId);
    void ClearScrollback(uint sessionId);
    void OpenExternal(uint sessionId, TerminalExternalTarget target);
}

public sealed class TerminalInputRouter : ITerminalInputRouter
{
    private readonly IEngineService _engine;
    private readonly ISettingsService _settings;
    private readonly ISessionManager _sessions;
    private readonly IMessenger _messenger;

    public TerminalInputRouter(
        IEngineService engine,
        ISettingsService settings,
        ISessionManager sessions,
        IMessenger messenger)
    {
        _engine = engine;
        _settings = settings;
        _sessions = sessions;
        _messenger = messenger;
    }

    public int WriteMouseEvent(uint sessionId, float xPx, float yPx, uint button, uint action, uint mods) =>
        _engine.WriteMouseEvent(sessionId, xPx, yPx, button, action, mods);

    public void WriteInput(uint sessionId, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return;

        _engine.WriteSession(sessionId, data);
        _engine.ScrollViewport(sessionId, int.MaxValue);
    }

    public void WriteTextInput(uint sessionId, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        WriteInput(sessionId, Encoding.UTF8.GetBytes(text));
    }

    public void WriteDroppedFilePaths(uint sessionId, IReadOnlyList<string> paths)
    {
        var text = FormatDroppedFilePaths(paths);
        if (string.IsNullOrEmpty(text))
            return;

        WriteTextInput(sessionId, text);
    }

    public void HandleCtrlWheel(short delta)
    {
        var current = _settings.Current.Terminal.Font.Size;
        var next = Math.Clamp(current + (delta > 0 ? 1.0 : -1.0), 8.0, 32.0);
        if (Math.Abs(next - current) <= 0.5)
            return;

        _settings.Current.Terminal.Font.Size = next;
        _settings.Save();
        _messenger.Send(new SettingsChangedMessage(_settings.Current));
    }

    public void HandleShiftWheel(uint sessionId, short delta)
    {
        _engine.ScrollViewport(sessionId, WheelDeltaToRows(delta));
    }

    public void HandleUnreportedWheel(uint sessionId, short delta)
    {
        _engine.ScrollViewport(sessionId, WheelDeltaToRows(delta));
    }

    public TerminalContextMenuState GetContextMenuState(uint sessionId, bool hasSelection)
    {
        var cwd = GetCwd(sessionId);
        var hasCwd = !string.IsNullOrEmpty(cwd) && Directory.Exists(cwd);
        return new TerminalContextMenuState(
            CanCopy: hasSelection,
            CanOpenVsCode: hasCwd && ExternalLauncher.IsAvailable("code"),
            CanOpenCursor: hasCwd && ExternalLauncher.IsAvailable("cursor"),
            CanOpenExplorer: hasCwd);
    }

    public bool CopySelection(uint sessionId, SelectionRange? range)
    {
        if (range is not { } selection)
            return false;

        var text = _engine.GetSelectedText(
            sessionId,
            selection.Start.Row,
            selection.Start.Col,
            selection.End.Row,
            selection.End.Col);
        if (string.IsNullOrEmpty(text))
            return false;

        for (var retry = 0; retry < 3; retry++)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                if (retry == 2)
                    return false;
                Thread.Sleep(50);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    public bool PasteClipboard(uint sessionId)
    {
        string? text = null;
        for (var retry = 0; retry < 3; retry++)
        {
            try
            {
                if (!System.Windows.Clipboard.ContainsText())
                    return false;
                text = System.Windows.Clipboard.GetText();
                break;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                if (retry == 2)
                    return false;
                Thread.Sleep(50);
            }
        }

        if (string.IsNullOrEmpty(text))
            return false;

        text = FilterForPaste(text);
        if (string.IsNullOrEmpty(text))
            return false;

        if (_engine.GetMode(sessionId, 2004))
        {
            var prefix = "\x1b[200~"u8;
            var suffix = "\x1b[201~"u8;
            var textBytes = Encoding.UTF8.GetBytes(text);
            var payload = new byte[prefix.Length + textBytes.Length + suffix.Length];
            prefix.CopyTo(payload.AsSpan(0));
            textBytes.CopyTo(payload, prefix.Length);
            suffix.CopyTo(payload.AsSpan(prefix.Length + textBytes.Length));
            WriteInput(sessionId, payload);
        }
        else
        {
            text = text.Replace("\r\n", "\r").Replace("\n", "\r");
            WriteInput(sessionId, Encoding.UTF8.GetBytes(text));
        }

        return true;
    }

    public void SelectAll(uint sessionId)
    {
        _engine.SetSelection(sessionId, 0, 0, int.MaxValue, int.MaxValue, true);
    }

    public void ClearScrollback(uint sessionId)
    {
        const byte esc = 0x1B;
        ReadOnlySpan<byte> sequence =
        [
            esc, (byte)'[', (byte)'3', (byte)'J',
            esc, (byte)'[', (byte)'2', (byte)'J',
            esc, (byte)'[', (byte)'H',
        ];
        _engine.WriteSession(sessionId, sequence);
    }

    public void OpenExternal(uint sessionId, TerminalExternalTarget target)
    {
        var cwd = GetCwd(sessionId);
        if (string.IsNullOrEmpty(cwd))
            return;

        _ = target switch
        {
            TerminalExternalTarget.VsCode => ExternalLauncher.TryOpenInVsCode(cwd),
            TerminalExternalTarget.Cursor => ExternalLauncher.TryOpenInCursor(cwd),
            TerminalExternalTarget.Explorer => ExternalLauncher.TryOpenInExplorer(cwd),
            _ => false,
        };
    }

    private string? GetCwd(uint sessionId) =>
        _sessions.Sessions.FirstOrDefault(s => s.Id == sessionId)?.Cwd;

    private static int WheelDeltaToRows(short delta) => delta > 0 ? -3 : 3;

    public static string FormatDroppedFilePaths(IEnumerable<string> paths)
    {
        var quoted = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => $"\"{path.Trim().Replace("\"", "\\\"")}\"");

        return string.Join(" ", quoted);
    }

    public static string FilterForPaste(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c < 0x20 && c != '\t' && c != '\n' && c != '\r')
                continue;
            if (c >= 0x80 && c <= 0x9F)
                continue;
            if (c == 0x7F)
                continue;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
